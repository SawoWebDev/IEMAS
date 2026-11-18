using Iemas.Application.Agents.Dtos;
using Iemas.Application.Cases;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.EmailClassification;
using Iemas.Application.EmailClassification.Dtos;
using Iemas.Application.Escalations;
using Iemas.Application.Inquiries;
using Iemas.Application.Notifications;
using Iemas.Application.Reminders;
using Iemas.Domain.Agents;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Escalations;
using Iemas.Domain.Notifications;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using static Iemas.Tests.Inquiries.InquiryHarness;

namespace Iemas.Tests.Inquiries;

/// <summary>Whole-pipeline production scenarios (intake → AI → Case → sync → supervision) over the shared harness.</summary>
public class ProductionScenarioEndToEndTests
{
    private const string Agent1 = "agent1@sawo.com";
    private const string Customer = "anna@customer.example";

    private sealed class ThrowingAssessor : IAiResolutionAssessor
    {
        public int Calls;
        public Task<ResolutionAssessmentResult> AssessAsync(ResolutionAssessmentRequest request, string modelIdentifier, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("assessor exploded");
        }
    }

    private sealed class FailingSender : IEscalationEmailSender
    {
        public int Calls;
        public bool Fail = true;
        public Task<OutboundEmailResult> SendAsync(OutboundEmail email, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Fail ? new OutboundEmailResult(false, null, "SMTP unreachable") : new OutboundEmailResult(true, "<ok@test>", null));
        }
    }

    private static InquirySupervisionService SupervisionWith(InquiryHarness h, IAiResolutionAssessor assessor, IEscalationEmailSender sender)
    {
        var encryption = new PassThroughEncryptionService();
        var notifications = new NotificationService(h.Db, h.PopUps);
        var conversation = new InquiryConversationService(h.Db, notifications);
        return new InquirySupervisionService(h.Db, new EscalationService(h.Db, notifications, sender), notifications,
            new ResolutionAssessmentService(h.Db, assessor, conversation, new AgentReplyProtector(encryption)));
    }

    // 1
    [Fact]
    public async Task FollowUpOnExistingThread_JoinsTheCase_AndNotifiesTheOwner()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var reply = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));

        h.Receive(Agent1, Customer, "Re: Heater problem", "Tried it. Still nothing.", Day(0, 11), inReplyTo: reply);
        await h.RunAsync(Day(0, 11, 5));

        Assert.Equal(1, await h.Db.Cases.CountAsync());
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.CustomerFollowUpReceived, c.Stage);
        Assert.Equal(3, await h.Db.CaseEmails.CountAsync(ce => ce.CaseId == c.Id));
        Assert.Equal(2, h.PopUpTitlesFor(Agent1).Count(t => t == "New Email"));
        Assert.All(h.PopUps.Sent, p => Assert.Equal(h.Agents[Agent1].Id, p.EmployeeId));
    }

    // 2
    [Fact]
    public async Task DuplicateInboundEmail_SameMessageId_JobsRerun_OneCaseOneNotification()
    {
        using var h = new InquiryHarness(Agent1);
        h.Receive(Agent1, Customer, "Request for quotation", "I would like to request a quotation for 20 heaters. How much per unit?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));

        // The mailbox is re-listed from scratch (cursor lost): the very same message is fetched again.
        foreach (var s in await h.Db.EmailSyncStates.ToListAsync()) { s.LastSeenUid = null; s.LastUidValidity = null; }
        await h.Db.SaveChangesAsync();
        await h.RunAsync(Monday0900.AddMinutes(2));
        await h.RunAsync(Monday0900.AddMinutes(3));

        Assert.Equal(1, await h.Db.EmailMessages.CountAsync());
        Assert.Equal(1, await h.Db.Cases.CountAsync());
        Assert.Equal(1, await h.Db.CaseEmails.CountAsync());
        Assert.Equal(1, await h.Db.Notifications.CountAsync(n => n.Type == NotificationType.NewEmail));
    }

    // 3
    [Fact]
    public async Task AgentReplySentJustBeforeConfirm_IsStoredBySyncOnConfirm()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));

        h.Send(Agent1, Customer, "Re: Heater problem", "Reset the breaker, that fixes it.", Day(0, 10), inReplyTo: first);
        var c = await h.CaseAsync(Agent1, Customer);
        var result = await h.AgentActions.SubmitActionAsync(Guid.NewGuid(), h.Agents[Agent1].Id,
            new SubmitCaseActionRequest("confirm-3", c.Id, CaseActionType.ConfirmResolved, null, Day(0, 10, 1)), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);

        await h.RunAsync(Day(0, 10, 5));

        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.Resolved, c.Stage);
        Assert.Equal(1, await h.Db.CaseEmails.CountAsync(ce => ce.CaseId == c.Id && ce.Role == ConversationRole.Agent));
        Assert.Empty(h.ManagementEmail.Sent);
    }

    // 4
    [Fact]
    public async Task AssessorThrows_InquiryKeepsBeingSupervised_NoCrash()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var fix = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        h.Receive(Agent1, Customer, "Re: Heater problem", "Thank you, it works now!", Day(0, 11), inReplyTo: fix);
        await h.RunWithoutSentFolderAsync(Day(0, 11, 1)); // intake only, so the case changes with a fresh message

        var assessor = new ThrowingAssessor();
        var supervision = SupervisionWith(h, assessor, h.ManagementEmail);
        await supervision.RunAsync(1000, CancellationToken.None, Day(0, 11, 5));
        await supervision.RunAsync(1000, CancellationToken.None, Day(1, 11, 5));

        var c = await h.CaseAsync(Agent1, Customer);
        Assert.NotEqual(InquiryStage.Resolved, c.Stage);
        Assert.NotEqual(CaseWorkStatus.Completed, c.WorkStatus);

        // Supervision still works: the customer is silent for 3+ days after the agent's last turn -> normal timers still fire.
        await h.RunAsync(Day(4, 12, 0));
        Assert.NotEqual(CaseWorkStatus.Completed, (await h.CaseAsync(Agent1, Customer)).WorkStatus);
    }

    // 5
    [Fact]
    public async Task CustomerThanksAlone_DoesNotYieldPotentiallyResolved()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var ack = h.Send(Agent1, Customer, "Re: Heater problem", "We have received your request and will look into it.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));

        h.Receive(Agent1, Customer, "Re: Heater problem", "Thanks!", Day(0, 10, 30), inReplyTo: ack);
        await h.RunAsync(Day(0, 10, 35));

        var c = await h.CaseAsync(Agent1, Customer);
        Assert.NotEqual(InquiryStage.PotentiallyResolved, c.Stage);
        Assert.NotEqual(CaseWorkStatus.PotentiallyResolved, c.WorkStatus);
        Assert.DoesNotContain("Please confirm: resolved?", h.PopUpTitlesFor(Agent1));
    }

    // 6
    private static async Task<(ClassificationReviewService Service, Guid EmailId)> QueueForReviewAsync(InquiryHarness h)
    {
        h.Ai.Behavior = (request, model, _, _) => Task.FromResult(new ClassificationAttemptResult(true, "OpenRouter", model,
            new ClassificationResponse(true, "PROJECT_INQUIRY", true, true, "LOW", 0.5, "Unsure whether this is a real inquiry.", true), null, 3));
        h.Receive(Agent1, "lee@prospect.example", "Upcoming project",
            "I visited your website and wanted to know whether your team could support us on an upcoming project.", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));

        var email = await h.Db.EmailMessages.SingleAsync();
        Assert.Equal(EmailProcessingStatus.ReviewRequired, email.ProcessingStatus);
        Assert.Empty(await h.Db.Cases.ToListAsync());

        var notifications = new NotificationService(h.Db, h.PopUps);
        var workflow = new CaseWorkflowService(h.Db, new CaseMatchingService(h.Db), new ReminderSchedulingService(h.Db), notifications,
            new InquiryConversationService(h.Db, notifications));
        return (new ClassificationReviewService(h.Db, workflow, new NoOpAuditService(), new FakeCurrentUserService(null, "reviewer@sawo.com")), email.Id);
    }

    [Fact]
    public async Task ReviewRequired_ReviewerApproves_CreatesCaseAndNotifiesOwner()
    {
        using var h = new InquiryHarness(Agent1);
        var (service, id) = await QueueForReviewAsync(h);

        var result = await service.ReviewAsync(id, new ReviewClassificationRequest("Approve", null, null, null, null), CancellationToken.None);

        Assert.True(result.Succeeded);
        var c = await h.CaseAsync(Agent1, "lee@prospect.example");
        Assert.Equal(h.Agents[Agent1].Id, c.OwnerEmployeeId);
        Assert.Contains("New Email", h.PopUpTitlesFor(Agent1));
    }

    [Fact]
    public async Task ReviewRequired_ReviewerRejects_NoCase()
    {
        using var h = new InquiryHarness(Agent1);
        var (service, id) = await QueueForReviewAsync(h);

        var result = await service.ReviewAsync(id, new ReviewClassificationRequest("Reject", null, null, null, null), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(await h.Db.Cases.ToListAsync());
        Assert.Empty(h.PopUps.Sent);
        await h.RunAsync(Day(0, 10)); // the jobs do not resurrect it
        Assert.Empty(await h.Db.Cases.ToListAsync());
    }

    // 7
    [Fact]
    public async Task ResolutionRejected_ReturnsCaseToSupervision_AndTimersStillFire()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var fix = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        h.Receive(Agent1, Customer, "Re: Heater problem", "Thank you, it works now!", Day(0, 11), inReplyTo: fix);
        await h.RunAsync(Day(0, 11, 5));
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.PotentiallyResolved, c.Stage);

        var result = await h.AgentActions.SubmitActionAsync(Guid.NewGuid(), h.Agents[Agent1].Id,
            new SubmitCaseActionRequest("reject-7", c.Id, CaseActionType.RejectResolution, "Spare part still to ship", Day(0, 12)), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);

        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.CustomerFollowUpReceived, c.Stage);
        Assert.NotEqual(CaseWorkStatus.Completed, c.WorkStatus);

        // The customer is waiting on the agent again: the response timer runs and reminds.
        await h.RunAsync(Day(1, 13, 0));
        await h.RunAsync(Day(1, 15, 0));
        Assert.True(await h.Db.Reminders.CountAsync() > 0);
        Assert.NotEqual(InquiryStage.PotentiallyResolved, (await h.CaseAsync(Agent1, Customer)).Stage);
    }

    // 8
    [Fact]
    public async Task EscalationSmtpFailure_IsRecorded_Retried_AndNotSentTwice()
    {
        using var h = new InquiryHarness(Agent1);
        h.Receive(Agent1, Customer, "Request for quotation", "I would like to request a quotation. What is the price?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        await h.RunAsync(Day(0, 13, 0));
        await h.RunAsync(Day(0, 15, 0));
        await h.RunAsync(Day(0, 17, 0));

        var sender = new FailingSender();
        var supervision = SupervisionWith(h, new FakeResolutionAssessor(), sender);
        await supervision.RunAsync(1000, CancellationToken.None, Day(1, 10, 0));

        var ev = await h.Db.EscalationEvents.SingleAsync(e => e.Outcome == EscalationOutcome.Executed);
        Assert.Equal("Failed", ev.DeliveryResult);
        Assert.Equal("SMTP unreachable", ev.FailureReason);
        Assert.Equal(1, sender.Calls);

        sender.Fail = false;
        await supervision.RunAsync(1000, CancellationToken.None, Day(1, 10, 6)); // after the 5-minute backoff
        for (var m = 7; m < 120; m += 3) await supervision.RunAsync(1000, CancellationToken.None, Day(1, 10, 0).AddMinutes(m));

        h.Db.ChangeTracker.Clear();
        ev = await h.Db.EscalationEvents.SingleAsync(e => e.Outcome == EscalationOutcome.Executed && e.Level == 1);
        Assert.Equal("Sent", ev.DeliveryResult);
        Assert.Equal(2, sender.Calls); // one failure + one successful retry, never a third
    }

    // 9
    [Fact]
    public async Task SupervisionAndSyncRunTwiceBackToBack_NoDuplicateEscalationsNotificationsOrMessages()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Request for quotation", "I would like to request a quotation. What is the price?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        h.Send(Agent1, Customer, "Re: Request for quotation", "We will look into it.", Day(0, 9, 30), inReplyTo: first);
        await h.RunAsync(Day(0, 9, 35));

        // Idle long enough that supervision has work to do; run the whole cycle twice at the same instant.
        var at = Day(4, 10, 5);
        await h.RunAsync(at);
        var messages = await h.Db.EmailMessages.CountAsync();
        var caseEmails = await h.Db.CaseEmails.CountAsync();
        var notifications = await h.Db.Notifications.CountAsync();
        var popUps = h.PopUps.Sent.Count;
        var reminders = await h.Db.Reminders.CountAsync();
        var escalations = await h.Db.EscalationEvents.CountAsync();
        var mails = h.ManagementEmail.Sent.Count;

        await h.RunAsync(at);
        await h.Sync.RunAsync(500, CancellationToken.None, at);
        await h.Supervision.RunAsync(1000, CancellationToken.None, at);

        Assert.Equal(messages, await h.Db.EmailMessages.CountAsync());
        Assert.Equal(caseEmails, await h.Db.CaseEmails.CountAsync());
        Assert.Equal(notifications, await h.Db.Notifications.CountAsync());
        Assert.Equal(popUps, h.PopUps.Sent.Count);
        Assert.Equal(reminders, await h.Db.Reminders.CountAsync());
        Assert.Equal(escalations, await h.Db.EscalationEvents.CountAsync());
        Assert.Equal(mails, h.ManagementEmail.Sent.Count);
    }
}
