using Iemas.Application.Common.Providers;
using Iemas.Application.EmailIntake;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.EmailIntake;

public class EmailIntakeServiceTests
{
    private static EmailAccount CreateAccount()
    {
        return new EmailAccount
        {
            EmailAddress = "sales@sawo.com",
            Purpose = EmailAccountPurpose.Inbound,
            Protocol = EmailProtocol.Imap,
            Host = "imap.example.com",
            Port = 993,
            Encryption = "SSL/TLS",
            Username = "sales@sawo.com",
            AuthMethod = EmailAuthMethod.Password,
            IsActive = true,
            MonitoringEnabled = true,
            Credential = new EmailCredential
            {
                EncryptedSecret = System.Text.Encoding.UTF8.GetBytes("password"),
                Nonce = Array.Empty<byte>(),
                Tag = Array.Empty<byte>(),
                KeyId = "test",
            },
        };
    }

    private static ProviderMessage Message(string providerId, string subject = "Test", DateTimeOffset? receivedAt = null) => new(
        providerId, $"<{providerId}@example.com>", null, null, Array.Empty<string>(),
        "customer@example.com", "Customer", new[] { "sales@sawo.com" }, Array.Empty<string>(),
        subject, "Body text", null, receivedAt ?? DateTimeOffset.UtcNow, Array.Empty<ProviderAttachmentSummary>());

