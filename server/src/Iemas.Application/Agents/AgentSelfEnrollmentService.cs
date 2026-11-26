using System.Globalization;
using Iemas.Application.Agents.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Application.Common.Security;
using Iemas.Application.EmailAccounts;
using Iemas.Application.EmailAccounts.Dtos;
using Iemas.Application.Employees;
using Iemas.Application.Employees.Dtos;
using Iemas.Application.Operations;
using Iemas.Domain.Agents;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

/// <summary>What a PC sends to set itself up: the company installer key plus the employee's own email and password.</summary>
/// <remarks>A class, not a record: a record's ToString would print the password.</remarks>
public class SelfEnrollAgentRequest
{
    public string InstallKey { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string? ServerAddress { get; set; }
    public string? AgentVersion { get; set; }
    public string? DeviceMetadata { get; set; }
    public string? FullName { get; set; }

    public override string ToString() => $"SelfEnrollAgentRequest({EmailAddress}, {ClientName})";
}

public record SelfEnrollAgentResponse(
    Guid AgentId,
    string RegistrationRequestToken,
    AgentRegistrationStatus Status,
    string EmployeeName,
    bool EmployeeCreated,
    bool MailboxCreated,
    string? Message);

public record SelfEnrollFailure(string Code, string Message);

public class SelfEnrollResult
{
    public SelfEnrollAgentResponse? Response { get; private init; }
    public SelfEnrollFailure? Failure { get; private init; }
    public bool Succeeded => Response is not null;

    public static SelfEnrollResult Ok(SelfEnrollAgentResponse response) => new() { Response = response };
    public static SelfEnrollResult Fail(string code, string message) => new() { Failure = new SelfEnrollFailure(code, message) };
}

public static class SelfEnrollCodes
{
    public const string InvalidKey = "InvalidKey";
    public const string InvalidRequest = "InvalidRequest";
    public const string DomainNotAllowed = "DomainNotAllowed";
    public const string WrongPassword = "WrongPassword";
    public const string ServerNotFound = "ServerNotFound";
    public const string MailboxDeactivated = "MailboxDeactivated";
    public const string EmployeeDeactivated = "EmployeeDeactivated";
    public const string TooManyAttempts = "TooManyAttempts";
    public const string ReservedAddress = "ReservedAddress";
    public const string Failed = "Failed";
}

/// <summary>
/// One-step PC setup. The employee types their email and password; this proves the password with a real mailbox
/// login, finds or creates the employee and the monitored mailbox, and has the PC approved by the existing
/// registration code. Nothing is written until the password has been proven.
/// </summary>
public class AgentSelfEnrollmentService
{
    private readonly IAppDbContext _db;
    private readonly MailServerDiscovery _discovery;
    private readonly IEmailProviderAdapterResolver _adapters;
    private readonly ICredentialEncryptionService _encryption;
    private readonly IAuditService _audit;
    private readonly AgentRegistrationService _registration;
    private readonly EmployeeService _employees;
    private readonly EmailAccountService _accounts;
    private readonly EnrollmentAttemptLimiter _limiter;

    public AgentSelfEnrollmentService(
        IAppDbContext db, MailServerDiscovery discovery, IEmailProviderAdapterResolver adapters, ICredentialEncryptionService encryption,
        IAuditService audit, AgentRegistrationService registration, EmployeeService employees, EmailAccountService accounts,
        EnrollmentAttemptLimiter limiter)
    {
        _db = db;
        _discovery = discovery;
        _adapters = adapters;
        _encryption = encryption;
        _audit = audit;
        _registration = registration;
        _employees = employees;
        _accounts = accounts;
        _limiter = limiter;
    }

    public async Task<SelfEnrollResult> EnrollAsync(SelfEnrollAgentRequest request, string? clientIp, CancellationToken cancellationToken)
    {
        var result = await EnrollCoreAsync(request, clientIp, cancellationToken);
        if (!result.Succeeded)
        {
            // Never the password. The address and the reason are enough to see who is struggling.
            await _audit.LogAsync("AGENT_SELF_ENROLL_FAILED", "Agent", null,
                $"{request.EmailAddress?.Trim().ToLowerInvariant()}: {result.Failure!.Code} (from {clientIp})", cancellationToken);
        }
        return result;
    }

