using Iemas.Application.Common.Interfaces;
using Iemas.Application.Escalations;
using Iemas.Application.Inquiries;
using Iemas.Application.Operations;
using Iemas.Domain.Cases;
using Iemas.Domain.Escalations;
using Iemas.Domain.Identity;
using Iemas.Domain.Operations;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Escalations;

/// <summary>Escalation levels, recipient fallback, duplicate prevention and SMTP-failure handling.</summary>
public class EscalationReliabilityTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Records every send and answers from a script (default: success).</summary>
    private sealed class ScriptedSender : IEscalationEmailSender
    {
        public List<OutboundEmail> Calls { get; } = new();
        public Func<int, OutboundEmailResult> Script { get; set; } = n => new OutboundEmailResult(true, $"<m{n}@test>", null);
        public bool Throw { get; set; }

        public Task<OutboundEmailResult> SendAsync(OutboundEmail email, CancellationToken cancellationToken)
        {
            Calls.Add(email);
            if (Throw) throw new InvalidOperationException("smtp exploded");
            return Task.FromResult(Script(Calls.Count));
        }
    }

    private static OutboundEmailResult Fail(string why = "SMTP server unreachable") => new(false, null, why);

    private readonly TestDbContext _db = TestDbContext.CreateNew();
    private readonly ScriptedSender _sender = new();
    private readonly Employee _supervisor = new() { FullName = "Sue Supervisor", Email = "sue@sawo.com" };
    private readonly Employee _deptManager = new() { FullName = "Dave Manager", Email = "dave@sawo.com" };
    private readonly Employee _groupA = new() { FullName = "Gina Group", Email = "gina@sawo.com" };
    private readonly Employee _groupB = new() { FullName = "Gus Group", Email = "gus@sawo.com" };
    private readonly Employee _agent;
    private readonly Case _case;
    private readonly EscalationPolicy _policy;

    public EscalationReliabilityTests()
    {
        var dept = new Department { Name = "Sales", ManagerEmployeeId = _deptManager.Id };
        _agent = new Employee { FullName = "Alan Agent", Email = "alan@sawo.com", SupervisorEmployeeId = _supervisor.Id, DepartmentId = dept.Id };
        _db.Employees.AddRange(_supervisor, _deptManager, _groupA, _groupB, _agent);
        _db.Departments.Add(dept);

        var group = new EscalationGroup { Name = "Directors" };
        group.Members.Add(new EscalationGroupMember { EscalationGroupId = group.Id, EmployeeId = _groupA.Id });
        group.Members.Add(new EscalationGroupMember { EscalationGroupId = group.Id, EmployeeId = _groupB.Id });
        _db.EscalationGroups.Add(group);

        _policy = new EscalationPolicy { Name = "Default", IsDefault = true, Enabled = true, MaximumLevel = 3 };
        _policy.Levels.Add(new EscalationLevel { EscalationPolicyId = _policy.Id, Level = 1, RecipientType = EscalationRecipientType.EmployeeSupervisor });
        _policy.Levels.Add(new EscalationLevel { EscalationPolicyId = _policy.Id, Level = 2, RecipientType = EscalationRecipientType.DepartmentManager });
        _policy.Levels.Add(new EscalationLevel { EscalationPolicyId = _policy.Id, Level = 3, RecipientType = EscalationRecipientType.SpecificGroup, SpecificGroupId = group.Id });
        _db.EscalationPolicies.Add(_policy);

        _case = new Case
        {
            CaseNumber = "C-100", Subject = "Heater", CustomerEmailAddress = "anna@customer.example", OwnerEmployeeId = _agent.Id,
            Stage = InquiryStage.AwaitingInitialResponse, WorkStatus = CaseWorkStatus.ActionRequired,
            FirstEmailReceivedAt = T0.AddDays(-1), StageChangedAt = T0.AddDays(-1),
        };
        _db.Cases.Add(_case);
        _db.SaveChanges();
        ShortTimings.Apply(_db);
    }

    public void Dispose() => _db.Dispose();

    private EscalationService Service(IEscalationEmailSender? sender = null) => new(_db, null, sender ?? _sender);

    private Task<EscalationService.SupervisionEscalationResult> Escalate(DateTimeOffset now, EscalationService? s = null) =>
        (s ?? Service()).EscalateForSupervisionAsync(_case, "No initial response", now, CancellationToken.None);

    private Task<List<EscalationEvent>> Executed() =>
        _db.EscalationEvents.Where(e => e.Outcome == EscalationOutcome.Executed).OrderBy(e => e.Level).ToListAsync();

    // ---------------------------------------------------------------- levels

    [Fact]
    public async Task Level1_GoesToTheSupervisor_AndIsRecordedAsSent()
    {
        var result = await Escalate(T0);

        Assert.Equal(EscalationOutcome.Executed, result.Outcome);
        var email = Assert.Single(_sender.Calls);
        Assert.Equal("sue@sawo.com", Assert.Single(email.To).Address);
        var e = Assert.Single(await Executed());
        Assert.Equal(1, e.Level);
        Assert.Equal("Sent", e.DeliveryResult);
        Assert.Equal(0, e.RetryCount);
        Assert.NotNull(e.EmailMessageId);
        Assert.Equal(_agent.Id, _case.OwnerEmployeeId); // escalation never transfers ownership
    }

    [Fact]
    public async Task EachFurtherEscalation_GoesOneLevelUp_SupervisorThenManagerThenGroup()
    {
        await Escalate(T0);
        await Escalate(T0.AddDays(1));
        await Escalate(T0.AddDays(2));

        var events = await Executed();
        Assert.Equal(new[] { 1, 2, 3 }, events.Select(e => e.Level));
        Assert.Equal("sue@sawo.com", _sender.Calls[0].To.Single().Address);
        Assert.Equal("dave@sawo.com", _sender.Calls[1].To.Single().Address);
        Assert.Equal(new[] { "gina@sawo.com", "gus@sawo.com" }, _sender.Calls[2].To.Select(t => t.Address).OrderBy(a => a));
    }

    [Fact]
    public async Task AtTheTopLevel_NothingIsSentAgain_ALevelNeverEscalatesTwice()
    {
        for (var i = 0; i < 3; i++) await Escalate(T0.AddDays(i));
        var extra = await Escalate(T0.AddDays(3));
        await Escalate(T0.AddDays(4));

        Assert.Equal(EscalationOutcome.Skipped, extra.Outcome);
        Assert.Equal(3, _sender.Calls.Count);
        Assert.Equal(3, (await Executed()).Count);
        Assert.Equal(new[] { 1, 2, 3 }, (await Executed()).Select(e => e.Level));
    }

    [Fact]
    public async Task PolicyMaximumLevel_IsHonoured()
    {
        _policy.MaximumLevel = 1;
        await _db.SaveChangesAsync();

        await Escalate(T0);
        var second = await Escalate(T0.AddDays(1));

        Assert.Equal(EscalationOutcome.Skipped, second.Outcome);
        Assert.Single(_sender.Calls);
    }

    [Fact]
    public async Task ASecondRunWithAStaleCase_CannotEscalateTwoLevelsInARow()
    {
        Func<DateTimeOffset, DateTimeOffset> oneDay = prev => prev.AddDays(1);
        var first = await Service().EscalateForSupervisionAsync(_case, "r", T0, CancellationToken.None, oneDay);
        var second = await Service().EscalateForSupervisionAsync(_case, "r", T0.AddSeconds(5), CancellationToken.None, oneDay);

        Assert.Equal(EscalationOutcome.Executed, first.Outcome);
        Assert.Equal(EscalationOutcome.Skipped, second.Outcome);
        Assert.True(second.RetryLater);
        Assert.Single(_sender.Calls);
        Assert.Single(await Executed());

        var third = await Service().EscalateForSupervisionAsync(_case, "r", T0.AddDays(1), CancellationToken.None, oneDay);
        Assert.Equal(EscalationOutcome.Executed, third.Outcome);
        Assert.Equal(new[] { 1, 2 }, (await Executed()).Select(e => e.Level));
    }

    // ---------------------------------------------------------------- recipient fallback

    [Fact]
    public async Task NoSupervisor_FallsBackToTheNextLevel_AndRecordsTheUnresolvedOne()
    {
        _agent.SupervisorEmployeeId = null;
        await _db.SaveChangesAsync();

        var result = await Escalate(T0);

        Assert.Equal(EscalationOutcome.Executed, result.Outcome);
        Assert.Equal("dave@sawo.com", Assert.Single(_sender.Calls).To.Single().Address);
        Assert.Equal(2, Assert.Single(await Executed()).Level);
        var unresolved = Assert.Single(await _db.EscalationEvents.Where(e => e.Outcome == EscalationOutcome.RecipientUnresolved).ToListAsync());
        Assert.Equal(1, unresolved.Level);
        Assert.Contains(await _db.CaseEvents.ToListAsync(), ev => ev.Detail!.Contains("Fell back to level 2"));
    }

    [Fact]
    public async Task InactiveSupervisorAndManager_FallBackToTheGroup_SkippingInactiveMembers()
    {
        _supervisor.IsActive = false;
        _deptManager.IsActive = false;
        _groupB.IsActive = false;
        await _db.SaveChangesAsync();

        await Escalate(T0);

        var email = Assert.Single(_sender.Calls);
        Assert.Equal("gina@sawo.com", Assert.Single(email.To).Address);
        Assert.Equal(3, Assert.Single(await Executed()).Level);
    }

    [Fact]
    public async Task NobodyResolvable_IsRecordedAsUnresolved_NothingSent_NoExecutedLevel()
    {
        _agent.SupervisorEmployeeId = null;
        _agent.DepartmentId = null;
        _groupA.IsActive = false;
        _groupB.IsActive = false;
        await _db.SaveChangesAsync();

        var result = await Escalate(T0);

        Assert.Equal(EscalationOutcome.RecipientUnresolved, result.Outcome);
        Assert.Empty(_sender.Calls);
        Assert.Empty(await Executed());
        Assert.Equal(3, await _db.EscalationEvents.CountAsync(e => e.Outcome == EscalationOutcome.RecipientUnresolved));
    }

    [Fact]
    public async Task RecipientWithBlankEmail_CountsAsUnresolved()
    {
        _supervisor.Email = " ";
        await _db.SaveChangesAsync();

        await Escalate(T0);

        Assert.Equal("dave@sawo.com", Assert.Single(_sender.Calls).To.Single().Address);
    }

    [Fact]
    public async Task NoPolicy_IsASkip_NotACrash()
    {
        _policy.Enabled = false;
        await _db.SaveChangesAsync();

        var result = await Escalate(T0);

        Assert.Equal(EscalationOutcome.Skipped, result.Outcome);
        Assert.Empty(_sender.Calls);
    }

    [Fact]
    public async Task IgnoredCustomer_IsNeverEscalated()
    {
        _db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.IgnoredSenders, Value = "customer.example" });
        await _db.SaveChangesAsync();

        var result = await Escalate(T0);

        Assert.Equal(EscalationOutcome.Skipped, result.Outcome);
        Assert.Empty(_sender.Calls);
        Assert.Empty(await Executed());
    }

    // ---------------------------------------------------------------- SMTP failure

    [Fact]
    public async Task FailedSend_IsRecordedAsFailed_NeverAsSent()
    {
        _sender.Script = _ => Fail("Connection refused");

        var result = await Escalate(T0);

        var e = Assert.Single(await Executed());
        Assert.Equal("Failed", e.DeliveryResult);
        Assert.Equal("Connection refused", e.FailureReason);
        Assert.Null(e.EmailMessageId);
        Assert.Contains("email not sent", result.Detail);
        Assert.Contains(await _db.CaseEvents.ToListAsync(), ev => ev.Detail!.Contains("NOT sent"));
        Assert.DoesNotContain(await _db.CaseEvents.ToListAsync(), ev => ev.Detail == "Escalation email sent.");
    }

    [Fact]
    public async Task FailedSend_StillCountsTheLevel_SoTheNextEscalationMovesUp_NotRepeats()
    {
        _sender.Script = _ => Fail();
        await Escalate(T0);
        _sender.Script = n => new OutboundEmailResult(true, "<ok>", null);

        await Escalate(T0.AddDays(1));

        Assert.Equal(new[] { 1, 2 }, (await Executed()).Select(e => e.Level));
    }

    [Fact]
    public async Task SenderThatThrows_IsRecordedAsFailure_AndDoesNotPropagate()
    {
        _sender.Throw = true;

        var result = await Escalate(T0);

        Assert.Equal(EscalationOutcome.Executed, result.Outcome);
        var e = Assert.Single(await Executed());
        Assert.Equal("Failed", e.DeliveryResult);
        Assert.Contains("smtp exploded", e.FailureReason);
    }

    [Fact]
    public async Task NoSenderConfigured_IsRecordedAsFailed()
    {
        var service = new EscalationService(_db, null, null);
        await service.EscalateForSupervisionAsync(_case, "r", T0, CancellationToken.None);

        var e = Assert.Single(await Executed());
        Assert.Equal("Failed", e.DeliveryResult);
        Assert.Contains("No escalation email sender", e.FailureReason);
    }

    [Fact]
    public async Task Retry_WaitsForTheBackoff_ThenSendsOnce_AndStops()
    {
        _sender.Script = _ => Fail();
        await Escalate(T0);
        _sender.Script = n => new OutboundEmailResult(true, "<retry>", null);

        Assert.Equal(0, await Service().RetryFailedEscalationEmailsAsync(T0.AddMinutes(4), CancellationToken.None));
        Assert.Single(_sender.Calls);

        Assert.Equal(1, await Service().RetryFailedEscalationEmailsAsync(T0.AddMinutes(5), CancellationToken.None));
        Assert.Equal(2, _sender.Calls.Count);

        var e = Assert.Single(await Executed());
        Assert.Equal("Sent", e.DeliveryResult);
        Assert.Equal("<retry>", e.EmailMessageId);
        Assert.Null(e.FailureReason);
        Assert.Equal(1, e.RetryCount);

        // Once sent it is never sent again.
        for (var m = 6; m < 600; m += 7) await Service().RetryFailedEscalationEmailsAsync(T0.AddMinutes(m), CancellationToken.None);
        Assert.Equal(2, _sender.Calls.Count);
    }

    [Fact]
    public async Task Retry_IsBounded_AndNeverATightLoop()
    {
        _sender.Script = _ => Fail("still down");
        await Escalate(T0);

        // Supervision ticks every minute for a day.
        for (var m = 1; m <= 24 * 60; m++) await Service().RetryFailedEscalationEmailsAsync(T0.AddMinutes(m), CancellationToken.None);

        Assert.Equal(1 + EscalationService.MaxEmailRetries, _sender.Calls.Count); // first attempt + 3 retries, no more
        var e = Assert.Single(await Executed());
        Assert.Equal("Failed", e.DeliveryResult);
        Assert.Equal(EscalationService.MaxEmailRetries, e.RetryCount);
        Assert.Equal("still down", e.FailureReason);
    }

    [Fact]
    public async Task Retry_SpacingFollowsTheBackoff_5_15_60_Minutes()
    {
        _sender.Script = _ => Fail();
        await Escalate(T0);
        var attempts = new List<DateTimeOffset>();
        for (var m = 1; m <= 300; m++)
        {
            var before = _sender.Calls.Count;
            await Service().RetryFailedEscalationEmailsAsync(T0.AddMinutes(m), CancellationToken.None);
            if (_sender.Calls.Count > before) attempts.Add(T0.AddMinutes(m));
        }
        Assert.Equal(new[] { T0.AddMinutes(5), T0.AddMinutes(20), T0.AddMinutes(80) }, attempts);
    }

    [Fact]
    public async Task Retry_AbandonsTheEmail_WhenTheInquiryWasResolvedInTheMeantime()
    {
        _sender.Script = _ => Fail();
        await Escalate(T0);
        _case.Stage = InquiryStage.Resolved;
        _case.WorkStatus = CaseWorkStatus.Completed;
        await _db.SaveChangesAsync();

        await Service().RetryFailedEscalationEmailsAsync(T0.AddMinutes(10), CancellationToken.None);

        Assert.Single(_sender.Calls);
        var e = Assert.Single(await Executed());
        Assert.Equal("Cancelled", e.DeliveryResult);
    }

    [Fact]
    public async Task Retry_RecoversAnEscalationLeftPendingByACrash()
    {
        _db.EscalationEvents.Add(new EscalationEvent
        {
            CaseId = _case.Id, EscalationPolicyId = _policy.Id, Level = 1, Trigger = "r", RecipientType = EscalationRecipientType.EmployeeSupervisor,
            Outcome = EscalationOutcome.Executed, DeliveryResult = "Pending", OccurredAt = T0, LastDeliveryAttemptAt = T0,
        });
        await _db.SaveChangesAsync();

        Assert.Equal(0, await Service().RetryFailedEscalationEmailsAsync(T0.AddMinutes(1), CancellationToken.None));
        Assert.Equal(1, await Service().RetryFailedEscalationEmailsAsync(T0.AddMinutes(6), CancellationToken.None));

        Assert.Equal("sue@sawo.com", Assert.Single(_sender.Calls).To.Single().Address);
        Assert.Equal("Sent", (await Executed()).Single().DeliveryResult);
    }

    [Fact]
    public async Task Retry_IgnoresOldFailures_AndSkippedAndUnresolvedRows()
    {
        _db.EscalationEvents.AddRange(
            new EscalationEvent { CaseId = _case.Id, Level = 1, Trigger = "old", Outcome = EscalationOutcome.Executed, DeliveryResult = "Failed", OccurredAt = T0.AddDays(-5) },
            new EscalationEvent { CaseId = _case.Id, Level = 2, Trigger = "skip", Outcome = EscalationOutcome.Skipped, DeliveryResult = "Failed", OccurredAt = T0 },
            new EscalationEvent { CaseId = _case.Id, Level = 3, Trigger = "unres", Outcome = EscalationOutcome.RecipientUnresolved, DeliveryResult = "Failed", OccurredAt = T0 });
        await _db.SaveChangesAsync();

        Assert.Equal(0, await Service().RetryFailedEscalationEmailsAsync(T0.AddHours(3), CancellationToken.None));
        Assert.Empty(_sender.Calls);
    }

    [Fact]
    public async Task Retry_WithoutASender_DoesNothing() =>
        Assert.Equal(0, await new EscalationService(_db, null, null).RetryFailedEscalationEmailsAsync(T0, CancellationToken.None));

    [Fact]
    public void RetryBackoff_GrowsAndIsBounded()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), EscalationService.RetryBackoff(0));
        Assert.Equal(TimeSpan.FromMinutes(15), EscalationService.RetryBackoff(1));
        Assert.Equal(TimeSpan.FromMinutes(60), EscalationService.RetryBackoff(2));
        Assert.Equal(TimeSpan.FromMinutes(60), EscalationService.RetryBackoff(99));
    }

    // ---------------------------------------------------------------- through the supervision engine

    private async Task<InquirySupervisionService> ReadyToEscalate()
    {
        _case.StageChangedAt = T0.AddDays(-5);
        _case.EpisodeDeadlineMissedAt = T0.AddDays(-4);
        _case.EpisodeRemindersSent = 2;
        _case.EpisodeLastReminderAt = T0.AddDays(-4);
        _case.ResolutionDueAt = T0.AddDays(30);
        _case.SupervisionCycleStartedAt = _case.FirstEmailReceivedAt;
        await _db.SaveChangesAsync();
        return new InquirySupervisionService(_db, Service());
    }

    [Fact]
    public async Task Supervision_SmtpDown_EscalatesOnce_RetriesBounded_NoDuplicateLevels()
    {
        var supervision = await ReadyToEscalate();
        _sender.Script = _ => Fail("SMTP down");
        var monday = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

        // A first escalation, then supervision ticks every minute for two business hours.
        var now = monday.AddDays(1);
        for (var m = 0; m < 120; m++) await supervision.RunAsync(100, CancellationToken.None, now.AddMinutes(m));

        var events = await Executed();
        Assert.Single(events);                                   // one escalation, not one per minute
        Assert.Equal(1, events[0].Level);
        Assert.Equal(1, _case.EpisodeEscalations);
        Assert.InRange(_sender.Calls.Count, 2, 1 + EscalationService.MaxEmailRetries);
    }

    [Fact]
    public async Task Supervision_StopsEscalatingOnceTheInquiryIsResolved()
    {
        var supervision = await ReadyToEscalate();
        var now = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

        _case.Stage = InquiryStage.Resolved;
        _case.WorkStatus = CaseWorkStatus.Completed;
        await _db.SaveChangesAsync();
        for (var m = 0; m < 30; m++) await supervision.RunAsync(100, CancellationToken.None, now.AddMinutes(m));

        Assert.Empty(_sender.Calls);
        Assert.Empty(await _db.EscalationEvents.ToListAsync());
    }

    [Fact]
    public async Task Supervision_TwoRunsAtTheSameInstant_EscalateOnce()
    {
        var supervision = await ReadyToEscalate();
        var other = new InquirySupervisionService(_db, Service());
        var now = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

        await supervision.RunAsync(100, CancellationToken.None, now);
        await other.RunAsync(100, CancellationToken.None, now);

        Assert.Single(await Executed());
        Assert.Single(_sender.Calls);
    }

    [Fact]
    public async Task Supervision_CustomerSilenceAlone_NeverEscalates_TheAgentIsTold_Instead()
    {
        var supervision = await ReadyToEscalate();
        _case.Stage = InquiryStage.AwaitingCustomer;
        _case.StageChangedAt = T0;
        _case.EpisodeRemindersSent = 0;
        _case.EpisodeDeadlineMissedAt = null;
        _case.EpisodeLastReminderAt = null;
        await _db.SaveChangesAsync();

        // Three calendar days of silence, checked every few hours: no management email, no escalation row.
        for (var h = 0; h <= 3 * 24; h += 3) await supervision.RunAsync(100, CancellationToken.None, T0.AddHours(h));

        Assert.Empty(_sender.Calls);
        Assert.Empty(await _db.EscalationEvents.ToListAsync());
        // The agent was told a follow-up may be needed, and now owes the next step.
        Assert.Equal(InquiryStage.Inactive, _case.Stage);
    }
}
