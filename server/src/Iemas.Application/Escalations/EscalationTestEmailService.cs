using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Operations;
using Iemas.Domain.Cases;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Escalations;

public sealed record SendTestEscalationEmailRequest(Guid AgentEmployeeId);

public sealed record EscalationEmailPreview(string Subject, string Html, string Text, string Recipient);

public sealed record TestEscalationEmailResult(bool Sent, string RecipientName, string RecipientEmail, string Subject, string? Error);

/// <summary>
/// Sends one sample escalation email, marked as a test, to the person a real level-1 escalation
/// for this agent would reach (the agent's supervisor). It uses the real email template and the
/// real outbound account, so it proves the whole delivery path without creating or changing any
/// Case. Nothing is stored except an audit-log entry.
/// </summary>
public class EscalationTestEmailService
{
    private readonly IAppDbContext _db;
    private readonly IEscalationEmailSender _sender;
    private readonly IAuditService _audit;

    public EscalationTestEmailService(IAppDbContext db, IEscalationEmailSender sender, IAuditService audit)
    {
        _db = db;
        _sender = sender;
        _audit = audit;
    }

    public async Task<Result<TestEscalationEmailResult>> SendAsync(SendTestEscalationEmailRequest request, CancellationToken cancellationToken)
    {
        var agent = await _db.Employees.AsNoTracking().Include(e => e.SupervisorEmployee)
            .FirstOrDefaultAsync(e => e.Id == request.AgentEmployeeId, cancellationToken);
        if (agent is null) return Result<TestEscalationEmailResult>.Failure("Employee not found.");

        var supervisor = agent.SupervisorEmployee;
        if (supervisor is null || string.IsNullOrWhiteSpace(supervisor.Email))
        {
            return Result<TestEscalationEmailResult>.Failure(
                $"{agent.FullName} has no supervisor with an email address. Set one under People > Employees; level-1 escalations go to that person.");
        }

        var email = await BuildSampleAsync(agent.FullName, agent.Email, supervisor.FullName, supervisor.Email, 1, cancellationToken);

        var result = await _sender.SendAsync(email, cancellationToken);
        await _audit.LogAsync("Escalation.TestEmailSent", "Employee", agent.Id.ToString(),
            $"Test escalation email for {agent.FullName} to {supervisor.Email}: {(result.Sent ? "sent" : "failed - " + result.Error)}", cancellationToken);

        return Result<TestEscalationEmailResult>.Success(
            new TestEscalationEmailResult(result.Sent, supervisor.FullName, supervisor.Email, email.Subject, result.Error));
    }

    /// <summary>
    /// The sample escalation email exactly as it would be sent (same composer), with placeholder names,
    /// for the CMS preview. The logo is inlined as a data address so a browser can show it.
    /// </summary>
    public async Task<EscalationEmailPreview> PreviewAsync(int level, CancellationToken cancellationToken)
    {
        level = Math.Clamp(level, 1, 3);
        var email = await BuildSampleAsync("Sample Agent", "agent@sawo.com",
            level == 1 ? "Sample Supervisor" : level == 2 ? "Sample Department Manager" : "Sample Director",
            "manager@sawo.com", level, cancellationToken);
        var html = email.HtmlBody ?? "";
        foreach (var image in email.InlineImages ?? Array.Empty<InlineImage>())
        {
            html = html.Replace($"cid:{image.ContentId}", $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Content)}");
        }
        return new EscalationEmailPreview(email.Subject, html, email.TextBody, $"Sample Recipient <manager@sawo.com>");
    }

    private async Task<OutboundEmail> BuildSampleAsync(string agentName, string agentEmail, string recipientName, string recipientEmail,
        int level, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var received = now.AddHours(-30);
        var sample = new Case
        {
            CaseNumber = "CASE-TEST",
            CustomerDisplayName = "Sample Customer",
            CustomerEmailAddress = "customer@example.com",
            Subject = "Sample inquiry: price for two sauna heaters",
            FirstEmailReceivedAt = received,
            LastCustomerMessageAt = received,
            SupervisionCycleStartedAt = received,
            OverdueSince = now.AddHours(-2),
            ResolutionDueAt = now.AddDays(4),
            Stage = InquiryStage.AwaitingInitialResponse,
        };
        var zone = await SystemSettingsService.GetDefaultTimeZoneAsync(_db, cancellationToken);
        var composed = EscalationEmailComposer.Compose(
            new EscalationEmailContext(sample, agentName, agentEmail, level,
                "TEST ONLY - no real inquiry is affected. This shows what a real escalation looks like and who receives it.",
                new[] { received.AddHours(6), received.AddHours(8) }, zone, now,
                await SystemSettingsService.GetValueAsync(_db, SystemSettingKeys.OrganizationName, cancellationToken),
                await SystemSettingsService.GetValueAsync(_db, SystemSettingKeys.CmsBaseUrl, cancellationToken),
                IsTest: true),
            new[] { new EmailRecipient(recipientName, recipientEmail) });

        return composed with
        {
            Subject = "[TEST] " + composed.Subject,
            TextBody = "THIS IS A TEST EMAIL from IEMAS. No action is needed.\n\n" + composed.TextBody,
        };
    }
}
