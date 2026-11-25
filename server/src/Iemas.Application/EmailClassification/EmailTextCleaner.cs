using System.Text.RegularExpressions;

namespace Iemas.Application.EmailClassification;

/// <summary>The newest message of an email, and the older text it quotes underneath.</summary>
public record EmailTextParts(string Latest, string Quoted);

/// <summary>
/// Separates what the sender just wrote from the reply history quoted under it. Judging an email on its
/// quoted history is a common cause of false alarms: a "thanks" under our own price list is not a price request.
///
/// Forwarded email is left whole, because there the forwarded text is the point of the message. So is an
/// email whose own part is too short to mean anything ("see below").
/// </summary>
public static class EmailTextCleaner
{
    private static readonly Regex[] QuoteStarts =
    {
        // "On Tue, 3 Nov 2026 at 10:00, Anna <anna@x.com> wrote:" (the address may wrap onto a second line)
        new(@"^[ \t]*On\b[^\n]{0,200}(?:\n[^\n]{0,200})?\bwrote:[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^[ \t]*Am\b[^\n]{0,200}\bschrieb\b[^\n]{0,200}:[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^[ \t]*-{2,}\s*(?:Original Message|Ursprüngliche Nachricht)\s*-{2,}", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Outlook's header block: From: ... followed within two lines by Sent: or Date:
        new(@"^[ \t]*(?:From|Von|Lähettäjä):[^\n]+\n(?:[^\n]*\n)?[ \t]*(?:Sent|Date|Gesendet|Lähetetty):", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Plain-text quoting.
        new(@"^[ \t]*>", RegexOptions.Multiline | RegexOptions.Compiled),
    };

    private static readonly Regex ForwardSubject = new(@"^\s*(?:fw|fwd|wg|vl)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ForwardMarker = new(@"-{2,}\s*Forwarded message\s*-{2,}|^\s*Begin forwarded message:", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private const int MinimumOwnWords = 3;

    public static EmailTextParts Split(string? subject, string? body)
    {
        var text = (body ?? string.Empty).Replace("\r\n", "\n");
        if (text.Length == 0) return new EmailTextParts(string.Empty, string.Empty);
        if (ForwardSubject.IsMatch(subject ?? string.Empty) || ForwardMarker.IsMatch(text)) return new EmailTextParts(text, string.Empty);

        var cut = text.Length;
        foreach (var pattern in QuoteStarts)
        {
            var match = pattern.Match(text);
            if (match.Success && match.Index < cut) cut = match.Index;
        }
        if (cut == text.Length) return new EmailTextParts(text, string.Empty);

        var latest = text[..cut].Trim();
        return Regex.Matches(latest, @"\p{L}+").Count < MinimumOwnWords
            ? new EmailTextParts(text, string.Empty)
            : new EmailTextParts(latest, text[cut..].Trim());
    }
}
