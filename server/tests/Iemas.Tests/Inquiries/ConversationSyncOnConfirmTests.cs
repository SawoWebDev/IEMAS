using Iemas.Application.Agents.Dtos;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;
using static Iemas.Tests.Inquiries.InquiryHarness;

namespace Iemas.Tests.Inquiries;

/// <summary>
/// Recommendation 1 - an agent's last reply must never be lost because the inquiry was confirmed
/// before the 5-minute sync read the Sent folder. Event-triggered sync, a grace window for closed
/// inquiries, non-fatal failures, idempotency.
/// </summary>
public class ConversationSyncOnConfirmTests
{
    private const string Agent1 = "agent1@sawo.com";
    private const string Customer = "anna@customer.example";

    private static async Task<(string First, Case Case)> OpenInquiryAsync(InquiryHarness h)
    {
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        return (first, await h.CaseAsync(Agent1, Customer));
    }

    private static Task<Iemas.Application.Common.Result<Iemas.Application.Agents.Dtos.CaseActionResultDto>> AgentAsync(
        InquiryHarness h, Guid caseId, CaseActionType type, string requestId, DateTimeOffset at) =>
        h.AgentActions.SubmitActionAsync(Guid.NewGuid(), h.Agents[Agent1].Id,
            new SubmitCaseActionRequest(requestId, caseId, type, null, at), CancellationToken.None);

