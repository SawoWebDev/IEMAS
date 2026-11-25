using Iemas.Application.Agents.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Application.EmailAccounts;
using Iemas.Domain.Agents;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

/// <summary>
/// An employee sets up their own PC with nothing but their company email address and password.
///
/// The password is the proof of identity: IEMAS signs in to the mail server it is configured with (never one
/// the caller names) and only for company domains. When that works, the mailbox is added or its password
/// updated, an employee record is found or created, and the PC is approved.
///
/// A PC is approved without an administrator only when the person who proved the password is plainly the one
/// whose alerts it will receive. Knowing the password of a shared mailbox owned by someone with other
/// mailboxes is not enough: that request waits for approval.
/// </summary>
public class MailboxEnrollmentService
{
    private const string Disabled = "Registering with a mailbox password is switched off. Ask your IEMAS administrator to add your mailbox.";

    private readonly IAppDbContext _db;
    private readonly ICredentialEncryptionService _encryption;
    private readonly IEmailProviderAdapterResolver _adapters;
    private readonly AgentRegistrationService _registration;
    private readonly EnrollmentThrottle _throttle;
    private readonly IEscalationEmailSender _sender;
    private readonly IAuditService _audit;

    public MailboxEnrollmentService(
        IAppDbContext db, ICredentialEncryptionService encryption, IEmailProviderAdapterResolver adapters,
        AgentRegistrationService registration, EnrollmentThrottle throttle, IEscalationEmailSender sender, IAuditService audit)
    {
        _db = db;
        _encryption = encryption;
        _adapters = adapters;
        _registration = registration;
        _throttle = throttle;
        _sender = sender;
        _audit = audit;
    }

    /// <summary>Signs in to the mailbox and changes nothing.</summary>
    public async Task<Result<MailboxTestResponse>> TestAsync(MailboxLoginRequest request, CancellationToken cancellationToken)
    {
        var check = await CheckAsync(request.EmailAddress, request.Password, cancellationToken);
        return check.Succeeded
            ? Result<MailboxTestResponse>.Success(new MailboxTestResponse(true,
                $"Your email and password work (signed in to {check.Value!.Settings.Host}). Click Connect to finish."))
            : Result<MailboxTestResponse>.Failure(check.Error!);
    }

    public async Task<Result<EnrollMailboxResponse>> EnrollAsync(EnrollMailboxRequest request, string? clientIp, CancellationToken cancellationToken)
    {
        var clientName = string.IsNullOrWhiteSpace(request.ClientName) ? "Employee PC" : request.ClientName.Trim();
        if (Common.Security.InputSanitizer.ValidateFreeText("Client name", clientName) is { } nameError)
        {
            return Result<EnrollMailboxResponse>.Failure(nameError);
        }

        var check = await CheckAsync(request.EmailAddress, request.Password, cancellationToken);
        if (!check.Succeeded) return Result<EnrollMailboxResponse>.Failure(check.Error!);
        var (email, settings, account) = check.Value!;

        Employee owner;
        var mailboxCreated = account is null;
        if (account?.OwnerEmployeeId is Guid ownerId)
        {
            owner = await _db.Employees.FirstAsync(e => e.Id == ownerId, cancellationToken);
        }
        else
        {
            (owner, _) = await MailboxDefaults.FindOrAddEmployeeAsync(_db, email, cancellationToken);
        }
        if (!owner.IsActive)
        {
            return Result<EnrollMailboxResponse>.Failure("Your employee record is inactive. Ask your IEMAS administrator.");
        }

        var encrypted = _encryption.Encrypt(request.Password);
        var now = DateTimeOffset.UtcNow;
        if (account is null)
        {
            account = new EmailAccount
            {
                EmailAddress = email,
                DisplayName = owner.FullName,
                Purpose = EmailAccountPurpose.Inbound,
                Kind = EmailAccountKind.Primary,
                Protocol = EmailProtocol.Imap,
                Host = settings.Host,
                Port = settings.Port,
                Encryption = settings.Encryption,
                Username = settings.Username,
                AuthMethod = EmailAuthMethod.Password,
                OwnerEmployeeId = owner.Id,
                MonitoringEnabled = true,
                IsActive = true,
                // Only email from now on becomes work: nobody is alerted about their old mail.
                ProcessEmailsReceivedAfter = now,
                Credential = new EmailCredential
                {
                    EncryptedSecret = encrypted.Ciphertext, Nonce = encrypted.Nonce, Tag = encrypted.Tag, KeyId = encrypted.KeyId,
                },
            };
            _db.EmailAccounts.Add(account);
        }
        else
        {
            account.OwnerEmployeeId = owner.Id;
            var credential = account.Credential;
            if (credential is null)
            {
                credential = new EmailCredential { EmailAccountId = account.Id };
                _db.EmailCredentials.Add(credential);
            }
            credential.EncryptedSecret = encrypted.Ciphertext;
            credential.Nonce = encrypted.Nonce;
            credential.Tag = encrypted.Tag;
            credential.KeyId = encrypted.KeyId;
            credential.RotatedAt = now;
            account.UpdatedAt = now;
        }
        account.LastTestedAt = now;
        account.LastTestSucceeded = true;
        account.LastTestError = null;
        await _db.SaveChangesAsync(cancellationToken);

        var approve = await IsSafeToApproveAsync(owner, account.Id, email, cancellationToken);
        var agent = await _registration.RegisterVerifiedAsync(
            request with { ClientName = clientName }, email, approve ? owner : null, clientIp, cancellationToken);

        await _audit.LogAsync("MAILBOX_SELF_ENROLLED", "EmailAccount", account.Id.ToString(),
            $"{email} {(mailboxCreated ? "added" : "password updated")} from {clientName} by signing in to {settings.Host}; "
            + $"owner {owner.FullName}; PC {(approve ? "approved" : "waiting for administrator approval")}", cancellationToken);

        var welcomeSent = await SendWelcomeAsync(owner.FullName, email, clientName, approve, cancellationToken);
        var message = approve
            ? $"Connected. IEMAS now watches {email} and will alert you on this PC."
                + (welcomeSent ? " A confirmation email is on its way to your inbox." : "")
            : "Your password was accepted. Because this mailbox belongs to someone with other mailboxes, an administrator must approve this PC.";

        return Result<EnrollMailboxResponse>.Success(new EnrollMailboxResponse(
            agent.AgentId, agent.RegistrationRequestToken, agent.Status, owner.FullName, mailboxCreated, welcomeSent, message));
    }

