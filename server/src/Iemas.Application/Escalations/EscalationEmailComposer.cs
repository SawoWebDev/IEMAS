using System.Net;
using System.Text;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Inquiries;
using Iemas.Application.Notifications;
using Iemas.Domain.Cases;

namespace Iemas.Application.Escalations;

/// <summary>What management needs to understand an escalated inquiry (Module 11's required list).</summary>
public sealed record EscalationEmailContext(
    Case Case,
    string? AgentName,
    string? AgentEmail,
    int Level,
    string Reason,
    IReadOnlyList<DateTimeOffset> RemindersSent,
    TimeZoneInfo Zone,
    DateTimeOffset Now,
    string OrganizationName = "SAWO",
    string? CmsBaseUrl = null,
    bool IsTest = false);

/// <summary>
/// Module 11 - the escalation email. Contains: customer, original subject and date, assigned agent,
/// current status, whether the agent has responded, last conversation activity, how long it has
/// been inactive/unresolved, previous reminders and the reason for escalation. Never includes the
/// conversation body itself.
///
/// Sent as plain text plus a branded HTML version in the SAWO style used by the SAWO Playbook emails
/// (wood-brown header, red accent, details table, highlighted note, button, logo footer). Layout is
/// tables with inline styles only, so it renders the same in Outlook, Gmail and phone mail apps.
/// </summary>
public static class EscalationEmailComposer
{
    /// <summary>Content id of the SAWO logo attached inline to each escalation email.</summary>
    public const string LogoContentId = "sawo-logo@iemas";

    // SAWO brand colours (same values as the SAWO Playbook).
    private const string Wood = "#7a4a2c";
    private const string Header = "#a97d53";
    private const string Button = "#b0825e";
    private const string Accent = "#c8454a";
    private const string Text = "#1a1a1a";
    private const string Muted = "#9b8573";
    private const string LabelColor = "#8a6a50";
    private const string Line = "#eee3d8";
    private const string Font = "'Montserrat','Segoe UI',Arial,Helvetica,sans-serif";

