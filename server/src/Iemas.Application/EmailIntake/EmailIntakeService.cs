using System.Diagnostics;
using System.Text;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Application.EmailIntake.Dtos;
using Iemas.Application.Operations;
using Iemas.Domain.Email;
using Iemas.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iemas.Application.EmailIntake;

/// <summary>
/// Requirements §20 — the Email Monitoring Pipeline, Fetch through Persist stages (Classification/
/// Case stages are Phase 4/5 and are intentionally not touched here). §22 duplicate protection,
/// §78 idempotency, §79 durable background processing, §103 transaction ordering (validate state
/// → persist → commit; no external signal is ever the source of truth).
/// </summary>
public class EmailIntakeService
{
    private const int MaxMessagesPerRun = 200;

    private readonly IAppDbContext _db;
    private readonly ICredentialEncryptionService _encryptionService;
    private readonly IEmailProviderAdapterResolver _adapterResolver;
    private readonly ILogger<EmailIntakeService> _logger;

    public EmailIntakeService(
        IAppDbContext db,
        ICredentialEncryptionService encryptionService,
        IEmailProviderAdapterResolver adapterResolver,
        ILogger<EmailIntakeService> logger)
    {
        _db = db;
        _encryptionService = encryptionService;
        _adapterResolver = adapterResolver;
        _logger = logger;
    }

    /// <summary>
    /// Runs one intake cycle for every active, monitoring-enabled Inbound account. Each account
    /// is processed independently — a provider failure on one account must never prevent the
    /// others from being polled (§81 reliability: "No lost Cases", and no single-account outage
    /// should degrade the whole system).
    /// </summary>
    public async Task<List<IntakeRunResult>> RunAllAsync(CancellationToken cancellationToken)
    {
        // §91 Pause Email Processing — mail stays on the server and is fetched after resume.
        if (await _db.IsPausedAsync(PauseControl.EmailProcessing, cancellationToken))
        {
            _logger.LogInformation("Email intake skipped: Email Processing is paused (Emergency Pause)");
            return new List<IntakeRunResult>();
        }

        var accountIds = await _db.EmailAccounts
            .Where(a => a.Purpose == EmailAccountPurpose.Inbound && a.IsActive && a.MonitoringEnabled)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        var results = new List<IntakeRunResult>();
        foreach (var accountId in accountIds)
        {
            // Re-check current state before acting (§103, §20 "Scheduled jobs must re-check
            // current state") — an admin may have disabled the account between the query above
            // and this iteration running, especially under Hangfire's own retry/scheduling.
            results.Add(await RunForAccountAsync(accountId, cancellationToken));
        }

        return results;
    }

    public async Task<IntakeRunResult> RunForAccountAsync(Guid emailAccountId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (await _db.IsPausedAsync(PauseControl.EmailProcessing, cancellationToken))
        {
            return new IntakeRunResult(emailAccountId, false, 0, 0, 0, 0, "Email Processing is paused (Emergency Pause).", stopwatch.ElapsedMilliseconds);
        }

        var account = await _db.EmailAccounts
            .Include(a => a.Credential)
            .FirstOrDefaultAsync(a => a.Id == emailAccountId, cancellationToken);

        if (account is null || !account.IsActive || !account.MonitoringEnabled || account.Purpose != EmailAccountPurpose.Inbound)
        {
            // Not recorded as a sync failure (the account may have been reconfigured since this run
            // was scheduled), but reported honestly so a manual Run Now doesn't look like an empty inbox.
            var reason = account is null ? "Email account not found."
                : account.Purpose != EmailAccountPurpose.Inbound ? "This is not an inbound (monitored) account."
                : !account.IsActive ? "This account is deactivated."
                : "Monitoring is off for this account. Enable it on the Email Accounts page (Edit → Monitoring enabled).";
            return new IntakeRunResult(emailAccountId, false, 0, 0, 0, 0, reason, stopwatch.ElapsedMilliseconds);
        }

        if (account.Credential is null)
        {
            return await RecordAccountFailureAsync(account.Id, "No credential is configured for this account.", stopwatch, cancellationToken);
        }

        var syncState = await _db.EmailSyncStates.FirstOrDefaultAsync(s => s.EmailAccountId == account.Id, cancellationToken);
        if (syncState is null)
        {
            syncState = new EmailSyncState { EmailAccountId = account.Id };
            _db.EmailSyncStates.Add(syncState);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Two first runs for a brand-new account overlapped (scheduled job + Run Now): the other
                // one created the row first. Use it instead of failing this run.
                _db.EmailSyncStates.Entry(syncState).State = EntityState.Detached;
                syncState = await _db.EmailSyncStates.FirstOrDefaultAsync(s => s.EmailAccountId == account.Id, cancellationToken);
                if (syncState is null) throw;
            }
        }

