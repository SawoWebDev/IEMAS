using System.Diagnostics;
using System.Text.RegularExpressions;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iemas.Application.Inquiries;

public sealed record ConversationSyncRunResult(int CasesChecked, int AgentMessagesStored, int InternalMessagesStored, int MailboxFailures, long DurationMs);

/// <summary>
/// Phase 2 (G5) - keeps the agent's side of every open inquiry's conversation. Unlike the older
/// reply check, which stopped at the first reply, this reads the Sent folder for every inquiry that
/// is still supervised and stores each agent message that belongs to it (read-only; IEMAS never
/// sends or changes customer email). Each stored message advances the lifecycle: the first reply
/// is "Initial response sent", later ones "Agent follow-up sent", internal-only forwards are
/// internal activity.
///
/// Only messages that belong to a supervised inquiry are stored - unrelated sent mail is never
/// kept. The text is masked and encrypted (Q4); attachments are counted, never stored.
/// </summary>
public class ConversationSyncService
{
    private const int MaxSentMessagesPerMailbox = 200;

    /// <summary>
    /// A resolved or closed inquiry keeps being synced for this long after it closed: catches the reply
    /// sent just before "Confirm resolved", and a mail client (or Outlook on a POP mailbox) that uploads
    /// its Sent copy late.
    /// </summary>
    public static readonly TimeSpan LateReplyWindow = TimeSpan.FromDays(1);