    private static readonly Lazy<byte[]?> Logo = new(() =>
    {
        using var stream = typeof(EscalationEmailComposer).Assembly.GetManifestResourceStream("Iemas.Application.sawo-logo.png");
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    public static OutboundEmail Compose(EscalationEmailContext x, IReadOnlyList<EmailRecipient> to)
    {
        var c = x.Case;
        string Local(DateTimeOffset t) => $"{TimeZoneInfo.ConvertTime(t, x.Zone):yyyy-MM-dd HH:mm} ({x.Zone.Id})";

        var lastActivity = new[] { c.LastCustomerMessageAt, c.LastAgentMessageAt, c.LastInternalActivityAt }
            .Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(c.FirstEmailReceivedAt).Max();
        var lastActivityBy = lastActivity == c.LastAgentMessageAt ? "agent" : lastActivity == c.LastCustomerMessageAt ? "customer" : "internal";
        var inactiveFor = NotificationService.FormatElapsed(x.Now - lastActivity);
        var openFor = NotificationService.FormatElapsed(x.Now - (c.SupervisionCycleStartedAt ?? c.FirstEmailReceivedAt));
        var caseLink = CaseLink(x.CmsBaseUrl, x.IsTest ? null : c.Id);

        var body = new StringBuilder()
            .AppendLine($"An inquiry needs management attention (escalation level {x.Level}).")
            .AppendLine()
            .AppendLine($"Reason for escalation: {x.Reason}")
            .AppendLine()
            .AppendLine($"Inquiry:            {c.CaseNumber}")
            .AppendLine($"Customer:           {(string.IsNullOrWhiteSpace(c.CustomerDisplayName) ? c.CustomerEmailAddress : $"{c.CustomerDisplayName} <{c.CustomerEmailAddress}>")}")
            .AppendLine($"Original subject:   {c.Subject}")
            .AppendLine($"Original date:      {Local(c.FirstEmailReceivedAt)}")
            .AppendLine($"Assigned agent:     {(x.AgentName is null ? "(unassigned)" : $"{x.AgentName} <{x.AgentEmail}>")}")
            .AppendLine($"Current status:     {InquiryLifecycle.Label(c.Stage)}{(c.OverdueSince is null ? "" : " - OVERDUE")}")
            .AppendLine($"Agent has replied:  {(c.FirstAgentResponseAt is DateTimeOffset first ? $"Yes, first reply {Local(first)}" : "No")}")
            .AppendLine($"Last activity:      {Local(lastActivity)} (by {lastActivityBy})")
            .AppendLine($"Inactive for:       {inactiveFor}")
            .AppendLine($"Open for:           {openFor}")
            .AppendLine($"Resolution target:  {(c.ResolutionDueAt is DateTimeOffset due ? Local(due) : "-")}")
            .AppendLine($"Previous reminders: {(x.RemindersSent.Count == 0 ? "none" : $"{x.RemindersSent.Count} ({string.Join(", ", x.RemindersSent.Select(Local))})")}")
            .AppendLine()
            .AppendLine(caseLink is null
                ? "Open the inquiry in the IEMAS CMS (Cases) for the full conversation and history."
                : $"Open the inquiry for the full conversation and history: {caseLink}")
            .AppendLine("This is an internal message from the inquiry monitoring system.");

        var subject = $"[Escalation L{x.Level}] {c.CaseNumber}: {c.Subject}".Replace("\r", " ").Replace("\n", " ");
        var logo = Logo.Value;
        var html = Html(x, Local, lastActivity, lastActivityBy, inactiveFor, openFor, caseLink, subject, logo is not null);
        var images = logo is null ? null : new[] { new InlineImage(LogoContentId, "image/png", logo) };

        return new OutboundEmail(to, subject, body.ToString(), html, images);
    }

    /// <summary>The Case's page in the CMS, or null when no valid http(s) address is configured.</summary>
    /// <remarks>A test email has no real Case, so it links to the Cases list instead.</remarks>
    private static string? CaseLink(string? baseUrl, Guid? caseId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return $"{uri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/cases{(caseId is Guid id ? $"/{id}" : "")}";
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string SystemName(string org) =>
        org.Equals("IEMAS", StringComparison.OrdinalIgnoreCase) ? "IEMAS Inquiry Monitoring" : $"{org} Inquiry Monitoring (IEMAS)";

    private static string Html(EscalationEmailContext x, Func<DateTimeOffset, string> local, DateTimeOffset lastActivity,
        string lastActivityBy, string inactiveFor, string openFor, string? caseLink, string subject, bool hasLogo)
    {
        var c = x.Case;
        var org = string.IsNullOrWhiteSpace(x.OrganizationName) ? "SAWO" : x.OrganizationName.Trim();
        var overdue = c.OverdueSince is not null;

        string Row(string label, string valueHtml, bool last = false)
        {
            var border = last ? "" : $"border-bottom:1px solid {Line};";
            return $@"<tr>
      <td style=""padding:10px 16px;{border}font-size:12px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:{LabelColor};width:38%;vertical-align:top;"">{E(label)}</td>
      <td style=""padding:10px 16px;{border}font-size:14px;color:{Text};vertical-align:top;"">{valueHtml}</td>
    </tr>";
        }

        string Mail(string name, string? address) => string.IsNullOrWhiteSpace(address)
            ? E(name)
            : $@"{E(name)}<br><a href=""mailto:{E(address)}"" style=""color:{Wood};text-decoration:none;font-size:13px;"">{E(address)}</a>";

        string Pill(string text, string background, string color) =>
            $@"<span style=""display:inline-block;margin:0 6px 6px 0;padding:5px 12px;border-radius:999px;background:{background};color:{color};font-size:12px;font-weight:700;letter-spacing:.04em;"">{text}</span>";

        string Tile(string label, string value, bool alert) => $@"<td width=""33%"" style=""padding:0 4px;"">
          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""border-collapse:separate;border:1px solid {(alert ? "#f3c5c7" : Line)};border-radius:10px;background:{(alert ? "#fdf1f1" : "#fcf9f6")};"">
            <tr><td align=""center"" valign=""middle"" height=""72"" style=""height:72px;padding:8px 6px;"">
              <div style=""font-size:20px;font-weight:700;color:{(alert ? Accent : Wood)};line-height:1.2;"">{E(value)}</div>
              <div style=""margin-top:4px;font-size:11px;font-weight:600;letter-spacing:.06em;text-transform:uppercase;color:{LabelColor};"">{E(label)}</div>
            </td></tr>
          </table>
        </td>";

        var customer = string.IsNullOrWhiteSpace(c.CustomerDisplayName)
            ? $@"<a href=""mailto:{E(c.CustomerEmailAddress)}"" style=""color:{Wood};text-decoration:none;"">{E(c.CustomerEmailAddress)}</a>"
            : Mail(c.CustomerDisplayName!, c.CustomerEmailAddress);
        var agent = x.AgentName is null ? "<i>Unassigned</i>" : Mail(x.AgentName, x.AgentEmail);
        var repliedHtml = c.FirstAgentResponseAt is DateTimeOffset first
            ? $@"<span style=""color:#2e7d4f;font-weight:700;"">&#10003; Yes</span> <span style=""color:{Muted};font-size:13px;"">first reply {E(local(first))}</span>"
            : $@"<span style=""color:{Accent};font-weight:700;"">&#10007; No reply yet</span>";
        var statusHtml = E(InquiryLifecycle.Label(c.Stage)) + (overdue ? $@" <span style=""color:{Accent};font-weight:700;"">&middot; OVERDUE</span>" : "");

        var rows = string.Join("", new[]
        {
            Row("Inquiry", $"<b>{E(c.CaseNumber)}</b>"),
            Row("Customer", customer),
            Row("Subject", $"<b>{E(c.Subject)}</b>"),
            Row("Received", E(local(c.FirstEmailReceivedAt))),
            Row("Assigned agent", agent),
            Row("Status", statusHtml),
            Row("Agent replied", repliedHtml),
            Row("Last activity", $@"{E(local(lastActivity))} <span style=""color:{Muted};font-size:13px;"">by {E(lastActivityBy)}</span>"),
            Row("Resolution target", c.ResolutionDueAt is DateTimeOffset due ? E(local(due)) : "&ndash;", last: true),
        });

        var reminders = x.RemindersSent.Count == 0
            ? $@"<div style=""font-size:14px;color:{Muted};"">No reminders were sent before this escalation.</div>"
            : string.Join("", x.RemindersSent.Select((t, i) => $@"<div style=""padding:6px 0;font-size:14px;color:{Text};"">
                <span style=""display:inline-block;width:22px;height:22px;line-height:22px;margin-right:10px;border-radius:999px;background:#fbefe2;color:{Wood};font-size:12px;font-weight:700;text-align:center;"">{i + 1}</span>Reminder sent to the agent &middot; {E(local(t))}</div>"));

        var testBanner = x.IsTest
            ? $@"<tr><td style=""padding:0;""><table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""background:#fff6d6;border-bottom:1px solid #f0d36b;"">
          <tr><td align=""center"" style=""padding:12px 24px;font-size:13px;font-weight:700;letter-spacing:.06em;color:#8a5a00;"">&#9888; TEST EMAIL &middot; NO ACTION NEEDED &middot; NO REAL INQUIRY IS AFFECTED</td></tr>
        </table></td></tr>"
            : "";

        var button = caseLink is null
            ? $@"<div style=""font-size:13px;line-height:1.6;color:{Muted};"">Open <b style=""color:{Wood};"">Cases &rarr; {E(c.CaseNumber)}</b> in the IEMAS CMS for the full conversation and history.</div>"
            : $@"<a href=""{E(caseLink)}"" style=""display:inline-block;padding:14px 30px;border-radius:10px;background:{Button};color:#ffffff;font-size:15px;font-weight:700;text-decoration:none;letter-spacing:.02em;"">Open case in IEMAS &rarr;</a>
        <div style=""margin-top:12px;font-size:12px;color:{Muted};"">or copy this link: <a href=""{E(caseLink)}"" style=""color:{Muted};word-break:break-all;"">{E(caseLink)}</a></div>";

        var logo = hasLogo
            ? $@"<img src=""cid:{LogoContentId}"" alt=""{E(org)}"" width=""90"" style=""display:block;width:90px;max-width:90px;height:auto;border:0;margin:0 auto 10px;"">"
            : "";

        var who = x.AgentName is null ? "An unassigned inquiry" : $"An inquiry assigned to <b>{E(x.AgentName)}</b>";
        var preheader = $"{c.CaseNumber} needs your attention: {x.Reason}";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<meta name=""color-scheme"" content=""light"">
<title>{E(subject)}</title>
</head>
<body style=""margin:0;padding:0;background:#f4eee8;font-family:{Font};"">
<div style=""display:none;max-height:0;overflow:hidden;opacity:0;"">{E(preheader)}</div>
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""background:#f4eee8;"">
  <tr><td align=""center"" style=""padding:32px 12px;"">
    <table role=""presentation"" width=""600"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""width:100%;max-width:600px;background:#ffffff;border-radius:14px;overflow:hidden;box-shadow:0 4px 18px rgba(122,74,44,.12);"">

      {testBanner}
      <tr><td style=""background:{Wood};background-image:linear-gradient(135deg,{Wood} 0%,{Header} 100%);padding:28px 32px;"">
        <div style=""font-size:12px;font-weight:600;letter-spacing:.14em;text-transform:uppercase;color:#f3e3d3;"">{E(org)} &middot; Inquiry Monitoring</div>
        <div style=""margin-top:6px;font-size:22px;font-weight:700;line-height:1.3;color:#ffffff;"">Inquiry needs your attention</div>
        <div style=""margin-top:6px;font-size:14px;line-height:1.5;color:#f3e3d3;"">{E(c.CaseNumber)} &middot; {E(c.Subject)}</div>
      </td></tr>
      <tr><td style=""height:4px;background:{Accent};font-size:0;line-height:0;"">&nbsp;</td></tr>

      <tr><td style=""padding:26px 32px 4px;"">
        {Pill($"&#9679; Escalation level {x.Level}", "#fdecec", Accent)}{Pill(E(InquiryLifecycle.Label(c.Stage)), "#fbefe2", Wood)}{(overdue ? Pill("Overdue", Accent, "#ffffff") : "")}
      </td></tr>

      <tr><td style=""padding:14px 32px 6px;"">
        <p style=""margin:0 0 16px;font-size:15px;line-height:1.65;color:{Text};"">{who} has not been handled in time and is now escalated to you. Please follow up with the agent.</p>
      </td></tr>

      <tr><td style=""padding:0 32px 18px;"">
        <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""border-collapse:separate;background:#fdf1f1;border:1px solid #f3c5c7;border-left:5px solid {Accent};border-radius:10px;"">
          <tr><td style=""padding:14px 18px;"">
            <div style=""font-size:12px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:{Accent};"">Why this was escalated</div>
            <div style=""margin-top:6px;font-size:15px;line-height:1.6;font-weight:600;color:{Text};"">{E(x.Reason)}</div>
          </td></tr>
        </table>
      </td></tr>

      <tr><td style=""padding:0 28px 18px;"">
        <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0""><tr>
        {Tile("Inactive for", inactiveFor, true)}
        {Tile("Open for", openFor, false)}
        {Tile("Reminders sent", x.RemindersSent.Count.ToString(), x.RemindersSent.Count > 0)}
        </tr></table>
      </td></tr>

      <tr><td style=""padding:4px 32px 8px;"">
        <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""border:1px solid {Line};border-radius:10px;background:#fcf9f6;border-collapse:separate;"">
          {rows}
        </table>
      </td></tr>

      <tr><td style=""padding:18px 32px 0;"">
        <div style=""font-size:12px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:{LabelColor};margin-bottom:6px;"">Reminder history</div>
        {reminders}
      </td></tr>

      <tr><td align=""center"" style=""padding:26px 32px 32px;"">
        {button}
      </td></tr>

      <tr><td style=""padding:0 32px;""><div style=""border-top:1px solid {Line};font-size:0;line-height:0;"">&nbsp;</div></td></tr>
      <tr><td align=""center"" style=""padding:24px 32px 28px;background:#ffffff;"">
        {logo}
        <div style=""font-size:12px;line-height:1.6;color:{Muted};"">
          This is an automated message from <b style=""color:{Wood};"">{E(SystemName(org))}</b>.<br>Please do not reply to this email.
        </div>
      </td></tr>

    </table>
  </td></tr>
</table>
</body>
</html>";
    }
}