        syncState.LastSyncStartedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        string secret;
        try
        {
            secret = _encryptionService.Decrypt(new(
                account.Credential.EncryptedSecret,
                account.Credential.Nonce,
                account.Credential.Tag,
                account.Credential.KeyId));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return await RecordAccountFailureAsync(account.Id, "Stored credential could not be decrypted; it may be corrupted or encrypted with a rotated key.", stopwatch, cancellationToken, syncState);
        }

        var settings = new EmailProviderConnectionSettings(
            account.Protocol, account.Host, account.Port, account.Encryption, account.Username, account.AuthMethod, secret);

        FetchInboxResult fetchResult;
        try
        {
            var adapter = _adapterResolver.Resolve(account.Protocol);
            fetchResult = await adapter.FetchInboxMessagesAsync(
                settings, syncState.LastUidValidity, syncState.LastSeenUid, MaxMessagesPerRun, cancellationToken, account.ProcessEmailsReceivedAfter);
        }
        catch (OperationCanceledException)
        {
            // Requirements: cooperative cancellation must not be recorded as a failure — the
            // caller (e.g. shutdown, or an admin pausing processing) explicitly requested this.
            throw;
        }
        catch (Exception ex)
        {
            // Provider/network/auth/TLS failure. Requirements §44: mailbox unavailability must
            // never be treated as "no messages" — the sync watermark is left untouched so the
            // next run retries from the same position instead of silently skipping messages.
            return await RecordAccountFailureAsync(account.Id, ex.Message, stopwatch, cancellationToken, syncState);
        }

        int persisted = 0, duplicates = 0, failed = 0;
        var ignoredSenders = await IgnoredSenderList.LoadAsync(_db, cancellationToken);

        foreach (var rawMessage in fetchResult.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // PostgreSQL cannot store NUL bytes or broken UTF-16 in text; one such email must not be
            // able to stop every message behind it, so text is cleaned before it is stored.
            var message = Sanitize(rawMessage);
            var outcome = await PersistOneMessageAsync(account.Id, account.Protocol, account.ProcessEmailsReceivedAfter, ignoredSenders, message, cancellationToken);
            if (outcome == PersistOutcome.Persisted) persisted++;
            else if (outcome == PersistOutcome.Duplicate) duplicates++;
            else if (outcome == PersistOutcome.Failed) failed++;
        }

        foreach (var (uid, error) in fetchResult.MalformedMessages)
        {
            _db.EmailIntakeLogs.Add(new EmailIntakeLog
            {
                EmailAccountId = account.Id,
                ProviderMessageId = uid.ToString(),
                Outcome = EmailIntakeOutcome.Skipped_Malformed,
                Detail = Truncate(error, 2000),
            });
        }

        syncState.LastUidValidity = fetchResult.UidValidity;
        syncState.LastSeenUid = fetchResult.HighestUidSeen ?? syncState.LastSeenUid;
        syncState.LastSyncCompletedAt = DateTimeOffset.UtcNow;
        syncState.LastSyncError = null;
        syncState.ConsecutiveFailureCount = 0;

