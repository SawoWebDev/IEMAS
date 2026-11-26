using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Application.Operations;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

public record MailDiscoveryResult(bool Succeeded, MailServerCandidate? Server, string? Username, bool WrongPassword, string? Detail);

/// <summary>
/// Works out which IMAP server a new mailbox is on by actually logging in, so a typed password is proven
/// before anything is saved. Tries, in order: the server of an existing mailbox on the same domain, the
/// administrator's presets, built-in presets for the big providers, the public auto-configuration database,
/// then imap./mail. guesses. A server that answers but refuses the password ends the search: the password is wrong.
/// </summary>
public class MailServerDiscovery
{
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(45);

    private static readonly Dictionary<string, MailServerCandidate> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = new("imap.gmail.com", 993, "SSL/TLS"),
        ["googlemail.com"] = new("imap.gmail.com", 993, "SSL/TLS"),
        ["outlook.com"] = new("outlook.office365.com", 993, "SSL/TLS"),
        ["hotmail.com"] = new("outlook.office365.com", 993, "SSL/TLS"),
        ["live.com"] = new("outlook.office365.com", 993, "SSL/TLS"),
        ["office365.com"] = new("outlook.office365.com", 993, "SSL/TLS"),
        ["yahoo.com"] = new("imap.mail.yahoo.com", 993, "SSL/TLS"),
        ["zoho.com"] = new("imap.zoho.com", 993, "SSL/TLS"),
    };

    private readonly IAppDbContext _db;
    private readonly IEmailProviderAdapterResolver _adapters;
    private readonly IMailAutoconfigLookup _autoconfig;

    public MailServerDiscovery(IAppDbContext db, IEmailProviderAdapterResolver adapters, IMailAutoconfigLookup autoconfig)
    {
        _db = db;
        _adapters = adapters;
        _autoconfig = autoconfig;
    }

    public async Task<MailDiscoveryResult> DiscoverAsync(string email, string password, CancellationToken cancellationToken)
    {
        var at = email.LastIndexOf('@');
        var local = email[..at];
        var domain = email[(at + 1)..].ToLowerInvariant();
        var usernames = new[] { email, local };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TotalBudget);
        var adapter = _adapters.Resolve(EmailProtocol.Imap);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastError = null;
        MailDiscoveryResult? acceptAll = null;

        foreach (var stage in Stages(domain))
        {
            IReadOnlyList<MailServerCandidate> candidates;
            try { candidates = await stage(budget.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            catch { continue; }

            foreach (var candidate in candidates)
            {
                if (!tried.Add($"{candidate.Host}:{candidate.Port}:{candidate.Encryption}")) continue;
                var authRefused = 0;
                foreach (var username in usernames)
                {
                    ProviderConnectionTestResult result;
                    try
                    {
                        result = await adapter.TestConnectionAsync(new EmailProviderConnectionSettings(
                            EmailProtocol.Imap, candidate.Host, candidate.Port, candidate.Encryption, username, EmailAuthMethod.Password, password), budget.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return new MailDiscoveryResult(false, null, null, false, lastError);
                    }

                    if (result.Succeeded)
                    {
                        // A server that also lets a made-up password in proves nothing about the typed one (a test
                        // server, or a catch-all). Keep looking for a server that really checks; fall back to this
                        // one only if no other server answers at all.
                        if (await AcceptsAnyPasswordAsync(adapter, candidate, username, budget.Token))
                        {
                            acceptAll ??= new MailDiscoveryResult(true, candidate, username, false, null);
                            break;
                        }
                        return new MailDiscoveryResult(true, candidate, username, false, null);
                    }
                    lastError = result.ErrorMessage;
                    if (result.AuthenticationFailed) authRefused++;
                    else break; // unreachable / TLS problem: another username will not help, try the next server
                }
                if (authRefused == usernames.Length) return new MailDiscoveryResult(false, candidate, null, true, lastError);
            }
        }

        return acceptAll ?? new MailDiscoveryResult(false, null, null, false, lastError);
    }

    private static async Task<bool> AcceptsAnyPasswordAsync(IEmailProviderAdapter adapter, MailServerCandidate candidate, string username, CancellationToken ct)
    {
        try
        {
            var probe = await adapter.TestConnectionAsync(new EmailProviderConnectionSettings(
                EmailProtocol.Imap, candidate.Host, candidate.Port, candidate.Encryption, username, EmailAuthMethod.Password,
                "wrong-" + Guid.NewGuid().ToString("N")), ct);
            return probe.Succeeded;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private IEnumerable<Func<CancellationToken, Task<IReadOnlyList<MailServerCandidate>>>> Stages(string domain)
    {
        yield return async ct =>
        {
            var existing = await _db.EmailAccounts.AsNoTracking()
                .Where(a => a.Purpose == EmailAccountPurpose.Inbound && a.Protocol == EmailProtocol.Imap && a.EmailAddress.EndsWith("@" + domain))
                .Select(a => new { a.Host, a.Port, a.Encryption })
                .ToListAsync(ct);
            return (IReadOnlyList<MailServerCandidate>)existing.GroupBy(a => (a.Host, a.Port, a.Encryption)).OrderByDescending(g => g.Count())
                .Select(g => new MailServerCandidate(g.Key.Host, g.Key.Port, g.Key.Encryption)).ToList();
        };
        yield return async ct => ParsePresets(await SystemSettingsService.GetValueAsync(_db, SystemSettingKeys.EnrollmentMailServerPresets, ct), domain);
        yield return _ => Task.FromResult<IReadOnlyList<MailServerCandidate>>(BuiltIn.TryGetValue(domain, out var c) ? new[] { c } : Array.Empty<MailServerCandidate>());
        yield return ct => _autoconfig.LookupAsync(domain, ct);
        yield return _ => Task.FromResult<IReadOnlyList<MailServerCandidate>>(new[]
        {
            new MailServerCandidate("imap." + domain, 993, "SSL/TLS"),
            new MailServerCandidate("mail." + domain, 993, "SSL/TLS"),
        });
    }

    /// <summary>Lines of "domain = host:port:ssl|starttls". Lines for other domains and malformed lines are skipped.</summary>
    public static IReadOnlyList<MailServerCandidate> ParsePresets(string text, string domain)
    {
        var list = new List<MailServerCandidate>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq < 1 || !string.Equals(raw[..eq].Trim(), domain, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = raw[(eq + 1)..].Trim().Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0 || !int.TryParse(parts[1], out var port) || port is < 1 or > 65535) continue;
            var enc = parts.Length > 2 && parts[2].Equals("starttls", StringComparison.OrdinalIgnoreCase) ? "STARTTLS" : "SSL/TLS";
            list.Add(new MailServerCandidate(parts[0], port, enc));
        }
        return list;
    }
}
