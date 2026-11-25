using Iemas.Domain.Email;

namespace Iemas.Application.EmailAccounts.Dtos;

/// <summary>
/// Requirements §16 — API may return "hasCredential": true but never the credential itself.
/// This DTO has no password/secret field by construction, not by convention.
/// </summary>
public record EmailAccountDto(
    Guid Id,
    string EmailAddress,
    string? DisplayName,
    EmailAccountPurpose Purpose,
    EmailAccountKind Kind,
    EmailProtocol Protocol,
    string Host,
    int Port,
    string Encryption,
    string Username,
    EmailAuthMethod AuthMethod,
    Guid? OwnerEmployeeId,
    string? OwnerEmployeeName,
    string? ClassificationProfileName,
    bool MonitoringEnabled,
    bool IsActive,
    bool HasCredential,
    DateTimeOffset? LastTestedAt,
    bool? LastTestSucceeded,
    string? LastTestError,
    DateTimeOffset? ProcessEmailsReceivedAfter);

public record CreateEmailAccountRequest(
    string EmailAddress,
    string? DisplayName,
    EmailAccountPurpose Purpose,
    EmailAccountKind Kind,
    EmailProtocol Protocol,
    /// <summary>Blank for a company address = the company mail server from "Mailbox defaults".</summary>
    string? Host,
    /// <summary>0 = the default port.</summary>
    int Port,
    string? Encryption,
    /// <summary>Blank = the email address.</summary>
    string? Username,
    EmailAuthMethod AuthMethod,
    string Secret,
    Guid? OwnerEmployeeId,
    string? ClassificationProfileName,
    bool MonitoringEnabled,
    /// <summary>Null = from now on (mailbox history is kept but not turned into Cases).</summary>
    DateTimeOffset? ProcessEmailsReceivedAfter = null,
    /// <summary>With no owner chosen: give an inbound mailbox to the employee with the same address, adding that employee if needed.</summary>
    bool AutoAssignOwner = false);

/// <summary>Connection settings to try before anything is saved.</summary>
public record TestMailboxSettingsRequest(
    string EmailAddress,
    EmailAccountPurpose Purpose,
    string? Host,
    int Port,
    string? Encryption,
    string? Username,
    string? Secret,
    /// <summary>When editing: with no new password typed, the stored one of this account is used.</summary>
    Guid? ExistingAccountId = null);

public record MailboxDefaultsDto(
    string ImapHost, int ImapPort, string Encryption, int SmtpPort, IReadOnlyList<string> CompanyDomains, bool AllowSelfEnrollment);

public record UpdateEmailAccountRequest(
    string? DisplayName,
    EmailAccountKind Kind,
    string Host,
    int Port,
    string Encryption,
    string Username,
    EmailAuthMethod AuthMethod,
    /// <summary>Null/empty = keep the existing stored credential unchanged (write-only after saving, §16).</summary>
    string? Secret,
    Guid? OwnerEmployeeId,
    string? ClassificationProfileName,
    bool MonitoringEnabled,
    bool IsActive,
    /// <summary>Null = no cut-off: every stored email is processed.</summary>
    DateTimeOffset? ProcessEmailsReceivedAfter = null);

public record TestConnectionResult(bool Succeeded, string? ErrorMessage, double DurationMs);