    private sealed record Checked(string Email, EmailProviderConnectionSettings Settings, EmailAccount? Account);

    private async Task<Result<Checked>> CheckAsync(string? address, string? password, CancellationToken cancellationToken)
    {
        var email = (address ?? string.Empty).Trim().ToLowerInvariant();
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || email.Any(char.IsWhiteSpace))
        {
            return Result<Checked>.Failure("Enter your full company email address.");
        }
        if (string.IsNullOrEmpty(password))
        {
            return Result<Checked>.Failure("Enter your email password.");
        }

        var defaults = await MailboxDefaults.LoadAsync(_db, cancellationToken);
        if (!defaults.AllowSelfEnrollment) return Result<Checked>.Failure(Disabled);
        if (!defaults.IsCompanyAddress(email))
        {
            return Result<Checked>.Failure($"Only company addresses can register here ({string.Join(", ", defaults.CompanyDomains)}).");
        }
        if (await ReservedAddresses.IsSuperAdministratorLoginAsync(_db, email, cancellationToken))
        {
            return Result<Checked>.Failure(ReservedAddresses.SuperAdministratorMessage);
        }

        var account = await _db.EmailAccounts.Include(a => a.Credential)
            .FirstOrDefaultAsync(a => a.EmailAddress == email && a.Purpose == EmailAccountPurpose.Inbound, cancellationToken);
        if (account is { IsActive: false })
        {
            return Result<Checked>.Failure("This mailbox was deactivated in IEMAS. Ask your IEMAS administrator.");
        }
        if (account is not null && account.Protocol != EmailProtocol.Imap)
        {
            return Result<Checked>.Failure("This mailbox is connected another way. Ask your IEMAS administrator to set up this PC.");
        }

        if (_throttle.IsBlocked(email))
        {
            return Result<Checked>.Failure("Too many wrong passwords for this address. Wait 15 minutes and try again.");
        }

        // An existing mailbox is checked against the server an administrator set for it; a new one against the company default.
        var settings = new EmailProviderConnectionSettings(
            EmailProtocol.Imap,
            account?.Host ?? defaults.ImapHost,
            account?.Port ?? defaults.ImapPort,
            account?.Encryption ?? defaults.Encryption,
            account?.Username ?? email,
            EmailAuthMethod.Password,
            password);

        var test = await _adapters.Resolve(EmailProtocol.Imap).TestConnectionAsync(settings, cancellationToken);
        if (!test.Succeeded)
        {
            if (test.AuthenticationFailed)
            {
                _throttle.RecordFailure(email);
                return Result<Checked>.Failure("The mail server did not accept that email and password. Check the password you use in Outlook.");
            }
            return Result<Checked>.Failure($"IEMAS could not reach the mail server {settings.Host}:{settings.Port}. Try again in a moment, or tell your IT team.");
        }

        _throttle.Clear(email);
        return Result<Checked>.Success(new Checked(email, settings, account));
    }

    private async Task<bool> IsSafeToApproveAsync(Employee owner, Guid accountId, string email, CancellationToken cancellationToken)
    {
        if (string.Equals(owner.Email, email, StringComparison.OrdinalIgnoreCase)) return true;

        // The Agent shows everything its employee owns, so this mailbox must be all of it.
        var otherMailboxes = await _db.EmailAccounts.AnyAsync(
            a => a.OwnerEmployeeId == owner.Id && a.Id != accountId && a.Purpose == EmailAccountPurpose.Inbound, cancellationToken);
        var otherCases = await _db.Cases.AnyAsync(
            c => c.OwnerEmployeeId == owner.Id && c.EmailAccountId != accountId
                && c.WorkStatus != CaseWorkStatus.Completed && c.WorkStatus != CaseWorkStatus.Cancelled, cancellationToken);
        return !otherMailboxes && !otherCases;
    }

    /// <summary>A confirmation in the employee's own inbox, so they can see that mail from IEMAS arrives.</summary>
    private async Task<bool> SendWelcomeAsync(string name, string email, string clientName, bool approved, CancellationToken cancellationToken)
    {
        var body = $"Hi {name},\n\n"
            + (approved
                ? $"The PC \"{clientName}\" is now connected to IEMAS for {email}.\n"
                : $"The PC \"{clientName}\" asked to connect to IEMAS for {email} and is waiting for an administrator's approval.\n")
            + "\nWhat happens next:\n"
            + "- When a customer sends you an inquiry, a pop-up appears on that PC.\n"
            + "- Reply from your mailbox as usual. IEMAS sees your reply by itself.\n"
            + "- Open the IEMAS Agent and choose \"Show me around\" to see an example from start to finish.\n"
            + "\nIf you did not set this up, tell your IT team and change your email password.\n";
        var result = await _sender.SendAsync(new OutboundEmail(new[] { new EmailRecipient(name, email) },
            "IEMAS: your PC is connected", body), cancellationToken);
        return result.Sent;
    }
}