    /// <summary>Inside the grace window a closed inquiry is re-read at most this often (the first read after closing is immediate).</summary>
    public static readonly TimeSpan ClosedRecheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan JustClosedWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SyncOverlap = TimeSpan.FromDays(1);
    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);

    private readonly IAppDbContext _db;
    private readonly ICredentialEncryptionService _encryption;
    private readonly IEmailProviderAdapterResolver _adapters;
    private readonly InquiryConversationService _conversation;
    private readonly AgentReplyProtector _protector;
    private readonly ILogger<ConversationSyncService>? _logger;

    public ConversationSyncService(
        IAppDbContext db, ICredentialEncryptionService encryption, IEmailProviderAdapterResolver adapters,
        InquiryConversationService conversation, AgentReplyProtector protector, ILogger<ConversationSyncService>? logger = null)
    {
        _db = db;
        _encryption = encryption;
        _adapters = adapters;
        _conversation = conversation;
        _protector = protector;
        _logger = logger;

        // The confirm/reject/complete paths sync the mailbox before finalising (see InquiryConversationService).
        _conversation.UseSync(this);
    }

    /// <summary>
    /// Event-triggered sync of ONE inquiry's mailbox (the agent confirms or rejects a resolution, completes
    /// the inquiry, or takes an action). Reads the Sent folder now instead of waiting for the 5-minute job,
    /// so the agent's last reply is stored before the inquiry is finalised. Idempotent (a message already
    /// stored is skipped) and never throws: a failure is logged, reported in the result and leaves the
    /// state untouched, so the calling action can carry on.
    /// </summary>
    public async Task<ConversationSyncRunResult> SyncCaseAsync(Guid caseId, CancellationToken cancellationToken, DateTimeOffset? at = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var accountId = await _db.Cases.Where(c => c.Id == caseId).Select(c => (Guid?)c.EmailAccountId).FirstOrDefaultAsync(cancellationToken);
            if (accountId is null) return new ConversationSyncRunResult(0, 0, 0, 0, stopwatch.ElapsedMilliseconds);
            var (agent, internalCount, failed) = await SyncAccountAsync(accountId.Value, new List<Guid> { caseId }, at ?? DateTimeOffset.UtcNow, cancellationToken);
            return new ConversationSyncRunResult(1, agent, internalCount, failed ? 1 : 0, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Event-triggered conversation sync of case {CaseId} failed; continuing", caseId);
            await DiscardPendingChangesAsync();
            return new ConversationSyncRunResult(1, 0, 0, 1, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// After a failed save nothing half-applied may linger in the unit of work: it would be written by the
    /// next, unrelated SaveChanges (for instance "Replied" without its verification record, which the
    /// database refuses). Drops the messages this run added and reloads the inquiries it changed.
    /// </summary>
    private async Task DiscardPendingChangesAsync()
    {
        if (_db is not DbContext context) return;
        try
        {
            context.ChangeTracker.DetectChanges();
            foreach (var entry in context.ChangeTracker.Entries().ToList())
            {
                if (entry.State == EntityState.Added && entry.Entity is EmailMessage or CaseEmail or ReplyVerificationAttempt or CaseEvent)
                    entry.State = EntityState.Detached;
                else if (entry.State == EntityState.Modified && entry.Entity is Case)
                    await entry.ReloadAsync();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not discard the pending changes of a failed conversation sync");
        }
    }

    /// <param name="at">The clock (tests pass an explicit time; the scheduled job uses now).</param>
    public async Task<ConversationSyncRunResult> RunAsync(int batchSize, CancellationToken cancellationToken, DateTimeOffset? at = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var openCases = await _db.Cases
            .Where(c => c.Stage != InquiryStage.Resolved && c.Stage != InquiryStage.Closed)
            .OrderBy(c => c.ReplyLastCheckedAt ?? DateTimeOffset.MinValue)
            .Take(batchSize)
            .Select(c => new { c.Id, c.EmailAccountId })
            .ToListAsync(cancellationToken);

        // An agent often sends the final answer and confirms "resolved" minutes later, before this job
        // has read the Sent folder (and the confirm path syncs only what the server could see at that
        // moment). A closed inquiry therefore stays in the check for the grace window after it closed
        // (re-read every ClosedRecheckInterval, and once more just after closing) so late-sent or
        // late-uploaded replies are still recorded. Closed inquiries never change stage because of it.
        var clock = at ?? DateTimeOffset.UtcNow;
        var graceStart = clock - LateReplyWindow;
        var recheckBefore = clock - ClosedRecheckInterval;
        var recentlyClosed = await _db.Cases
            .Where(c => (c.Stage == InquiryStage.Resolved || c.Stage == InquiryStage.Closed)
                && c.StageChangedAt >= graceStart
                && (c.ReplyLastCheckedAt == null
                    || c.ReplyLastCheckedAt < c.StageChangedAt + JustClosedWindow
                    || c.ReplyLastCheckedAt < recheckBefore))
            .OrderBy(c => c.ReplyLastCheckedAt ?? DateTimeOffset.MinValue)
            .Take(batchSize)
            .Select(c => new { c.Id, c.EmailAccountId })
            .ToListAsync(cancellationToken);
        openCases.AddRange(recentlyClosed);

        int agentStored = 0, internalStored = 0, failures = 0;
        foreach (var group in openCases.GroupBy(c => c.EmailAccountId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (agent, internalCount, failed) = await SyncAccountAsync(group.Key, group.Select(c => c.Id).ToList(), clock, cancellationToken);
            agentStored += agent;
            internalStored += internalCount;
            if (failed) failures++;
        }

        return new ConversationSyncRunResult(openCases.Count, agentStored, internalStored, failures, stopwatch.ElapsedMilliseconds);
    }

    private async Task<(int Agent, int Internal, bool Failed)> SyncAccountAsync(Guid accountId, List<Guid> caseIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var account = await _db.EmailAccounts.Include(a => a.Credential).FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        var cases = await _db.Cases.Where(c => caseIds.Contains(c.Id)).ToListAsync(cancellationToken);

        // Sample mailboxes have no server and nobody can reply from them.
        if (account is null || account.Host == Operations.SampleDataService.SampleMailboxHost || account.Credential is null)
        {
            foreach (var c in cases) c.ReplyLastCheckedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return (0, 0, account is not null && account.Host != Operations.SampleDataService.SampleMailboxHost);
        }

        IReadOnlyCollection<ProviderMessage> sent;
        try
        {
            var secret = _encryption.Decrypt(new(account.Credential.EncryptedSecret, account.Credential.Nonce, account.Credential.Tag, account.Credential.KeyId));
            var settings = new EmailProviderConnectionSettings(account.Protocol, account.Host, account.Port, account.Encryption, account.Username, account.AuthMethod, secret);
            var since = cases.Min(c => c.ConversationSyncedThrough is DateTimeOffset through ? through - SyncOverlap : c.FirstEmailReceivedAt);
            var result = await _adapters.Resolve(account.Protocol).FetchSentMessagesAsync(settings, since, MaxSentMessagesPerMailbox, cancellationToken);
            if (!result.FolderAccessible)
            {
                _logger?.LogWarning("Sent folder of {Mailbox} not accessible: {Error}", account.EmailAddress, result.AccessError);
                return (0, 0, true);
            }
            sent = result.Messages;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never treated as "no reply": nothing changes, the next run tries again.
            _logger?.LogWarning(ex, "Sent folder of {Mailbox} could not be read", account.EmailAddress);
            return (0, 0, true);
        }

        try
        {
            var (agentStored, internalStored) = await AttachSentAsync(account, cases, sent, "sent:", cancellationToken);

            foreach (var c in cases) c.ReplyLastCheckedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return (agentStored, internalStored, false);
        }
        catch (DbUpdateException ex)
        {
            // Typically a concurrent run (the 5-minute job and a confirm) stored the same message first and
            // the unique index refused the second copy. Nothing is lost: the winner's rows exist, and the
            // next run skips them.
            _logger?.LogWarning(ex, "Storing sent messages of {Mailbox} hit a database conflict; will retry on the next run", account.EmailAddress);
            await DiscardPendingChangesAsync();
            return (0, 0, true);
        }
    }

    /// <summary>
    /// Sent emails reported by the employee's Windows Agent, read from Outlook's own Sent Items. Needed when
    /// the mailbox is set up as POP in Outlook: the replies are then saved only on the agent's PC and never
    /// reach the server's Sent folder. Only the employee's own open inquiries are considered, so an Agent can
    /// never attach mail to someone else's inquiry; the same matching rules as the Sent-folder check apply.
    /// </summary>
    public async Task<(int Agent, int Internal)> IngestFromAgentAsync(Guid employeeId, IReadOnlyCollection<ProviderMessage> sent, CancellationToken cancellationToken)
    {
        if (sent.Count == 0) return (0, 0);
        // Outlook's Sent Items reach the server when the Agent next reports, possibly after the agent
        // confirmed the inquiry resolved: inquiries closed in the last day still take their own replies.
        var closedSince = DateTimeOffset.UtcNow - LateReplyWindow;
        var cases = await _db.Cases
            .Where(c => c.OwnerEmployeeId == employeeId
                && ((c.Stage != InquiryStage.Resolved && c.Stage != InquiryStage.Closed) || c.StageChangedAt >= closedSince))
            .ToListAsync(cancellationToken);

        int agentStored = 0, internalStored = 0;
        foreach (var group in cases.GroupBy(c => c.EmailAccountId))
        {
            var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.Id == group.Key, cancellationToken);
            if (account is null) continue;
            // Only mail sent from this monitored mailbox (Outlook may hold other accounts, e.g. a personal one).
            var fromMailbox = sent.Where(m => string.Equals(m.FromAddress, account.EmailAddress, StringComparison.OrdinalIgnoreCase)).ToList();
            if (fromMailbox.Count == 0) continue;
            var (agent, internalCount) = await AttachSentAsync(account, group.ToList(), fromMailbox, "outlook:", cancellationToken);
            agentStored += agent;
            internalStored += internalCount;
        }
        await _db.SaveChangesAsync(cancellationToken);
        if (agentStored + internalStored > 0)
            _logger?.LogInformation("Windows Agent reported {Agent} reply(ies) and {Internal} internal email(s) for employee {EmployeeId}", agentStored, internalStored, employeeId);
        return (agentStored, internalStored);
    }

    /// <summary>Stores every sent message that belongs to one of <paramref name="cases"/> (all from <paramref name="account"/>) and advances the lifecycle.</summary>
    private async Task<(int Agent, int Internal)> AttachSentAsync(
        EmailAccount account, List<Case> cases, IEnumerable<ProviderMessage> sent, string idPrefix, CancellationToken cancellationToken)
    {
        var accountId = account.Id;
        var caseIds = cases.Select(c => c.Id).ToList();
        var links = await _db.CaseEmails
            .Where(ce => caseIds.Contains(ce.CaseId))
            .Select(ce => new { ce.CaseId, ce.EmailMessage.MessageId, ce.EmailMessage.ThreadId })
            .ToListAsync(cancellationToken);

        int agentStored = 0, internalStored = 0;
        // Messages stored earlier in THIS run are not visible to the database queries yet (unsaved), so a
        // message listed twice by the provider (or from both the server and Outlook) is tracked here.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in sent.OrderBy(m => m.ReceivedAt))
        {
            var providerId = $"{idPrefix}{message.ProviderMessageId}";
            var hasMessageId = !string.IsNullOrWhiteSpace(message.MessageId);
            var toJoined = string.Join(';', message.ToAddresses);
            var fingerprint = hasMessageId
                ? $"id:{message.MessageId}"
                : $"fp:{message.FromAddress}|{message.Subject}|{message.ReceivedAt.UtcTicks}|{toJoined}";
            if (!seen.Add(fingerprint) | !seen.Add($"pm:{providerId}")) continue;

            // Idempotency: by provider id, by Message-ID, and - for a message with no Message-ID - by a
            // fingerprint of sender, subject, time and recipients.
            var known = await _db.EmailMessages.AnyAsync(m => m.EmailAccountId == accountId
                && (m.ProviderMessageId == providerId
                    || (hasMessageId && m.MessageId == message.MessageId)
                    || (!hasMessageId && m.Direction == MessageDirection.Outbound && m.FromAddress == message.FromAddress
                        && m.Subject == message.Subject && m.ReceivedAt == message.ReceivedAt && m.ToAddresses == toJoined)), cancellationToken);
            if (known) continue;

            var (target, signal, detail) = Assign(message, cases, links.Select(l => (l.CaseId, l.MessageId, l.ThreadId)).ToList());
            if (target is null) continue;

            var role = await _conversation.OutboundRoleAsync(target.CustomerEmailAddress, message.ToAddresses.Concat(message.CcAddresses), cancellationToken);
            var entity = new EmailMessage
            {
                EmailAccountId = accountId,
                Provider = account.Protocol,
                ProviderMessageId = providerId,
                MessageId = message.MessageId,
                ThreadId = message.ThreadId,
                InReplyTo = message.InReplyTo,
                References = message.References.Count > 0 ? string.Join(' ', message.References) : null,
                FromAddress = message.FromAddress,
                FromDisplayName = message.FromDisplayName,
                ToAddresses = string.Join(';', message.ToAddresses),
                CcAddresses = message.CcAddresses.Count > 0 ? string.Join(';', message.CcAddresses) : null,
                Subject = message.Subject,
                BodyText = null,
                BodyHtml = null,
                BodyTextEncrypted = _protector.Protect(message.BodyText ?? HtmlTag.Replace(message.BodyHtml ?? string.Empty, " ")),
                ReceivedAt = message.ReceivedAt,
                AttachmentCount = message.Attachments.Count,
                Direction = MessageDirection.Outbound,
                ProcessingStatus = EmailProcessingStatus.Outbound,
                CaseId = target.Id,
            };
            _db.EmailMessages.Add(entity);
            _db.CaseEmails.Add(new CaseEmail { CaseId = target.Id, EmailMessageId = entity.Id, MatchSignal = signal, MatchDetail = detail, Role = role });
            links.Add(new { CaseId = target.Id, entity.MessageId, entity.ThreadId });

            _conversation.OnOutboundAttached(target, entity, role);
            if (role == ConversationRole.Agent)
            {
                agentStored++;
                var caseId = target.Id;
                if (!await _db.ReplyVerificationAttempts.AnyAsync(a => a.CaseId == caseId && a.Outcome == ReplyVerificationOutcome.VerifiedReply, cancellationToken))
                {
                    _db.ReplyVerificationAttempts.Add(new ReplyVerificationAttempt
                    {
                        CaseId = caseId,
                        Outcome = ReplyVerificationOutcome.VerifiedReply,
                        MatchedSentMessageId = message.MessageId,
                        MatchSignal = ToReplySignal(signal),
                        MatchDetail = detail,
                    });
                    // Saved before the status changes: the database trigger requires the record to exist first.
                    await _db.SaveChangesAsync(cancellationToken);
                }
                target.ReplyStatus = CaseReplyStatus.Replied;
            }
            else
            {
                internalStored++;
            }

            target.LastActivityAt = message.ReceivedAt > target.LastActivityAt ? message.ReceivedAt : target.LastActivityAt;
            target.ConversationSyncedThrough = target.ConversationSyncedThrough is DateTimeOffset t && t > message.ReceivedAt ? t : message.ReceivedAt;
        }

        return (agentStored, internalStored);
    }

    /// <summary>
    /// Which open inquiry a sent message belongs to: a real thread relationship first (thread id,
    /// In-Reply-To, References - including the agent's own earlier replies), then a message to the
    /// customer sent after the inquiry began (the most recently active inquiry for that customer).
    /// Never by subject.
    /// </summary>
    private static (Case? Case, CaseMatchSignal Signal, string? Detail) Assign(
        ProviderMessage message, List<Case> cases, List<(Guid CaseId, string? MessageId, string? ThreadId)> links)
    {
        Case? ById(Guid id) => cases.FirstOrDefault(c => c.Id == id);

        if (!string.IsNullOrWhiteSpace(message.ThreadId))
        {
            var hit = links.FirstOrDefault(l => l.ThreadId == message.ThreadId);
            if (hit.CaseId != Guid.Empty) return (ById(hit.CaseId), CaseMatchSignal.ThreadId, $"ThreadId={message.ThreadId}");
        }

        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            var hit = links.FirstOrDefault(l => l.MessageId == message.InReplyTo);
            if (hit.CaseId != Guid.Empty) return (ById(hit.CaseId), CaseMatchSignal.InReplyTo, $"InReplyTo={message.InReplyTo}");
        }

        var refs = message.References.ToHashSet();
        if (refs.Count > 0)
        {
            var hit = links.LastOrDefault(l => l.MessageId != null && refs.Contains(l.MessageId));
            if (hit.CaseId != Guid.Empty) return (ById(hit.CaseId), CaseMatchSignal.References, "References contains a message of this inquiry");
        }

        // A customer can have several open inquiries. With no thread headers, the one whose subject the
        // message carries wins; otherwise the most recently active one.
        var sentSubject = Cases.CaseMatchingService.NormalizeSubject(message.Subject);
        // A closed inquiry only takes messages that are provably part of its thread (above): a new email
        // to the same customer belongs to whatever they are discussing now, not to a finished inquiry.
        var byRecipient = cases
            .Where(c => !InquiryLifecycle.IsTerminal(c.Stage)
                && message.ReceivedAt >= c.FirstEmailReceivedAt
                && message.ToAddresses.Concat(message.CcAddresses).Any(a => string.Equals(a, c.CustomerEmailAddress, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => sentSubject.Length > 0 && string.Equals(c.NormalizedSubject, sentSubject, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.LastActivityAt)
            .FirstOrDefault();
        return byRecipient is null
            ? (null, CaseMatchSignal.NewCase, null)
            : (byRecipient, CaseMatchSignal.ParticipantAndAccountRelationship, $"Sent to {byRecipient.CustomerEmailAddress}");
    }

    private static ReplyMatchSignal ToReplySignal(CaseMatchSignal signal) => signal switch
    {
        CaseMatchSignal.ThreadId => ReplyMatchSignal.ThreadId,
        CaseMatchSignal.InReplyTo => ReplyMatchSignal.InReplyTo,
        CaseMatchSignal.References => ReplyMatchSignal.References,
        _ => ReplyMatchSignal.RecipientAndAccountRelationship,
    };
}