    private async Task<SelfEnrollResult> EnrollCoreAsync(SelfEnrollAgentRequest request, string? clientIp, CancellationToken cancellationToken)
    {
        var email = (request.EmailAddress ?? string.Empty).Trim().ToLowerInvariant();
        var at = email.LastIndexOf('@');
        if (at < 1 || at == email.Length - 1 || email.Contains(' ') || string.IsNullOrEmpty(request.Password))
            return SelfEnrollResult.Fail(SelfEnrollCodes.InvalidRequest, "Type your full email address and its password.");
        var domain = email[(at + 1)..];
        var clientName = string.IsNullOrWhiteSpace(request.ClientName) ? "PC" : request.ClientName.Trim();

        // 1. The company key, before anything else: without it the server is not an open mailbox-login tester.
        var rawKey = request.InstallKey?.Trim() ?? string.Empty;
        var hash = AgentInstallKeyService.Hash(rawKey);
        var key = rawKey.Length == 0 ? null : await _db.AgentInstallKeys.FirstOrDefaultAsync(k => k.KeyHash == hash, cancellationToken);
        if (key is null || !key.AllowsSelfEnrollment || key.RevokedAt is not null || key.ExpiresAt <= DateTimeOffset.UtcNow || key.UseCount >= key.MaxUses)
            return SelfEnrollResult.Fail(SelfEnrollCodes.InvalidKey, "This installer can no longer be used. Ask your IEMAS administrator for a new one.");

        if (_limiter.IsLocked(email))
            return SelfEnrollResult.Fail(SelfEnrollCodes.TooManyAttempts, "Too many wrong passwords for this address. Wait 15 minutes and try again.");

        if (await ReservedAddresses.IsSuperAdministratorLoginAsync(_db, email, cancellationToken))
            return SelfEnrollResult.Fail(SelfEnrollCodes.ReservedAddress, ReservedAddresses.SuperAdministratorMessage);

        var existing = await _db.EmailAccounts.Include(a => a.Credential).Include(a => a.OwnerEmployee)
            .FirstOrDefaultAsync(a => a.EmailAddress == email && a.Purpose == EmailAccountPurpose.Inbound, cancellationToken);

        if (existing is null && !await DomainAllowedAsync(domain, cancellationToken))
            return SelfEnrollResult.Fail(SelfEnrollCodes.DomainNotAllowed,
                $"{domain} is not a company email domain, so this PC can't be set up with it. Use your work email, or ask your IEMAS administrator.");

        // 2. Prove the password.
        MailServerCandidate? server = null;
        string? username = null;
        if (existing is not null)
        {
            if (!existing.IsActive)
                return SelfEnrollResult.Fail(SelfEnrollCodes.MailboxDeactivated, "This mailbox is switched off in IEMAS. Ask your IEMAS administrator.");
            var test = await _adapters.Resolve(existing.Protocol).TestConnectionAsync(new EmailProviderConnectionSettings(
                existing.Protocol, existing.Host, existing.Port, existing.Encryption, existing.Username, EmailAuthMethod.Password, request.Password), cancellationToken);
            if (!test.Succeeded)
                return Rejected(email, test.AuthenticationFailed);
        }
        else
        {
            var found = await _discovery.DiscoverAsync(email, request.Password, cancellationToken);
            if (!found.Succeeded)
                return Rejected(email, found.WrongPassword);
            server = found.Server;
            username = found.Username;
        }
        _limiter.Reset(email);

        // 3. Owner.
        var employeeCreated = false;
        var mailboxCreated = false;
        Employee? owner;
        var needsAdmin = false;

        if (existing is not null)
        {
            await RefreshStoredPasswordAsync(existing, request.Password, cancellationToken);
            owner = existing.OwnerEmployee;
            if (owner is not null && (existing.Kind != EmailAccountKind.Primary || !string.Equals(owner.Email, email, StringComparison.OrdinalIgnoreCase)))
                needsAdmin = true; // a shared or someone else's mailbox: an administrator decides
            if (owner is null)
            {
                var resolved = await FindOrCreateEmployeeAsync(email, request.FullName, cancellationToken);
                if (resolved.Failure is not null) return resolved.Failure;
                owner = resolved.Employee;
                employeeCreated = resolved.Created;
                existing.OwnerEmployeeId = owner!.Id;
                await _db.SaveChangesAsync(cancellationToken);
            }

            // The employee's own mailbox, password just proven: installing the Agent means "watch my mailbox", so a
            // mailbox an administrator added with monitoring left off is switched on here. Shared or someone else's
            // mailboxes are left for an administrator to decide.
            if (!needsAdmin && owner is { IsActive: true } && !existing.MonitoringEnabled)
            {
                existing.MonitoringEnabled = true;
                // Only mail from now on becomes inquiries; whatever arrived while monitoring was off stays history.
                existing.ProcessEmailsReceivedAfter = DateTimeOffset.UtcNow;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                await _audit.LogAsync("EMAIL_ACCOUNT_MONITORING_ENABLED", "EmailAccount", existing.Id.ToString(),
                    $"{existing.EmailAddress} (switched on by the employee's PC setup)", cancellationToken);
            }
        }
        else
        {
            var resolved = await FindOrCreateEmployeeAsync(email, request.FullName, cancellationToken);
            if (resolved.Failure is not null) return resolved.Failure;
            owner = resolved.Employee;
            employeeCreated = resolved.Created;

            var created = await _accounts.CreateAsync(new CreateEmailAccountRequest(
                email, owner!.FullName, EmailAccountPurpose.Inbound, EmailAccountKind.Primary, EmailProtocol.Imap,
                server!.Host, server.Port, server.Encryption, username!, EmailAuthMethod.Password, request.Password,
                owner.Id, await DefaultProfileAsync(cancellationToken), true), cancellationToken);
            if (!created.Succeeded)
                return SelfEnrollResult.Fail(SelfEnrollCodes.Failed, created.Error!);
            mailboxCreated = true;
        }

        if (owner is not null && !owner.IsActive)
            return SelfEnrollResult.Fail(SelfEnrollCodes.EmployeeDeactivated, "Your account is switched off in IEMAS. Ask your IEMAS administrator.");

        // 4. The PC itself: the existing registration code approves it when the company key is valid and the mailbox has an active owner.
        var registered = await _registration.RegisterAsync(new RegisterAgentRequest(
            email, clientName, request.ServerAddress, request.AgentVersion, request.DeviceMetadata, needsAdmin ? null : rawKey), clientIp, cancellationToken);
        if (!registered.Succeeded)
            return SelfEnrollResult.Fail(SelfEnrollCodes.Failed, registered.Error!);

        await RevokeEarlierInstallsAsync(email, clientName, registered.Value!.AgentId, cancellationToken);

        await _audit.LogAsync("AGENT_SELF_ENROLLED", "Agent", registered.Value.AgentId.ToString(),
            $"{email} on {clientName} (from {clientIp}); employee {(employeeCreated ? "created" : "existing")}, mailbox {(mailboxCreated ? "created" : "existing")}{(needsAdmin ? "; waiting for an administrator" : "")}", cancellationToken);

        var message = needsAdmin
            ? "This mailbox belongs to someone else or is shared, so an administrator has to approve this PC."
            : null;
        return SelfEnrollResult.Ok(new SelfEnrollAgentResponse(
            registered.Value.AgentId, registered.Value.RegistrationRequestToken, registered.Value.Status,
            owner?.FullName ?? email, employeeCreated, mailboxCreated, message));
    }