    /// <summary>Completion gate Q3/Q5 — new messages are persisted with required identifiers preserved.</summary>
    [Fact]
    public async Task RunForAccountAsync_PersistsNewMessagesWithRequiredFields()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(
                1, 1, new[] { Message("101") }, Array.Empty<(uint, string)>()))
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);

        var result = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.PersistedCount);
        Assert.Equal(0, result.DuplicateCount);

        var stored = await db.EmailMessages.SingleAsync();
        Assert.Equal("101", stored.ProviderMessageId);
        Assert.Equal("customer@example.com", stored.FromAddress);
        Assert.Equal("sales@sawo.com", stored.ToAddresses);
        Assert.Equal("Test", stored.Subject);
        Assert.Equal(EmailProcessingStatus.PendingClassification, stored.ProcessingStatus);
    }

    /// <summary>Completion gate Q4 — the same ProviderMessageId must never create two rows.</summary>
    [Fact]
    public async Task RunForAccountAsync_DoesNotCreateDuplicateOnRepeatedFetchOfSameMessage()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, afterUid, _, _) => Task.FromResult(new FetchInboxResult(
                1, 1, new[] { Message("101") }, Array.Empty<(uint, string)>()))
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);

        // Simulate the adapter returning the same message twice across two separate runs
        // (e.g. a watermark that didn't advance, or a provider re-delivering the same UID).
        await service.RunForAccountAsync(account.Id, CancellationToken.None);
        var second = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.Equal(1, second.DuplicateCount);
        Assert.Equal(0, second.PersistedCount);
        Assert.Equal(1, await db.EmailMessages.CountAsync());
    }

    /// <summary>Completion gate Q7 — one malformed message must not stop the rest of the batch.</summary>
    [Fact]
    public async Task RunForAccountAsync_PersistsGoodMessagesEvenWhenOthersAreMalformed()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(
                1, 3,
                new[] { Message("101"), Message("103") },
                new[] { (102u, "Malformed MIME structure") }))
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);

        var result = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.PersistedCount);
        Assert.Equal(1, result.MalformedCount);
        Assert.Equal(2, await db.EmailMessages.CountAsync());

        var malformedLog = await db.EmailIntakeLogs.SingleAsync(l => l.Outcome == EmailIntakeOutcome.Skipped_Malformed);
        Assert.Equal("102", malformedLog.ProviderMessageId);
    }

    /// <summary>Completion gate Q9 — a provider/connection failure must not corrupt the resume watermark.</summary>
    [Fact]
    public async Task RunForAccountAsync_OnProviderFailure_LeavesWatermarkUntouchedAndRecordsFailure()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        var syncState = new EmailSyncState { EmailAccountId = account.Id, LastUidValidity = 1, LastSeenUid = 50 };
        db.EmailSyncStates.Add(syncState);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => throw new IOException("Connection reset by peer")
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);

        var result = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Connection reset", result.Error);

        var reloaded = await db.EmailSyncStates.SingleAsync(s => s.EmailAccountId == account.Id);
        // §44 — mailbox unavailability must never be treated as "no messages"; the watermark
        // must be exactly what it was before this failed attempt, so the next run retries the
        // same range instead of silently skipping past unfetched messages.
        Assert.Equal(50u, reloaded.LastSeenUid);
        Assert.Equal(1u, reloaded.LastUidValidity);
        Assert.Equal(1, reloaded.ConsecutiveFailureCount);
        Assert.Contains("Connection reset", reloaded.LastSyncError);

        var failureLog = await db.EmailIntakeLogs.SingleAsync(l => l.Outcome == EmailIntakeOutcome.Failed);
        Assert.Contains("Connection reset", failureLog.Detail);
    }

    /// <summary>Completion gate Q11 — the watermark advances so a subsequent run resumes, not re-scans.</summary>
    [Fact]
    public async Task RunForAccountAsync_AdvancesWatermark_SoNextRunOnlyFetchesNewerMessages()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        uint? capturedAfterUid = null;
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, afterUid, _, _) =>
            {
                capturedAfterUid = afterUid;
                return Task.FromResult(new FetchInboxResult(1, 5, new[] { Message("105") }, Array.Empty<(uint, string)>()));
            }
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);

        await service.RunForAccountAsync(account.Id, CancellationToken.None);
        await service.RunForAccountAsync(account.Id, CancellationToken.None);

        // Second call must have passed the watermark left by the first call (5), not null/0.
        Assert.Equal(5u, capturedAfterUid);
    }

    /// <summary>Completion gate — deactivated/monitoring-disabled accounts must not be polled, and a manual run says why instead of looking like an empty inbox.</summary>
    [Fact]
    public async Task RunForAccountAsync_DoesNotPoll_AndExplains_WhenAccountIsNotActivelyMonitored()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        account.MonitoringEnabled = false;
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => throw new InvalidOperationException("Should never be called")
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);

        var result = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.FetchedCount);
        Assert.Contains("Monitoring is off", result.Error);
        Assert.False(await db.EmailIntakeLogs.AnyAsync());
    }

    /// <summary>§81 — a run that stops early (connection drop / time budget) keeps the adapter's watermark, so the remaining messages are fetched next run.</summary>
    [Fact]
    public async Task RunForAccountAsync_StoppedEarly_KeepsWatermarkAndReportsWhy()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(
                7, 101, new[] { Message("100"), Message("101") }, Array.Empty<(uint, string)>(),
                "Stopped at message 102 (The ImapClient is not connected.); it and the rest continue on the next run.")),
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);

        var result = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.PersistedCount);
        Assert.Contains("Stopped at message 102", result.Error);
        var state = await db.EmailSyncStates.SingleAsync();
        Assert.Equal(101u, state.LastSeenUid);
        Assert.Equal(0, await db.EmailIntakeLogs.CountAsync(l => l.Outcome == EmailIntakeOutcome.Skipped_Malformed));
    }

    /// <summary>Completion gate Q1/Q2 boundary — RunAllAsync only targets active Inbound monitored accounts.</summary>
    [Fact]
    public async Task RunAllAsync_OnlyProcessesActiveMonitoredInboundAccounts()
    {
        using var db = TestDbContext.CreateNew();

        var monitored = CreateAccount();
        var notMonitored = CreateAccount();
        notMonitored.EmailAddress = "other@sawo.com";
        notMonitored.MonitoringEnabled = false;
        var outbound = CreateAccount();
        outbound.EmailAddress = "notify@sawo.com";
        outbound.Purpose = EmailAccountPurpose.Outbound;
        outbound.MonitoringEnabled = false;

        db.EmailAccounts.AddRange(monitored, notMonitored, outbound);
        await db.SaveChangesAsync();

        var processedAccountIds = new List<Guid>();
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (settings, _, _, _, _) => Task.FromResult(new FetchInboxResult(1, null, Array.Empty<ProviderMessage>(), Array.Empty<(uint, string)>()))
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);

        var results = await service.RunAllAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(monitored.Id, results[0].EmailAccountId);
    }

    // --- Observability (Phase 10 hardening) — IMAP sync duration/result logging ---

    [Fact]
    public async Task RunForAccountAsync_OnSuccess_LogsSyncDurationAndResultCounts()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(
                1, 1, new[] { Message("101") }, Array.Empty<(uint, string)>()))
        };
        var logger = new CapturingLogger<EmailIntakeService>();
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), logger);

        await service.RunForAccountAsync(account.Id, CancellationToken.None);

        var logLine = Assert.Single(logger.Entries, e => e.Message.Contains("IMAP sync") && e.Message.Contains("completed"));
        Assert.Contains("1 fetched", logLine.Message);
        Assert.Contains("1 persisted", logLine.Message);
        // Must never log the decrypted secret/credential material.
        Assert.DoesNotContain("password", logLine.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunForAccountAsync_OnFailure_LogsWarningWithDurationAndError_ButNeverTheCredential()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => throw new IOException("Connection reset by peer")
        };
        var logger = new CapturingLogger<EmailIntakeService>();
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), logger);

        await service.RunForAccountAsync(account.Id, CancellationToken.None);

        var logLine = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("Connection reset by peer", logLine.Message);
        Assert.DoesNotContain("password", logLine.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PostgreSQL refuses NUL and unpaired surrogates; one such email must never block the mailbox.</summary>
    [Theory]
    [InlineData("a\0b", "ab")]
    [InlineData("\0\0", "")]
    [InlineData("ok 🔥 fine", "ok 🔥 fine")]
    public void Clean_RemovesWhatPostgresCannotStore(string input, string expected)
    {
        Assert.Equal(expected, EmailIntakeService.Clean(input));
    }

    [Fact]
    public async Task RunForAccountAsync_StoresMessageContainingNulBytes_AndStillStoresTheOnesBehindIt()
    {
        using var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var nul = Message("101", subject: "Quote\0 please") with { BodyText = "Phone 0912\0345" };
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(
                1, 102, new[] { nul, Message("102") }, Array.Empty<(uint, string)>()))
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);

        var result = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.PersistedCount);
        var stored = await db.EmailMessages.SingleAsync(m => m.ProviderMessageId == "101");
        Assert.Equal("Quote please", stored.Subject);
        Assert.Equal("Phone 0912345", stored.BodyText);
    }

    [Fact]
    public void Clean_ReplacesUnpairedSurrogates()
    {
        var high = ((char)0xD83D).ToString();
        var low = ((char)0xDD25).ToString();
        Assert.Equal("lone \uFFFD end", EmailIntakeService.Clean("lone " + high + " end"));
        Assert.Equal("lone \uFFFD end", EmailIntakeService.Clean("lone " + low + " end"));
        Assert.Equal("tail \uFFFD", EmailIntakeService.Clean("tail " + high));
        Assert.Equal("pair " + high + low, EmailIntakeService.Clean("pair " + high + low));
    }

    private static async Task<(TestDbContext Db, EmailIntakeService Service, Guid AccountId)> Arrange(params ProviderMessage[] messages)
    {
        var db = TestDbContext.CreateNew();
        var account = CreateAccount();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(1, 999, messages, Array.Empty<(uint, string)>()))
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter), Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);
        return (db, service, account.Id);
    }

    /// <summary>A reused Message-ID must not make intake discard a different customer's inquiry.</summary>
    [Fact]
    public async Task ReusedMessageId_FromADifferentCustomer_IsStoredNotDropped()
    {
        var a = Message("1", "Quote") with { MessageId = "<same@mailer>", FromAddress = "alice@customer.example" };
        var b = Message("2", "Quote") with { MessageId = "<same@mailer>", FromAddress = "bob@other.example" };
        var (db, service, id) = await Arrange(a, b);
        using var _ = db;

        var result = await service.RunForAccountAsync(id, CancellationToken.None);

        Assert.Equal(2, result.PersistedCount);
        Assert.Equal(0, result.DuplicateCount);
    }

    [Fact]
    public async Task SameMessageIdSenderAndSubject_UnderANewProviderId_IsStillADuplicate()
    {
        var first = Message("1", "Quote");
        var (db, service, id) = await Arrange(first, first with { ProviderMessageId = "77" }); // e.g. UID reset after a mailbox move
        using var _ = db;

        var result = await service.RunForAccountAsync(id, CancellationToken.None);

        Assert.Equal(1, result.PersistedCount);
        Assert.Equal(1, result.DuplicateCount);
    }

    [Fact]
    public async Task NoMessageId_SameSecondAndSubject_ButDifferentBody_AreBothStored()
    {
        var at = DateTimeOffset.UtcNow;
        var a = Message("1", "Website enquiry", at) with { MessageId = null, FromAddress = "form@customer.example", BodyText = "Customer A wants 5 heaters" };
        var b = a with { ProviderMessageId = "2", BodyText = "Customer B wants 50 stoves" };
        var (db, service, id) = await Arrange(a, b);
        using var _ = db;

        var result = await service.RunForAccountAsync(id, CancellationToken.None);

        Assert.Equal(2, result.PersistedCount);
    }

    [Theory]
    [InlineData(-3650, 0, true, "delivered")]   // Date header ten years back, mail arrived after the cut-off: not history
    [InlineData(400, 0, false, "delivered")]    // Date header in the future: deadline would never fire
    [InlineData(-5, 0, false, "header")]        // ordinary
    [InlineData(-3, 0, true, "header")]         // delayed delivery that is still after the cut-off keeps its date
    public void ReconcileReceivedAt_CorrectsOnlyWrongSenderClocks(int headerOffsetDays, int _, bool withCutoff, string expected)
    {
        var delivered = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        var cutoff = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var header = delivered.AddDays(headerOffsetDays);
        var actual = Iemas.Infrastructure.Providers.ImapEmailProviderAdapter.ReconcileReceivedAt(header, delivered, withCutoff ? cutoff : null);
        Assert.Equal(expected == "delivered" ? delivered : header, actual);
    }

    [Fact]
    public void ReconcileReceivedAt_WithoutDeliveryTime_KeepsTheHeader()
    {
        var header = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(header, Iemas.Infrastructure.Providers.ImapEmailProviderAdapter.ReconcileReceivedAt(header, null, DateTimeOffset.UtcNow));
    }
}
