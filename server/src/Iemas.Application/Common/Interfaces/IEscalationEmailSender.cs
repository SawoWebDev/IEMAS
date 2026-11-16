namespace Iemas.Application.Common.Interfaces;

public sealed record EmailRecipient(string Name, string Address);

/// <summary>A picture embedded in the HTML body and referenced from it as <c>cid:ContentId</c>.</summary>
public sealed record InlineImage(string ContentId, string ContentType, byte[] Content);

/// <summary>
/// One outgoing email. <see cref="TextBody"/> is always sent; when <see cref="HtmlBody"/> is set the
/// email is multipart/alternative, so mail apps that do not show HTML still get the plain text.
/// </summary>
public sealed record OutboundEmail(IReadOnlyList<EmailRecipient> To, string Subject, string TextBody,
    string? HtmlBody = null, IReadOnlyList<InlineImage>? InlineImages = null);

public sealed record OutboundEmailResult(bool Sent, string? MessageId, string? Error);

/// <summary>
/// Module 11 / G10 - sends internal escalation email to management through the configured Outbound
/// email account. Never used for customer email: IEMAS still never sends, replies to or forwards
/// customer email.
/// </summary>
public interface IEscalationEmailSender
{
    Task<OutboundEmailResult> SendAsync(OutboundEmail email, CancellationToken cancellationToken);
}
