using Iemas.Application.Inquiries;
using Iemas.Domain.Cases;
using Iemas.Domain.Notifications;
using Iemas.Domain.Reminders;
using Xunit;

namespace Iemas.Tests.Inquiries;

/// <summary>Timer rules of the supervision engine: spacing, ladders, stage handling, silence never escalates.</summary>
public class SupervisionHardeningRulesTests
{
    private static readonly BusinessCalendar Cal = BusinessCalendar.Default; // Mon-Fri 08-17 UTC
    private static readonly SupervisionSettings S = TestSupport.ShortTimings.Settings;

    /// <summary>Day 2 = Monday 2 Nov 2026.</summary>
    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    private static Case NewCase(InquiryStage stage, DateTimeOffset stageChangedAt) => new()
    {
        CaseNumber = "C-1", Stage = stage, StageChangedAt = stageChangedAt, FirstEmailReceivedAt = stageChangedAt,
        SupervisionCycleStartedAt = stageChangedAt, ResolutionDueAt = stageChangedAt.AddDays(60),
        WorkStatus = CaseWorkStatus.ActionRequired,
    };

    private static SupervisionAction? Eval(Case c, DateTimeOffset now, SupervisionSettings? s = null, BusinessCalendar? cal = null) =>
        SupervisionRules.Evaluate(c, s ?? S, cal ?? Cal, now);

    // ---------------------------------------------------------------- terminal / stopped

    [Theory]
    [InlineData(InquiryStage.Resolved)]
    [InlineData(InquiryStage.Closed)]
    public void ResolvedOrClosed_IsNeverSupervised_AtAnyTime(InquiryStage stage)
    {
        var c = NewCase(stage, At(2, 9));
        c.OverdueSince = At(3, 9);
        foreach (var days in new[] { 0, 1, 5, 30, 400 })
        {
            Assert.Null(Eval(c, At(2, 9).AddDays(days)));
        }
    }