    [Fact]
    public async Task ConfirmResolved_StoresTheAgentsLastReply_ThatTheScheduledSyncHadNotReadYet()
    {
        using var h = new InquiryHarness(Agent1);
        var (first, c) = await OpenInquiryAsync(h);

        // The agent answers and confirms two minutes later; the Sent folder has not been synced in between.
        h.Send(Agent1, Customer, "Re: Heater problem", "Reset the breaker; that fixes it.", Day(0, 10), inReplyTo: first);
        Assert.Equal(0, await h.Db.EmailMessages.CountAsync(m => m.Direction == MessageDirection.Outbound));

        var result = await AgentAsync(h, c.Id, CaseActionType.ConfirmResolved, "confirm-1", Day(0, 10, 2));
        Assert.True(result.Succeeded, result.Error);

        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.Resolved, c.Stage);
        var stored = await h.Db.EmailMessages.SingleAsync(m => m.Direction == MessageDirection.Outbound);
        Assert.Equal(Day(0, 10), stored.ReceivedAt);
        Assert.Equal(Day(0, 10), c.FirstAgentResponseAt);
        Assert.Equal(Day(0, 10), c.LastAgentMessageAt);
        Assert.Equal(ConversationRole.Agent, (await h.Db.CaseEmails.SingleAsync(ce => ce.EmailMessageId == stored.Id)).Role);
        // The reply is part of the conversation as it was BEFORE the confirmation: the resolution stands.
        Assert.Equal(CaseWorkStatus.Completed, c.WorkStatus);
    }

    [Fact]
    public async Task ConfirmResolved_StillSucceeds_WhenTheMailboxCannotBeRead()
    {
        using var h = new InquiryHarness(Agent1);
        var (first, c) = await OpenInquiryAsync(h);
        h.Send(Agent1, Customer, "Re: Heater problem", "Reset the breaker.", Day(0, 10), inReplyTo: first);

        // Break the mailbox: the (fake) provider throws for an unknown login.
        var account = await h.Db.EmailAccounts.SingleAsync();
        account.Username = "broken@nowhere.example";
        await h.Db.SaveChangesAsync();

        var result = await AgentAsync(h, c.Id, CaseActionType.ConfirmResolved, "confirm-1", Day(0, 10, 2));

        Assert.True(result.Succeeded, result.Error);
        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.Resolved, c.Stage);
        Assert.Equal(0, await h.Db.EmailMessages.CountAsync(m => m.Direction == MessageDirection.Outbound));
    }

    [Fact]
    public async Task LateSentReply_AfterConfirmation_IsStoredWithinTheGraceWindow_AndNeverReopensTheInquiry()
    {
        using var h = new InquiryHarness(Agent1);
        var (first, c) = await OpenInquiryAsync(h);
        Assert.True((await AgentAsync(h, c.Id, CaseActionType.ConfirmResolved, "confirm-1", Day(0, 10))).Succeeded);

        // Hours later the mail client uploads the agent's closing message, dated shortly after the confirm.
        h.Send(Agent1, Customer, "Re: Heater problem", "Glad that is sorted. Closing this.", Day(0, 10, 5), inReplyTo: first);
        await h.RunAsync(Day(0, 16));

        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(1, await h.Db.EmailMessages.CountAsync(m => m.Direction == MessageDirection.Outbound));
        Assert.Equal(InquiryStage.Resolved, c.Stage);
        Assert.Equal(Day(0, 10, 5), c.LastAgentMessageAt);
    }

    [Fact]
    public async Task ClosedInquiry_IsNoLongerSynced_AfterTheGraceWindow()
    {
        using var h = new InquiryHarness(Agent1);
        var (first, c) = await OpenInquiryAsync(h);
        Assert.True((await AgentAsync(h, c.Id, CaseActionType.ConfirmResolved, "confirm-1", Day(0, 10))).Succeeded);

        h.Send(Agent1, Customer, "Re: Heater problem", "Two days later.", Day(2, 10), inReplyTo: first);
        await h.RunAsync(Day(2, 11));

        Assert.Equal(0, await h.Db.EmailMessages.CountAsync(m => m.Direction == MessageDirection.Outbound));
    }

    [Fact]
    public async Task Sync_IsIdempotent_ByEventAndByJob_NoDuplicateMessagesOrLinks()
    {
        using var h = new InquiryHarness(Agent1);
        var (first, c) = await OpenInquiryAsync(h);
        h.Send(Agent1, Customer, "Re: Heater problem", "Reset the breaker.", Day(0, 10), inReplyTo: first);

        await h.Sync.SyncCaseAsync(c.Id, CancellationToken.None, Day(0, 10, 1));
        await h.Sync.SyncCaseAsync(c.Id, CancellationToken.None, Day(0, 10, 2));
        await h.Sync.RunAsync(100, CancellationToken.None, Day(0, 10, 3));
        await h.Sync.RunAsync(100, CancellationToken.None, Day(0, 10, 4));

        Assert.Equal(1, await h.Db.EmailMessages.CountAsync(m => m.Direction == MessageDirection.Outbound));
        Assert.Equal(2, await h.Db.CaseEmails.CountAsync()); // the customer's email and the agent's reply
        Assert.Equal(1, await h.Db.ReplyVerificationAttempts.CountAsync(a => a.Outcome == ReplyVerificationOutcome.VerifiedReply));
    }

    [Fact]
    public async Task WaitingForCustomer_StoresTheAgentsReplyFirst_SoTheStageBuildsOnTheRealConversation()
    {
        using var h = new InquiryHarness(Agent1);
        var (first, c) = await OpenInquiryAsync(h);
        h.Send(Agent1, Customer, "Re: Heater problem", "Please send a photo of the label.", Day(0, 10), inReplyTo: first);

        var result = await AgentAsync(h, c.Id, CaseActionType.WaitingForCustomer, "wait-1", Day(0, 10, 1));
        Assert.True(result.Succeeded, result.Error);

        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.AwaitingCustomer, c.Stage);
        Assert.Equal(Day(0, 10), c.FirstAgentResponseAt);
        Assert.Equal(1, await h.Db.EmailMessages.CountAsync(m => m.Direction == MessageDirection.Outbound));
    }

    [Fact]
    public async Task RejectResolution_StoresTheAgentsNewestReply_AndKeepsSupervision()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var fix = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker; that fixes this model.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        h.Receive(Agent1, Customer, "Re: Heater problem", "Thank you, it works now!", Day(0, 11), inReplyTo: fix);
        await h.RunAsync(Day(0, 11, 5));
        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.PotentiallyResolved, c.Stage);

        // The agent writes once more, then says "not resolved yet" before the Sent folder was read.
        h.Send(Agent1, Customer, "Re: Heater problem", "One more thing to check: the fuse.", Day(0, 12), inReplyTo: fix);
        var result = await AgentAsync(h, c.Id, CaseActionType.RejectResolution, "reject-1", Day(0, 12, 1));

        Assert.True(result.Succeeded, result.Error);
        c = await h.CaseAsync(Agent1, Customer);
        Assert.NotEqual(InquiryStage.PotentiallyResolved, c.Stage);
        Assert.False(InquiryLifecycleIsTerminal(c.Stage));
        Assert.Equal(2, await h.Db.EmailMessages.CountAsync(m => m.Direction == MessageDirection.Outbound));
    }

    private static bool InquiryLifecycleIsTerminal(InquiryStage stage) => stage is InquiryStage.Resolved or InquiryStage.Closed;
}
