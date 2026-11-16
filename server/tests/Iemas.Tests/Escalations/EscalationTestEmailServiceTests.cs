using Iemas.Application.Escalations;
using Iemas.Domain.Identity;
using Iemas.Tests.Inquiries;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Escalations;

public class EscalationTestEmailServiceTests
{
    [Fact]
    public async Task SendAsync_GoesToTheAgentsSupervisor_MarkedAsTest_AndChangesNoCase()
    {
        using var db = TestDbContext.CreateNew();
        var boss = new Employee { FullName = "Jeremiah", Email = "jeremiah@sawo.com" };
        db.Employees.Add(boss);
        var agent = new Employee { FullName = "Rafael", Email = "rafael@sawo.com", SupervisorEmployeeId = boss.Id };
        db.Employees.Add(agent);
        await db.SaveChangesAsync();
        var sender = new CapturingEscalationEmailSender();

        var result = await new EscalationTestEmailService(db, sender, new NoOpAuditService())
            .SendAsync(new SendTestEscalationEmailRequest(agent.Id), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.Value!.Sent);
        Assert.Equal("jeremiah@sawo.com", result.Value.RecipientEmail);
        var email = Assert.Single(sender.Sent);
        Assert.Equal("jeremiah@sawo.com", Assert.Single(email.To).Address);
        Assert.StartsWith("[TEST] [Escalation L1]", email.Subject);
        Assert.StartsWith("THIS IS A TEST EMAIL", email.TextBody);
        Assert.Contains("Rafael <rafael@sawo.com>", email.TextBody);
        Assert.Contains("Previous reminders: 2", email.TextBody);
        Assert.Equal(0, await db.Cases.CountAsync());
    }

    [Fact]
    public async Task SendAsync_AgentWithoutSupervisor_FailsWithGuidance_AndSendsNothing()
    {
        using var db = TestDbContext.CreateNew();
        var agent = new Employee { FullName = "Rafael", Email = "rafael@sawo.com" };
        db.Employees.Add(agent);
        await db.SaveChangesAsync();
        var sender = new CapturingEscalationEmailSender();

        var result = await new EscalationTestEmailService(db, sender, new NoOpAuditService())
            .SendAsync(new SendTestEscalationEmailRequest(agent.Id), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no supervisor", result.Error);
        Assert.Empty(sender.Sent);
    }

    [Theory]
    [InlineData(1, "Sample Supervisor")]
    [InlineData(2, "Sample Department Manager")]
    [InlineData(9, "Sample Director")] // out-of-range levels are clamped to 3
    public async Task PreviewAsync_ReturnsTheRealEmail_WithLogoInlinedAndNothingSent(int level, string recipientName)
    {
        using var db = TestDbContext.CreateNew();
        var sender = new CapturingEscalationEmailSender();

        var preview = await new EscalationTestEmailService(db, sender, new NoOpAuditService()).PreviewAsync(level, CancellationToken.None);

        Assert.StartsWith("[TEST] [Escalation L", preview.Subject);
        Assert.Contains("Inquiry needs your attention", preview.Html);
        Assert.Contains("data:image/png;base64,", preview.Html);   // a browser cannot resolve cid:
        Assert.DoesNotContain("cid:", preview.Html);
        Assert.Contains("Previous reminders: 2", preview.Text);
        Assert.Empty(sender.Sent);
        Assert.Equal(0, await db.Cases.CountAsync());
    }
}