    [Theory]
    [InlineData(CaseWorkStatus.Completed)]
    [InlineData(CaseWorkStatus.Cancelled)]
    public void CompletedOrCancelledCase_IsNeverSupervised_EvenIfTheStageIsStillOpen(CaseWorkStatus status)
    {
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));
        c.WorkStatus = status;
        Assert.Null(Eval(c, At(2, 9).AddDays(30)));
    }

    [Fact]
    public void FirstEvaluation_RecordsTheResolutionTarget()
    {
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));
        c.ResolutionDueAt = null;
        var a = Eval(c, At(2, 9));
        Assert.Equal(SupervisionActionKind.SetResolutionDue, a!.Kind);
        Assert.Equal(At(9, 9), a.Value); // 5 business days
    }

    // ---------------------------------------------------------------- initial response ladder

    [Fact]
    public void InitialResponse_FullLadder_ExactBoundaries()
    {
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));

        Assert.Null(Eval(c, At(2, 12, 59)));
        var missed = Eval(c, At(2, 13, 0));
        Assert.Equal(SupervisionActionKind.DeadlineMissed, missed!.Kind);
        c.EpisodeDeadlineMissedAt = missed.Value;

        Assert.Null(Eval(c, At(2, 14, 59)));
        var r1 = Eval(c, At(2, 15, 0));
        Assert.Equal(SupervisionActionKind.Reminder, r1!.Kind);
        Assert.Equal(NotificationType.FirstReminder, r1.Notification);
        c.EpisodeRemindersSent = 1; c.EpisodeLastReminderAt = At(2, 15, 0);

        Assert.Null(Eval(c, At(2, 16, 59)));
        var r2 = Eval(c, At(2, 17, 0));
        Assert.Equal(SupervisionActionKind.Reminder, r2!.Kind);
        Assert.Equal(NotificationType.EscalationWarning, r2.Notification); // the last reminder before escalation warns
        c.EpisodeRemindersSent = 2; c.EpisodeLastReminderAt = At(2, 17, 0);

        // Evening and night count for nothing; the interval resumes next morning: 17:00 + 2 business hours = Tue 10:00.
        Assert.Null(Eval(c, At(3, 9, 59)));
        var esc = Eval(c, At(3, 10, 0));
        Assert.Equal(SupervisionActionKind.Escalate, esc!.Kind);
        Assert.False(esc.IsOverdueEscalation);
    }

    [Fact]
    public void ExactlyTwoReminders_ThenEscalation_NeverAThird()
    {
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));
        c.EpisodeDeadlineMissedAt = At(2, 13);
        c.EpisodeRemindersSent = 2; c.EpisodeLastReminderAt = At(2, 17);
        foreach (var now in new[] { At(3, 10), At(3, 12), At(4, 9) })
        {
            Assert.Equal(SupervisionActionKind.Escalate, Eval(c, now)!.Kind);
        }
    }

    [Fact]
    public void RemindersAreTwoBusinessHoursApart_EvenWhenTheFirstWasLate()
    {
        // The engine was down: reminder 1 only went out at Tue 09:30 although it was due Monday 15:00.
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));
        c.EpisodeDeadlineMissedAt = At(2, 13);
        c.EpisodeRemindersSent = 1; c.EpisodeLastReminderAt = At(3, 9, 30);

        Assert.Null(Eval(c, At(3, 9, 31)));   // measured from the deadline it would already be due; it must wait
        Assert.Null(Eval(c, At(3, 11, 29)));
        Assert.Equal(SupervisionActionKind.Reminder, Eval(c, At(3, 11, 30))!.Kind);
    }

    [Fact]
    public void EscalationWaitsOneIntervalAfterTheLastReminderActuallySent()
    {
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));
        c.EpisodeDeadlineMissedAt = At(2, 13);
        c.EpisodeRemindersSent = 2; c.EpisodeLastReminderAt = At(4, 9);   // 2nd reminder very late
        Assert.Null(Eval(c, At(4, 10, 59)));
        Assert.Equal(SupervisionActionKind.Escalate, Eval(c, At(4, 11, 0))!.Kind);
    }

    [Fact]
    public void NoRemindersConfigured_EscalatesOneIntervalAfterTheDeadline()
    {
        var s = S with { RemindersBeforeEscalation = 0 };
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));
        c.EpisodeDeadlineMissedAt = At(2, 13);
        Assert.Null(Eval(c, At(2, 14, 59), s));
        Assert.Equal(SupervisionActionKind.Escalate, Eval(c, At(2, 15, 0), s)!.Kind);
    }

    [Fact]
    public void ConfigurableIntervalAndFirstResponseTime_AreHonoured()
    {
        var s = S with { FirstResponseBusinessHours = 1, ReminderIntervalBusinessHours = 3 };
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));
        Assert.Equal(SupervisionActionKind.DeadlineMissed, Eval(c, At(2, 10), s)!.Kind);
        c.EpisodeDeadlineMissedAt = At(2, 10);
        Assert.Null(Eval(c, At(2, 12, 59), s));
        Assert.Equal(SupervisionActionKind.Reminder, Eval(c, At(2, 13), s)!.Kind);
    }

    [Fact]
    public void ArrivalOnFridayAfternoon_DeadlineFallsOnMonday()
    {
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(6, 16));
        Assert.Null(Eval(c, At(6, 17)));
        Assert.Null(Eval(c, At(8, 12)));              // Sunday
        Assert.Null(Eval(c, At(9, 10, 59)));          // 1h Fri + 3h Mon = Monday 11:00
        Assert.Equal(SupervisionActionKind.DeadlineMissed, Eval(c, At(9, 11))!.Kind);
    }

    [Fact]
    public void Holiday_PushesTheDeadline()
    {
        var cal = new BusinessCalendar(TimeZoneInfo.Utc, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, new[] { new DateOnly(2026, 11, 9) });
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(6, 16));
        Assert.Null(Eval(c, At(9, 12), cal: cal));
        Assert.Null(Eval(c, At(10, 10, 59), cal: cal));
        Assert.Equal(SupervisionActionKind.DeadlineMissed, Eval(c, At(10, 11), cal: cal)!.Kind);
    }

    // ---------------------------------------------------------------- later escalation levels

    [Fact]
    public void NextEscalationLevel_OnlyAfterOneBusinessDayFromTheLast()
    {
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));
        c.EpisodeDeadlineMissedAt = At(2, 13);
        c.EpisodeRemindersSent = 2; c.EpisodeLastReminderAt = At(2, 17);
        c.EpisodeEscalations = 1; c.EpisodeLastEscalatedAt = At(3, 10);

        Assert.Null(Eval(c, At(3, 17)));
        Assert.Null(Eval(c, At(4, 9, 59)));
        Assert.Equal(SupervisionActionKind.Escalate, Eval(c, At(4, 10))!.Kind);
    }

    [Fact]
    public void NextEscalationLevel_FromFridayWaitsUntilMonday()
    {
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));
        c.EpisodeDeadlineMissedAt = At(2, 13);
        c.EpisodeRemindersSent = 2; c.EpisodeLastReminderAt = At(2, 17);
        c.EpisodeEscalations = 1; c.EpisodeLastEscalatedAt = At(6, 10);

        Assert.Null(Eval(c, At(7, 12)));
        Assert.Null(Eval(c, At(9, 9, 59)));
        Assert.Equal(SupervisionActionKind.Escalate, Eval(c, At(9, 10))!.Kind);
    }

    // ---------------------------------------------------------------- other stages

    [Fact]
    public void CustomerFollowUp_UsesTheIdleBusinessDay_AndTheSameLadder()
    {
        var c = NewCase(InquiryStage.CustomerFollowUpReceived, At(2, 14));
        Assert.Null(Eval(c, At(3, 13, 59)));
        Assert.Equal(SupervisionActionKind.DeadlineMissed, Eval(c, At(3, 14))!.Kind);
    }

    [Fact]
    public void InactiveStage_RequestsUpdatesAndEscalatesIfIgnored()
    {
        var c = NewCase(InquiryStage.Inactive, At(2, 9));
        c.EpisodeDeadlineMissedAt = At(3, 9);
        var r = Eval(c, At(3, 11));
        Assert.Equal(SupervisionActionKind.Reminder, r!.Kind);
        Assert.Equal(NotificationType.UpdateRequested, r.Notification);
    }

    [Fact]
    public void AwaitingInternal_AsksTheAgentForAnUpdate_AfterOneBusinessDay()
    {
        var c = NewCase(InquiryStage.AwaitingInternal, At(2, 9));
        Assert.Null(Eval(c, At(3, 8, 59)));
        var a = Eval(c, At(3, 9));
        Assert.Equal(SupervisionActionKind.WaitExpired, a!.Kind);
        Assert.Equal(NotificationType.UpdateRequested, a.Notification);
    }

    [Fact]
    public void AwaitingInternal_RecentInternalActivityRestartsTheWait()
    {
        var c = NewCase(InquiryStage.AwaitingInternal, At(2, 9));
        c.LastInternalActivityAt = At(3, 8);
        Assert.Null(Eval(c, At(3, 15)));
        Assert.Equal(SupervisionActionKind.WaitExpired, Eval(c, At(4, 8))!.Kind);
    }

    // ---------------------------------------------------------------- customer silence never escalates

    [Theory]
    [InlineData(InquiryStage.InitialResponseReceived)]
    [InlineData(InquiryStage.AgentFollowUpSent)]
    [InlineData(InquiryStage.AwaitingCustomer)]
    public void CustomerSilence_NeverEscalates_OnlyNotifiesTheAgentAfterThreeCalendarDays(InquiryStage stage)
    {
        var c = NewCase(stage, At(2, 9));
        Assert.Null(Eval(c, At(5, 8, 59)));
        var a = Eval(c, At(5, 9));
        Assert.Equal(SupervisionActionKind.WaitExpired, a!.Kind);
        Assert.Equal(ReminderTrigger.CustomerSilent, a.Trigger);

        // Over weeks of silence in this stage: never a management escalation.
        for (var days = 0; days <= 90; days++)
        {
            Assert.NotEqual(SupervisionActionKind.Escalate, Eval(c, At(2, 9).AddDays(days))?.Kind);
        }
    }

    [Fact]
    public void CustomerSilence_WithOverdueResolution_StillNeverEscalates()
    {
        var c = NewCase(InquiryStage.AwaitingCustomer, At(2, 9));
        c.ResolutionDueAt = At(9, 9);
        c.OverdueSince = At(9, 9);
        for (var days = 8; days <= 60; days++)
        {
            Assert.NotEqual(SupervisionActionKind.Escalate, Eval(c, At(2, 9).AddDays(days))?.Kind);
        }
    }

    [Fact]
    public void OverdueWhileCustomerOwesReply_IsOnlyMarkedOverdue()
    {
        var c = NewCase(InquiryStage.AwaitingCustomer, At(2, 9));
        c.ResolutionDueAt = At(9, 9);
        var a = Eval(c, At(9, 9));
        Assert.Equal(SupervisionActionKind.MarkOverdue, a!.Kind);
    }

    [Fact]
    public void OverdueWhileAgentOwesNextStep_EscalatesOnceOneIntervalAfterTheNotice()
    {
        var c = NewCase(InquiryStage.AgentFollowUpSent, At(2, 9));
        c.Stage = InquiryStage.Inactive;
        c.StageChangedAt = At(9, 9);
        c.ResolutionDueAt = At(9, 9);
        c.OverdueSince = At(9, 9);
        c.EpisodeDeadlineMissedAt = null;
        // Overdue escalation fires only when no stage ladder step is due; here the idle deadline is a day away.
        Assert.Null(Eval(c, At(9, 10, 59)));
        var a = Eval(c, At(9, 11));
        Assert.Equal(SupervisionActionKind.Escalate, a!.Kind);
        Assert.True(a.IsOverdueEscalation);

        c.OverdueEscalatedAt = At(9, 11);
        Assert.Null(Eval(c, At(9, 12)));
    }

    // ---------------------------------------------------------------- potentially resolved

    [Fact]
    public void PotentiallyResolved_NeverEscalatesOrCloses_OnlyAsksTheAgentToConfirm()
    {
        var c = NewCase(InquiryStage.PotentiallyResolved, At(2, 9));
        var first = Eval(c, At(5, 9));
        Assert.Equal(SupervisionActionKind.ConfirmationFollowUp, first!.Kind);

        c.EpisodeWaitNoticeAt = At(5, 9);
        c.EpisodeDeadlineMissedAt = At(6, 9);
        c.EpisodeRemindersSent = 2; c.EpisodeLastReminderAt = At(9, 9);
        for (var days = 0; days <= 60; days++)
        {
            Assert.NotEqual(SupervisionActionKind.Escalate, Eval(c, At(9, 9).AddDays(days))?.Kind);
        }
    }

    [Fact]
    public void PotentiallyResolved_DoesNothingBeforeTheWaitPeriod()
    {
        var c = NewCase(InquiryStage.PotentiallyResolved, At(2, 9));
        Assert.Null(Eval(c, At(5, 8, 59)));
    }

    // ---------------------------------------------------------------- one action per run, no loops

    [Fact]
    public void AfterAnActionIsApplied_TheSameInstantYieldsNothingFurther()
    {
        var c = NewCase(InquiryStage.AwaitingInitialResponse, At(2, 9));
        c.EpisodeDeadlineMissedAt = At(2, 13);
        c.EpisodeRemindersSent = 1; c.EpisodeLastReminderAt = At(2, 15);
        Assert.Null(Eval(c, At(2, 15)));
        Assert.Null(Eval(c, At(2, 15, 1)));
    }

    [Fact]
    public void OverdueIsMarkedOnce()
    {
        var c = NewCase(InquiryStage.InitialResponseReceived, At(2, 9));
        c.ResolutionDueAt = At(3, 9);
        Assert.Equal(SupervisionActionKind.MarkOverdue, Eval(c, At(3, 9))!.Kind);
        c.OverdueSince = At(3, 9);
        Assert.NotEqual(SupervisionActionKind.MarkOverdue, Eval(c, At(3, 10))?.Kind);
    }
}
