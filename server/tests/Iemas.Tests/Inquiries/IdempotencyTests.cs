using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Application.EmailIntake;
using Iemas.Application.Notifications;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Domain.Notifications;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Tests.Inquiries;

/// <summary>Recommendation 7 - a retried or overlapping job can never duplicate an email, a stored message or a notification.</summary>
public class IdempotencyTests
{
    private static EmailAccount Account() => new()
    {
        EmailAddress = "sales@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "imap.example.com",
        Port = 993, Encryption = "SSL/TLS", Username = "sales@sawo.com", AuthMethod = EmailAuthMethod.Password, IsActive = true, MonitoringEnabled = true,
        Credential = new EmailCredential { EncryptedSecret = System.Text.Encoding.UTF8.GetBytes("pw"), Nonce = Array.Empty<byte>(), Tag = Array.Empty<byte>(), KeyId = "test" },
    };

    private static ProviderMessage Message(string providerId, string? messageId, string subject = "Hello", DateTimeOffset? at = null) => new(
        providerId, messageId, null, null, Array.Empty<string>(), "customer@example.com", "Customer",
        new[] { "sales@sawo.com" }, Array.Empty<string>(), subject, "Body", null, at ?? new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
        Array.Empty<ProviderAttachmentSummary>());

    private static async Task<(TestDbContext Db, EmailIntakeService Service, EmailAccount Account, List<ProviderMessage> Fetch)> SetUpAsync()
    {
        var db = TestDbContext.CreateNew();
        var account = Account();
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        var fetch = new List<ProviderMessage>();
        var adapter = new FakeEmailProviderAdapter
        {
            FetchBehavior = (_, _, _, _, _) => Task.FromResult(new FetchInboxResult(1, 1, fetch.ToList(), Array.Empty<(uint, string)>())),
        };
        var service = new EmailIntakeService(db, new PassThroughEncryptionService(), new FakeEmailProviderAdapterResolver(adapter),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailIntakeService>.Instance);
        return (db, service, account, fetch);
    }

    [Fact]
    public async Task Intake_SameMessageId_UnderANewProviderId_IsADuplicate()
    {
        var (db, service, account, fetch) = await SetUpAsync();
        using var _ = db;
        fetch.Add(Message("101", "<abc@customer.example>"));
        await service.RunForAccountAsync(account.Id, CancellationToken.None);

        // The mailbox was migrated: the same email is listed again with a different UID.
        fetch.Clear();
        fetch.Add(Message("9001", "<abc@customer.example>"));
        var second = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.Equal(1, second.DuplicateCount);
        Assert.Equal(1, await db.EmailMessages.CountAsync());
    }

    [Fact]
    public async Task Intake_WithoutAMessageId_FallsBackToAFingerprint()
    {
        var (db, service, account, fetch) = await SetUpAsync();
        using var _ = db;
        fetch.Add(Message("101", null, "Quote request"));
        await service.RunForAccountAsync(account.Id, CancellationToken.None);

        fetch.Clear();
        fetch.Add(Message("102", null, "Quote request")); // same sender, subject, time, recipients
        fetch.Add(Message("103", null, "Another request")); // genuinely different
        var second = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.Equal(1, second.DuplicateCount);
        Assert.Equal(1, second.PersistedCount);
        Assert.Equal(2, await db.EmailMessages.CountAsync());
    }

    [Fact]
    public async Task Intake_DifferentMessageIds_AreBothKept()
    {
        var (db, service, account, fetch) = await SetUpAsync();
        using var _ = db;
        fetch.Add(Message("101", "<a@x>"));
        fetch.Add(Message("102", "<b@x>"));
        var run = await service.RunForAccountAsync(account.Id, CancellationToken.None);

        Assert.Equal(2, run.PersistedCount);
        Assert.Equal(0, run.DuplicateCount);
    }

