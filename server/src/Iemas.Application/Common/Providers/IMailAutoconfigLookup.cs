namespace Iemas.Application.Common.Providers;

/// <summary>An IMAP server a mailbox might live on.</summary>
public record MailServerCandidate(string Host, int Port, string Encryption);

/// <summary>Looks up the public mail-client auto-configuration for a domain (Thunderbird's database).</summary>
public interface IMailAutoconfigLookup
{
    Task<IReadOnlyList<MailServerCandidate>> LookupAsync(string domain, CancellationToken cancellationToken);
}