        await _db.SaveChangesAsync(cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation(
            "IMAP sync for account {EmailAccountId} completed in {ElapsedMilliseconds}ms: {FetchedCount} fetched, {PersistedCount} persisted, {DuplicateCount} duplicates, {MalformedCount} malformed",
            account.Id, stopwatch.ElapsedMilliseconds, fetchResult.Messages.Count, persisted, duplicates, fetchResult.MalformedMessages.Count);
        if (fetchResult.StoppedEarlyReason is not null)
        {
            _logger.LogWarning("IMAP sync for account {EmailAccountId} stopped early: {Reason}", account.Id, fetchResult.StoppedEarlyReason);
        }
        var note = fetchResult.StoppedEarlyReason;
        if (failed > 0)
        {
            var failedNote = $"{failed} message(s) could not be stored and were recorded as Failed (see the intake log).";
            note = note is null ? failedNote : $"{note} {failedNote}";
            _logger.LogError("IMAP sync for account {EmailAccountId}: {FailedCount} message(s) could not be stored", account.Id, failed);
        }
        return new IntakeRunResult(
            account.Id, true, fetchResult.Messages.Count, persisted, duplicates, fetchResult.MalformedMessages.Count,
            note, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Removes what PostgreSQL text columns refuse: NUL (0x00) and unpaired UTF-16 surrogates. Both occur
    /// in real mail (binary pasted into a text part, mangled emoji) and would otherwise fail the whole run.
    /// </summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        StringBuilder? sb = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var bad = c == '\0'
                || (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(value[i - 1])))
                || (char.IsHighSurrogate(c) && (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])));
            if (bad)
            {
                sb ??= new StringBuilder(value, 0, i, value.Length);
                if (c != '\0') sb.Append('�');
            }
            else sb?.Append(c);
        }
        return sb is null ? value : sb.ToString();
    }

    public static ProviderMessage Sanitize(ProviderMessage m) => m with
    {
        ProviderMessageId = Clean(m.ProviderMessageId)!,
        MessageId = Clean(m.MessageId),
        ThreadId = Clean(m.ThreadId),
        InReplyTo = Clean(m.InReplyTo),
        References = m.References.Select(r => Clean(r)!).ToList(),
        FromAddress = Clean(m.FromAddress) ?? string.Empty,
        FromDisplayName = Clean(m.FromDisplayName),
        ToAddresses = m.ToAddresses.Select(r => Clean(r)!).ToList(),
        CcAddresses = m.CcAddresses.Select(r => Clean(r)!).ToList(),
        Subject = Clean(m.Subject) ?? string.Empty,
        BodyText = Clean(m.BodyText),
        BodyHtml = Clean(m.BodyHtml),
        Attachments = m.Attachments.Select(a => a with { FileName = Clean(a.FileName) ?? "unnamed", ContentType = Clean(a.ContentType) }).ToList(),
    };

    private enum PersistOutcome { Persisted, Duplicate, Failed }

    private async Task<PersistOutcome> PersistOneMessageAsync(
        Guid emailAccountId, Iemas.Domain.Email.EmailProtocol protocol, DateTimeOffset? processEmailsReceivedAfter, IgnoredSenderList ignoredSenders,
        ProviderMessage message, CancellationToken cancellationToken)
    {
        // In-memory duplicate check first (cheap, avoids most round-trips), but the *authoritative*
        // guard is the unique DB index on (EmailAccountId, ProviderMessageId) — §22 requires
        // idempotency to hold even under concurrent/overlapping intake runs, which an in-memory
        // check alone cannot guarantee.
        var alreadyExists = await _db.EmailMessages
            .AnyAsync(m => m.EmailAccountId == emailAccountId && m.ProviderMessageId == message.ProviderMessageId, cancellationToken);

        // The same email can come back under a different provider id (an IMAP UID reset after a mailbox
        // migration, a message re-delivered by the provider, the same mail listed twice). Its RFC 5322
        // Message-ID is the identity; with none, a fingerprint of sender, subject, time and recipients stands in.
        if (!alreadyExists)
        {
            var messageId = Truncate(message.MessageId, 998);
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                // A Message-ID alone is not enough: some mailers and web forms reuse one id for different mails,
                // and dropping the second would lose a customer. The same mail has the same sender and subject too.
                var fromForId = Truncate(message.FromAddress, 320) ?? string.Empty;
                alreadyExists = await _db.EmailMessages.AnyAsync(m => m.EmailAccountId == emailAccountId
                    && m.Direction == MessageDirection.Inbound && m.MessageId == messageId
                    && m.FromAddress == fromForId && m.Subject == message.Subject, cancellationToken);
            }
            else
            {
                var from = Truncate(message.FromAddress, 320) ?? string.Empty;
                var to = string.Join(';', message.ToAddresses);
                alreadyExists = await _db.EmailMessages.AnyAsync(m => m.EmailAccountId == emailAccountId
                    && m.Direction == MessageDirection.Inbound && m.MessageId == null
                    && m.FromAddress == from && m.Subject == message.Subject && m.ReceivedAt == message.ReceivedAt && m.ToAddresses == to
                    && m.BodyText == message.BodyText, cancellationToken);
            }
        }

        if (alreadyExists)
        {
            _db.EmailIntakeLogs.Add(new EmailIntakeLog
            {
                EmailAccountId = emailAccountId,
                ProviderMessageId = message.ProviderMessageId,
                Outcome = EmailIntakeOutcome.Skipped_Duplicate,
            });
            return PersistOutcome.Duplicate;
        }

        // Q3 - an ignored/internal sender never starts an inquiry, but a reply inside an existing
        // inquiry's conversation must stay linked to it, so it is classified (and linked) instead of ignored.
        var ignored = ignoredSenders.Matches(message.FromAddress)
            && !(await SystemSettingsService.GetBoolAsync(_db, SystemSettingKeys.SupervisionEnabled, cancellationToken)
                 && await Inquiries.ConversationThreadMatcher.FindCaseAsync(_db, emailAccountId, message.ThreadId, message.InReplyTo,
                        message.References.Count > 0 ? string.Join(' ', message.References) : null, cancellationToken) is not null);

        var entity = new EmailMessage
        {
            EmailAccountId = emailAccountId,
            Provider = protocol,
            ProviderMessageId = message.ProviderMessageId,
            MessageId = Truncate(message.MessageId, 998),
            ThreadId = message.ThreadId,
            InReplyTo = Truncate(message.InReplyTo, 998),
            References = message.References.Count > 0 ? string.Join(' ', message.References) : null,
            FromAddress = Truncate(message.FromAddress, 320) ?? string.Empty,
            FromDisplayName = message.FromDisplayName,
            ToAddresses = string.Join(';', message.ToAddresses),
            CcAddresses = message.CcAddresses.Count > 0 ? string.Join(';', message.CcAddresses) : null,
            Subject = message.Subject,
            BodyText = message.BodyText,
            BodyHtml = message.BodyHtml,
            ReceivedAt = message.ReceivedAt,
            AttachmentCount = message.Attachments.Count,
            ProcessingStatus = ignored
                ? EmailProcessingStatus.Ignored
                : processEmailsReceivedAfter is DateTimeOffset cutoff && message.ReceivedAt < cutoff
                    ? EmailProcessingStatus.Historical
                    : EmailProcessingStatus.PendingClassification,
        };

        foreach (var attachment in message.Attachments)
        {
            entity.Attachments.Add(new EmailAttachmentMetadata
            {
                FileName = Truncate(attachment.FileName, 512) ?? "unnamed",
                ContentType = attachment.ContentType,
                SizeBytes = attachment.SizeBytes,
            });
        }

        _db.EmailMessages.Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException saveEx)
        {
            // Requirements §22 — the unique index on (EmailAccountId, ProviderMessageId) is the
            // authoritative idempotency guard, not the in-memory AnyAsync check above (which has
            // a race window under concurrent/overlapping intake runs). Detach the failed insert,
            // then re-check the database directly: if the row now exists, a concurrent run won
            // the race and this is a genuine duplicate, not a real failure. If it still doesn't
            // exist, the save failed for some other reason and must be surfaced, not swallowed.
            _db.EmailMessages.Entry(entity).State = EntityState.Detached;
            foreach (var attachment in entity.Attachments)
            {
                _db.EmailAttachmentMetadata.Entry(attachment).State = EntityState.Detached;
            }

            // If this query itself fails the database is unreachable: that is not the message's fault, the
            // exception propagates, the watermark stays put and the whole batch is retried next run.
            var existsNow = await _db.EmailMessages
                .AnyAsync(m => m.EmailAccountId == emailAccountId && m.ProviderMessageId == message.ProviderMessageId, cancellationToken);

            if (!existsNow)
            {
                // The database answers but refuses this one message (a value it cannot store). Stopping
                // here would block every email behind it forever, so record it as Failed - visible, with
                // sender and subject - and carry on with the rest.
                return await RecordUnstorableMessageAsync(emailAccountId, protocol, message, saveEx, cancellationToken);
            }

            _db.EmailIntakeLogs.Add(new EmailIntakeLog
            {
                EmailAccountId = emailAccountId,
                ProviderMessageId = message.ProviderMessageId,
                Outcome = EmailIntakeOutcome.Skipped_Duplicate,
                Detail = $"Detected via unique constraint after save failure (concurrent intake run). Original error: {Truncate(saveEx.InnerException?.Message ?? saveEx.Message, 500)}",
            });
            await _db.SaveChangesAsync(cancellationToken);
            return PersistOutcome.Duplicate;
        }

        _db.EmailIntakeLogs.Add(new EmailIntakeLog
        {
            EmailAccountId = emailAccountId,
            ProviderMessageId = message.ProviderMessageId,
            Outcome = EmailIntakeOutcome.Fetched,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return PersistOutcome.Persisted;
    }

    private async Task<PersistOutcome> RecordUnstorableMessageAsync(
        Guid emailAccountId, Iemas.Domain.Email.EmailProtocol protocol, ProviderMessage message, DbUpdateException cause, CancellationToken cancellationToken)
    {
        var error = Truncate(cause.InnerException?.Message ?? cause.Message, 1000);
        _logger.LogError(cause, "Message {ProviderMessageId} of account {EmailAccountId} could not be stored", message.ProviderMessageId, emailAccountId);

        // A minimal stand-in so the email still shows up as Failed for a person to open on the mail server.
        var placeholder = new EmailMessage
        {
            EmailAccountId = emailAccountId,
            Provider = protocol,
            ProviderMessageId = Truncate(message.ProviderMessageId, 512) ?? string.Empty,
            FromAddress = Truncate(message.FromAddress, 320) ?? string.Empty,
            ToAddresses = Truncate(string.Join(';', message.ToAddresses), 2000) ?? string.Empty,
            Subject = Truncate(message.Subject, 500) ?? string.Empty,
            ReceivedAt = message.ReceivedAt,
            ProcessingStatus = EmailProcessingStatus.Failed,
            ProcessingError = $"Could not be stored: {error}",
        };
        _db.EmailMessages.Add(placeholder);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.EmailMessages.Entry(placeholder).State = EntityState.Detached;
        }

        _db.EmailIntakeLogs.Add(new EmailIntakeLog
        {
            EmailAccountId = emailAccountId,
            ProviderMessageId = message.ProviderMessageId,
            Outcome = EmailIntakeOutcome.Failed,
            Detail = $"Could not be stored (from {Truncate(message.FromAddress, 200)}): {error}",
        });
        await _db.SaveChangesAsync(cancellationToken);
        return PersistOutcome.Failed;
    }

    private async Task<IntakeRunResult> RecordAccountFailureAsync(
        Guid emailAccountId, string error, Stopwatch stopwatch, CancellationToken cancellationToken, EmailSyncState? syncState = null)
    {
        syncState ??= await _db.EmailSyncStates.FirstOrDefaultAsync(s => s.EmailAccountId == emailAccountId, cancellationToken);
        if (syncState is not null)
        {
            syncState.LastSyncError = Truncate(error, 2000);
            syncState.ConsecutiveFailureCount++;
            await _db.SaveChangesAsync(cancellationToken);
        }

        _db.EmailIntakeLogs.Add(new EmailIntakeLog
        {
            EmailAccountId = emailAccountId,
            Outcome = EmailIntakeOutcome.Failed,
            Detail = Truncate(error, 2000),
            DurationMs = stopwatch.ElapsedMilliseconds,
        });
        await _db.SaveChangesAsync(cancellationToken);

        // Never logs the account's credential/secret — only the account ID and the adapter's own
        // error message (already vetted not to contain the secret; see ImapEmailProviderAdapter).
        _logger.LogWarning(
            "IMAP sync for account {EmailAccountId} failed after {ElapsedMilliseconds}ms: {Error}",
            emailAccountId, stopwatch.ElapsedMilliseconds, error);

        stopwatch.Stop();
        return new IntakeRunResult(emailAccountId, false, 0, 0, 0, 0, error, stopwatch.ElapsedMilliseconds);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