    [Fact]
    public async Task Notification_ForTheSameCaseEmailAndType_IsCreatedOnce()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John", Email = "john@sawo.com" };
        db.Employees.Add(owner);
        var c = new Case
        {
            CaseNumber = "CASE-000001", EmailAccountId = Guid.NewGuid(), CustomerEmailAddress = "maria@customer.example", Subject = "Quote",
            NormalizedSubject = "Quote", OwnerEmployeeId = owner.Id, FirstEmailReceivedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow,
        };
        db.Cases.Add(c);
        await db.SaveChangesAsync();
        var dispatcher = new FakeAgentNotificationDispatcher();
        var service = new NotificationService(db, dispatcher);
        var emailId = Guid.NewGuid();

        var first = await service.SendForCaseAsync(NotificationType.NewEmail, c, AgentPushCommandType.ShowCase, null, CancellationToken.None, emailId);
        var retry = await service.SendForCaseAsync(NotificationType.NewEmail, c, AgentPushCommandType.ShowCase, null, CancellationToken.None, emailId);
        var otherEmail = await service.SendForCaseAsync(NotificationType.NewEmail, c, AgentPushCommandType.ShowCase, null, CancellationToken.None, Guid.NewGuid());
        var otherType = await service.SendForCaseAsync(NotificationType.CustomerWroteAgain, c, AgentPushCommandType.ShowCase, null, CancellationToken.None, emailId);

        Assert.NotEqual(NotificationSendOutcome.Duplicate, first);
        Assert.Equal(NotificationSendOutcome.Duplicate, retry);
        Assert.NotEqual(NotificationSendOutcome.Duplicate, otherEmail);
        Assert.NotEqual(NotificationSendOutcome.Duplicate, otherType);
        Assert.Equal(3, await db.Notifications.CountAsync());
        Assert.Equal(3, dispatcher.Sent.Count); // the duplicate was never pushed
    }

    [Fact]
    public async Task Notification_WithoutAnEmail_IsNotDeduplicated_SoTimeDrivenReminderscKeepWorking()
    {
        using var db = TestDbContext.CreateNew();
        var owner = new Employee { FullName = "John", Email = "john@sawo.com" };
        db.Employees.Add(owner);
        var c = new Case
        {
            CaseNumber = "CASE-000001", EmailAccountId = Guid.NewGuid(), CustomerEmailAddress = "maria@customer.example", Subject = "Quote",
            NormalizedSubject = "Quote", OwnerEmployeeId = owner.Id, FirstEmailReceivedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow,
        };
        db.Cases.Add(c);
        await db.SaveChangesAsync();
        var service = new NotificationService(db, new FakeAgentNotificationDispatcher());

        await service.SendForCaseAsync(NotificationType.Reminder, c, AgentPushCommandType.ShowReminder, 1, CancellationToken.None);
        await service.SendForCaseAsync(NotificationType.Reminder, c, AgentPushCommandType.ShowReminder, 2, CancellationToken.None);

        Assert.Equal(2, await db.Notifications.CountAsync());
    }

    [Fact]
    public async Task Pipeline_RerunningEveryJob_DoesNotDuplicateCasesEmailsOrAlerts()
    {
        using var h = new InquiryHarness("agent1@sawo.com");
        h.Receive("agent1@sawo.com", "anna@customer.example", "Request for quotation",
            "Hello, I would like to request a quotation for 20 heaters. How much per unit?", InquiryHarness.Monday0900);

        for (var i = 1; i <= 4; i++) await h.RunAsync(InquiryHarness.Monday0900.AddMinutes(i));

        Assert.Equal(1, await h.Db.Cases.CountAsync());
        Assert.Equal(1, await h.Db.EmailMessages.CountAsync());
        Assert.Equal(1, await h.Db.CaseEmails.CountAsync());
        Assert.Equal(1, await h.Db.Notifications.CountAsync(n => n.Type == NotificationType.NewEmail));
    }
}
