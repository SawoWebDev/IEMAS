using Iemas.Application.Common.Interfaces;
using Iemas.Application.Operations;
using Iemas.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.EmailAccounts;

/// <summary>
/// The company's mail server settings (System Settings → "Mailbox defaults"). With them, a company mailbox
/// needs only its address and password, both in the CMS and from an employee's PC.
/// </summary>
public record MailboxDefaults(
    string ImapHost,
    int ImapPort,
    string Encryption,
    int SmtpPort,
    IReadOnlyList<string> CompanyDomains,
    bool AllowSelfEnrollment)
{
    public static async Task<MailboxDefaults> LoadAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        Task<string> Text(string key) => SystemSettingsService.GetValueAsync(db, key, cancellationToken);
        return new MailboxDefaults(
            (await Text(SystemSettingKeys.DefaultImapHost)).Trim(),
            await SystemSettingsService.GetIntAsync(db, SystemSettingKeys.DefaultImapPort, cancellationToken),
            (await Text(SystemSettingKeys.DefaultImapEncryption)).Trim(),
            await SystemSettingsService.GetIntAsync(db, SystemSettingKeys.DefaultSmtpPort, cancellationToken),
            (await Text(SystemSettingKeys.CompanyMailDomains))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            await SystemSettingsService.GetBoolAsync(db, SystemSettingKeys.AllowSelfEnrollment, cancellationToken));
    }

    /// <summary>True when the address is in one of the company domains (or listed as a full address).</summary>
    public bool IsCompanyAddress(string? address) =>
        IgnoredSenderList.Parse(string.Join('\n', CompanyDomains)).Matches(address);

    /// <summary>"maria.santos@sawo.com" → "Maria Santos".</summary>
    public static string NameFromAddress(string address)
    {
        var local = address.Split('@')[0];
        var words = local.Split(new[] { '.', '_', '-', '+' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]);
        var name = string.Join(' ', words);
        return name.Length == 0 ? address : name;
    }

    /// <summary>
    /// The employee a new mailbox belongs to: the one with the same email address, or a new employee named
    /// after the address (an administrator can rename them and set their supervisor).
    /// </summary>
    public static async Task<(Employee Employee, bool Created)> FindOrAddEmployeeAsync(IAppDbContext db, string email, CancellationToken cancellationToken)
    {
        var existing = await db.Employees.FirstOrDefaultAsync(e => e.Email.ToLower() == email, cancellationToken);
        if (existing is not null) return (existing, false);

        var employee = new Employee { FullName = NameFromAddress(email), Email = email, IsActive = true };
        db.Employees.Add(employee);
        return (employee, true);
    }
}