    private SelfEnrollResult Rejected(string email, bool wrongPassword)
    {
        if (wrongPassword)
        {
            _limiter.RecordFailure(email);
            return SelfEnrollResult.Fail(SelfEnrollCodes.WrongPassword,
                "That password didn't work for your mailbox. Check it, and if your mailbox is Gmail or Microsoft 365 use an app password.");
        }
        return SelfEnrollResult.Fail(SelfEnrollCodes.ServerNotFound,
            "IEMAS couldn't reach your mail server. Ask your administrator to add your email domain under System, then Settings, then PC setup.");
    }

    private async Task<bool> DomainAllowedAsync(string domain, CancellationToken cancellationToken)
    {
        var configured = await SystemSettingsService.GetValueAsync(_db, SystemSettingKeys.EnrollmentAllowedDomains, cancellationToken);
        var list = ParseDomains(configured);
        if (list.Count == 0)
            list = ParseDomains(await SystemSettingsService.GetValueAsync(_db, SystemSettingKeys.InternalDomains, cancellationToken));
        return list.Contains(domain);
    }

    private static HashSet<string> ParseDomains(string text) =>
        text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.TrimStart('@').ToLowerInvariant())
            .Select(d => d.Contains('@') ? d[(d.IndexOf('@') + 1)..] : d)
            .Where(d => d.Length > 0)
            .ToHashSet();

