using Iemas.Application.Common.Ai;
using Iemas.Application.Inquiries;
using Iemas.Domain.Cases;
using Xunit;

namespace Iemas.Tests.Ai;

/// <summary>The deterministic layer that overrules an over-optimistic "appears resolved" verdict.</summary>
public class ResolutionGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static List<ConversationTurn> Turns(params (string Role, string Text)[] t) =>
        t.Select((x, i) => new ConversationTurn(x.Role, T0.AddHours(i), x.Text)).ToList();

    private static ResolutionGuardResult Guard(params (string Role, string Text)[] t) =>
        ResolutionGuard.Apply(ResolutionVerdict.AppearsResolved, Turns(t), null);

    [Fact]
    public void CustomerConfirmsItWorks_StaysResolved()
    {
        var r = Guard(("Customer", "My heater does not work."), ("Agent", "Please reset the breaker."), ("Customer", "Thank you, it works now!"));
        Assert.False(r.Downgraded);
        Assert.Equal(ResolutionVerdict.AppearsResolved, r.Verdict);
    }

    [Fact]
    public void CustomerThanksAlone_IsNotResolution()
    {
        var r = Guard(("Customer", "Can I get a quote?"), ("Agent", "Thanks for contacting us, we received your request."), ("Customer", "Thanks!"));
        Assert.True(r.Downgraded);
        Assert.Equal(ResolutionVerdict.InProgress, r.Verdict);
    }

    [Fact]
    public void LastMessageFromAgent_CustomerHasNotReplied_Downgraded()
    {
        var r = Guard(("Customer", "Price for 50 units?"), ("Agent", "It is 40 USD per unit."));
        Assert.True(r.Downgraded);
        Assert.Equal(ResolutionVerdict.InProgress, r.Verdict);
        Assert.Contains(r.Findings, f => f.Contains("customer has not replied"));
    }

    [Fact]
    public void CustomerThanksButAsksAgain_BecomesCustomerWaiting()
    {
        var r = Guard(("Customer", "Price?"), ("Agent", "40 USD."), ("Customer", "Thanks, but can you also send the datasheet?"));
        Assert.True(r.Downgraded);
        Assert.Equal(ResolutionVerdict.CustomerWaiting, r.Verdict);
    }

    [Theory]
    [InlineData("I will send the quotation tomorrow.")]
    [InlineData("We will get back to you with the delivery date.")]
    [InlineData("Quotation attached, awaiting your approval.")]
    [InlineData("I'll check with the warehouse and follow up.")]
    [InlineData("Please confirm the delivery address.")]
    [InlineData("Which colour would you prefer?")]
    public void AgentLeavesSomethingOpen_EvenIfCustomerThanksAfterwards(string agentText)
    {
        var r = Guard(("Customer", "I need a heater."), ("Agent", agentText), ("Customer", "Thank you very much."));
        Assert.True(r.Downgraded, agentText);
        Assert.NotEqual(ResolutionVerdict.AppearsResolved, r.Verdict);
    }

    [Fact]
    public void AgentLastMessageEndingInQuestion_WithSignature_Downgraded()
    {
        var r = Guard(("Customer", "Need a quote."), ("Agent", "We can offer 3 models. Which one do you prefer?\n\nBest regards,\nJohn\nSales"));
        Assert.True(r.Downgraded);
    }

    [Fact]
    public void PoliteClosingQuestion_IsNotAnOpenQuestion()
    {
        Assert.False(ResolutionGuard.EndsWithOpenQuestion("is there anything else i can help you with?"));
        Assert.True(ResolutionGuard.EndsWithOpenQuestion("which model do you prefer?"));
    }

    [Fact]
    public void ExplicitCustomerConfirmationAfterAgentPromise_Wins()
    {
        var r = Guard(("Customer", "Need the invoice."), ("Agent", "I will send the invoice today."), ("Customer", "Received it, all set, thank you."));
        Assert.False(r.Downgraded);
    }

    [Fact]
    public void AiOpenItems_BlockResolution()
    {
        var turns = Turns(("Customer", "Help"), ("Agent", "Done."), ("Customer", "Thanks, it works."));
        var r = ResolutionGuard.Apply(ResolutionVerdict.AppearsResolved, turns, new[] { "Pending quotation or proposal" });
        Assert.True(r.Downgraded);
        Assert.Equal(ResolutionVerdict.InProgress, r.Verdict);
    }

    [Theory]
    [InlineData(ResolutionVerdict.InProgress)]
    [InlineData(ResolutionVerdict.CustomerWaiting)]
    [InlineData(ResolutionVerdict.AcknowledgedOnly)]
    public void OtherVerdicts_AreNeverChanged(ResolutionVerdict verdict)
    {
        var r = ResolutionGuard.Apply(verdict, Turns(("Customer", "Hi"), ("Customer", "Thanks, it works.")), null);
        Assert.Equal(verdict, r.Verdict);
        Assert.False(r.Downgraded);
    }

    [Fact]
    public void QuotedHistoryInCustomerReply_IsIgnored()
    {
        var r = Guard(("Customer", "Need help."), ("Agent", "Try rebooting."),
            ("Customer", "It works now, thanks!\n\nOn Mon, 2 Nov 2026 at 10:00, Support wrote:\n> Which model is it?\n> Try rebooting."));
        Assert.False(r.Downgraded);
    }
}
