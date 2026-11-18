using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using static Iemas.Tests.Inquiries.InquiryHarness;

namespace Iemas.Tests.Inquiries;

/// <summary>
/// Recommendation 2 - a customer follow-up in an existing thread is linked to the right inquiry,
/// saved, and immediately tells the employee who owns the inquiry NOW. Never silently skipped.
/// </summary>
public class ThreadFollowUpTests
{
    private const string Agent1 = "agent1@sawo.com";
    private const string Agent2 = "agent2@sawo.com";
    private const string Customer = "anna@customer.example";

    private static int Alerts(InquiryHarness h, string mailbox) =>
        h.PopUps.Sent.Count(p => p.EmployeeId == h.Agents[mailbox].Id);

    [Fact]
    public async Task FollowUp_OnAResolvedInquiry_ReopensIt_AndAlertsTheAgentImmediately()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var fix = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        var c = await h.CaseAsync(Agent1, Customer);
        await h.AgentActions.SubmitActionAsync(Guid.NewGuid(), h.Agents[Agent1].Id,
            new Iemas.Application.Agents.Dtos.SubmitCaseActionRequest("confirm-1", c.Id, CaseActionType.ConfirmResolved, null, Day(0, 10, 10)), CancellationToken.None);
        var alertsBefore = Alerts(h, Agent1);

        var followUpAt = Day(2, 9);
        h.Receive(Agent1, Customer, "Re: Heater problem", "ok", followUpAt, inReplyTo: fix);
        await h.RunAsync(Day(2, 9, 1));

        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(InquiryStage.CustomerFollowUpReceived, c.Stage);
        Assert.Equal(1, c.ReopenCount);
        Assert.Equal(1, await h.Db.Cases.CountAsync());
        Assert.Equal(alertsBefore + 1, Alerts(h, Agent1));
        Assert.Equal(1, await h.Db.CaseEmails.CountAsync(ce => ce.Role == ConversationRole.Customer && ce.EmailMessage.ReceivedAt == followUpAt));
    }

    [Fact]
    public async Task FollowUp_GoesToTheCurrentOwner_WhenTheInquiryWasReassigned()
    {
        using var h = new InquiryHarness(Agent1, Agent2);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var fix = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));

        var c = await h.CaseAsync(Agent1, Customer);
        h.Db.Cases.Single(x => x.Id == c.Id).OwnerEmployeeId = h.Agents[Agent2].Id;
        await h.Db.SaveChangesAsync();
        var agent2Before = Alerts(h, Agent2);
        var agent1Before = Alerts(h, Agent1);

        h.Receive(Agent1, Customer, "Re: Heater problem", "It broke again, please help?", Day(0, 13), inReplyTo: fix);
        await h.RunAsync(Day(0, 13, 1));

        Assert.Equal(agent2Before + 1, Alerts(h, Agent2));
        Assert.Equal(agent1Before, Alerts(h, Agent1));
        Assert.Equal(1, await h.Db.Cases.CountAsync());
    }

    [Fact]
    public async Task FollowUp_ClassifiedBeforeItsInquiryExisted_IsStillLinked_AndTheAgentIsToldTheCustomerWroteAgain()
    {
        using var h = new InquiryHarness(Agent1);
        // Both emails arrive in the same poll: the follow-up is screened before the inquiry exists, on its
        // own words ("ok thanks"), which alone are not an inquiry.
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        h.Receive(Agent1, Customer, "Re: Heater problem", "ok thanks", Monday0900.AddMinutes(2), inReplyTo: first);

        await h.RunAsync(Monday0900.AddMinutes(5));
        await h.RunAsync(Monday0900.AddMinutes(6));

        var c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(1, await h.Db.Cases.CountAsync());
        Assert.Equal(2, await h.Db.CaseEmails.CountAsync(ce => ce.CaseId == c.Id));
        Assert.Contains(h.PopUpTitlesFor(Agent1), t => t.Contains("still needs your response", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FollowUp_OnAnInquiryWithoutOwner_FallsBackToTheMailboxOwner_InsteadOfAlertingNobody()
    {
        using var h = new InquiryHarness(Agent1);
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Monday0900);
        await h.RunAsync(Monday0900.AddMinutes(1));
        var fix = h.Send(Agent1, Customer, "Re: Heater problem", "Please reset the breaker.", Day(0, 10), inReplyTo: first);
        await h.RunAsync(Day(0, 10, 5));
        var c = await h.CaseAsync(Agent1, Customer);
        h.Db.Cases.Single(x => x.Id == c.Id).OwnerEmployeeId = null;
        await h.Db.SaveChangesAsync();
        var before = Alerts(h, Agent1);

        h.Receive(Agent1, Customer, "Re: Heater problem", "It stopped again.", Day(0, 14), inReplyTo: fix);
        await h.RunAsync(Day(0, 14, 1));

        c = await h.CaseAsync(Agent1, Customer);
        Assert.Equal(h.Agents[Agent1].Id, c.OwnerEmployeeId);
        Assert.Equal(before + 1, Alerts(h, Agent1));
        Assert.Contains(await h.Db.Notifications.Select(n => n.Type).ToListAsync(), t => t == NotificationType.NewEmail);
    }
}