    /// <summary>A mailbox whose password changed: the proven new one replaces the stored one, same as editing it in the CMS.</summary>
    private async Task RefreshStoredPasswordAsync(EmailAccount account, string password, CancellationToken cancellationToken)
    {
        if (account.Credential is not null)
        {
            try
            {
                var stored = _encryption.Decrypt(new EncryptedSecret(account.Credential.EncryptedSecret, account.Credential.Nonce, account.Credential.Tag, account.Credential.KeyId));
                if (stored == password) return;
            }
            catch (System.Security.Cryptography.CryptographicException) { /* unreadable: replace it */ }
        }

        var encrypted = _encryption.Encrypt(password);
        if (account.Credential is null)
        {
            account.Credential = new EmailCredential { EmailAccountId = account.Id };
            _db.EmailCredentials.Add(account.Credential);
        }
        account.Credential.EncryptedSecret = encrypted.Ciphertext;
        account.Credential.Nonce = encrypted.Nonce;
        account.Credential.Tag = encrypted.Tag;
        account.Credential.KeyId = encrypted.KeyId;
        account.Credential.RotatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.LogAsync("EMAIL_ACCOUNT_CREDENTIAL_ROTATED", "EmailAccount", account.Id.ToString(), $"{account.EmailAddress} (by the employee's PC setup)", cancellationToken);
    }

    private record ResolvedEmployee(Employee? Employee, bool Created, SelfEnrollResult? Failure);

    private async Task<ResolvedEmployee> FindOrCreateEmployeeAsync(string email, string? fullName, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Email == email, cancellationToken);
        if (employee is not null)
        {
            return employee.IsActive
                ? new ResolvedEmployee(employee, false, null)
                : new ResolvedEmployee(null, false, SelfEnrollResult.Fail(SelfEnrollCodes.EmployeeDeactivated, "Your account is switched off in IEMAS. Ask your IEMAS administrator."));
        }

        var departmentName = (await SystemSettingsService.GetValueAsync(_db, SystemSettingKeys.EnrollmentDefaultDepartment, cancellationToken)).Trim();
        Guid? departmentId = departmentName.Length == 0 ? null
            : await _db.Departments.Where(d => d.Name == departmentName).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(cancellationToken);

        var name = string.IsNullOrWhiteSpace(fullName) ? NameFromAddress(email) : fullName.Trim();
        var created = await _employees.CreateAsync(new CreateEmployeeRequest(name, email, departmentId, null), cancellationToken);
        if (!created.Succeeded)
            return new ResolvedEmployee(null, false, SelfEnrollResult.Fail(SelfEnrollCodes.Failed, created.Error!));

        employee = await _db.Employees.FirstAsync(e => e.Email == email, cancellationToken);
        return new ResolvedEmployee(employee, true, null);
    }

    /// <summary>maria.santos@x.com becomes "Maria Santos".</summary>
    public static string NameFromAddress(string email)
    {
        var local = email[..email.IndexOf('@')];
        var words = local.Split(new[] { '.', '_', '-', '+' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetter).ToArray()))
            .Where(w => w.Length > 0);
        var name = string.Join(' ', words);
        return name.Length == 0 ? local : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
    }

    private async Task<string?> DefaultProfileAsync(CancellationToken cancellationToken)
    {
        var configured = (await SystemSettingsService.GetValueAsync(_db, SystemSettingKeys.EnrollmentDefaultProfile, cancellationToken)).Trim();
        if (configured.Length > 0 && await _db.ClassificationProfiles.AnyAsync(p => p.Name == configured, cancellationToken))
            return configured;

        var used = await _db.EmailAccounts.AsNoTracking()
            .Where(a => a.Purpose == EmailAccountPurpose.Inbound && a.ClassificationProfileName != null)
            .GroupBy(a => a.ClassificationProfileName!)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return used.OrderByDescending(u => u.Count).Select(u => u.Name).FirstOrDefault();
    }

    /// <summary>A reinstall on the same PC replaces the earlier registration instead of leaving a stale one behind.</summary>
    private async Task RevokeEarlierInstallsAsync(string email, string clientName, Guid keepAgentId, CancellationToken cancellationToken)
    {
        var older = await _db.Agents
            .Where(a => a.EnrollmentEmailAddress == email && a.ClientName == clientName && a.Id != keepAgentId
                && (a.RegistrationStatus == AgentRegistrationStatus.Approved || a.RegistrationStatus == AgentRegistrationStatus.Pending))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in older)
            await _registration.RevokeAsync(id, new RevokeAgentRequest("Replaced by a new installation on the same PC"), cancellationToken);
    }
}
