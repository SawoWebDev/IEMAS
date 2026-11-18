using Iemas.Application.Cases;
using Iemas.Application.EmailClassification;
using Iemas.Application.EmailClassification.Dtos;
using Iemas.Application.Notifications;
using Iemas.Application.Reminders;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.EmailClassification;

public class ClassificationReviewServiceTests
{
    private sealed class Fixture
    {
        public TestDbContext Db = TestDbContext.CreateNew();
        public NoOpAuditService Audit = new();
        public FakeAgentNotificationDispatcher Dispatcher = new();
        public FakeCurrentUserService User = new(null, "reviewer@sawo.com");
        public Employee Employee = new() { FullName = "John Smith", Email = "john@sawo.com" };
        public EmailAccount Account = null!;

        public async Task<Guid> AddPendingAsync(string subject = "Maybe an inquiry", DateTimeOffset? classifiedAt = null)
        {
            if (Account is null)
            {
                Db.Employees.Add(Employee);
                await Db.SaveChangesAsync();
                Account = new EmailAccount
                {
                    EmailAddress = "sales@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap,
                    Host = "imap.example.com", Port = 993, Username = "sales@sawo.com", AuthMethod = EmailAuthMethod.Password,
                    IsActive = true, MonitoringEnabled = true, OwnerEmployeeId = Employee.Id,
                };
                Db.EmailAccounts.Add(Account);
                await Db.SaveChangesAsync();
            }

            var message = new EmailMessage
            {
                EmailAccountId = Account.Id, Provider = EmailProtocol.Imap, ProviderMessageId = Guid.NewGuid().ToString(),
                FromAddress = "customer@example.com", ToAddresses = "sales@sawo.com", Subject = subject,
                BodyText = "Hello,\n\nwe might need some help with   your product.", ReceivedAt = DateTimeOffset.UtcNow,
                ProcessingStatus = EmailProcessingStatus.ReviewRequired,
            };
            Db.EmailMessages.Add(message);
            Db.EmailClassifications.Add(new Iemas.Domain.Ai.EmailClassification
            {
                EmailMessageId = message.Id, Decision = ImportanceDecision.ReviewRequired, Relevance = ClassificationRelevance.Relevant,
                Category = "Inquiry", Priority = ClassificationPriority.Low, AiConfidence = 0.55, DecisionReason = "Low confidence",
                ClassifiedAt = classifiedAt ?? DateTimeOffset.UtcNow,
            });
            await Db.SaveChangesAsync();
            return message.Id;
        }

        public ClassificationReviewService Service()
        {
            var workflow = new CaseWorkflowService(Db, new CaseMatchingService(Db), new ReminderSchedulingService(Db), new NotificationService(Db, Dispatcher));
            return new ClassificationReviewService(Db, workflow, Audit, User);
        }
    }

    [Fact]
    public async Task Approve_CreatesCase_NotifiesOwner_RecordsReviewerAndAudit()
    {
        var f = new Fixture();
        var id = await f.AddPendingAsync();

        var result = await f.Service().ReviewAsync(id, new ReviewClassificationRequest("Approve", "Looks real", null, null, null), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("NewCase", result.Value!.CaseOutcome);
        Assert.StartsWith("CASE-", result.Value.CaseNumber);

        var c = await f.Db.EmailClassifications.AsNoTracking().SingleAsync();
        Assert.Equal(ImportanceDecision.Important, c.Decision);
        Assert.Equal(ClassificationReviewDecision.Approved, c.ReviewDecision);
        Assert.Equal(f.User.UserId, c.ReviewedByUserId);
        Assert.NotNull(c.ReviewedAt);
        Assert.Equal("Looks real", c.ReviewNote);
        Assert.Equal(ClassificationPriority.Low, c.Priority); // AI value untouched

        Assert.Single(await f.Db.Cases.ToListAsync());
        Assert.Single(f.Dispatcher.Sent);
        var entry = Assert.Single(f.Audit.Entries);
        Assert.Equal("CLASSIFICATION_REVIEW_APPROVED", entry.Action);
        Assert.Contains("confidence=0.55", entry.Details);
    }

    [Fact]
    public async Task Reject_MarksNotImportant_NoCase()
    {
        var f = new Fixture();
        var id = await f.AddPendingAsync();

        var result = await f.Service().ReviewAsync(id, new ReviewClassificationRequest("Reject", null, null, null, null), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("NoCase", result.Value!.CaseOutcome);
        Assert.Empty(await f.Db.Cases.ToListAsync());
        var c = await f.Db.EmailClassifications.AsNoTracking().SingleAsync();
        Assert.Equal(ImportanceDecision.NotImportant, c.Decision);
        Assert.Equal(ClassificationReviewDecision.Rejected, c.ReviewDecision);
        Assert.Equal(EmailProcessingStatus.Processed, (await f.Db.EmailMessages.AsNoTracking().SingleAsync()).ProcessingStatus);
        Assert.Equal("CLASSIFICATION_REVIEW_REJECTED", Assert.Single(f.Audit.Entries).Action);
    }

    [Fact]
    public async Task Correct_RecordsCorrectionsBesideAiValues_ThenApproves()
    {
        var f = new Fixture();
        var id = await f.AddPendingAsync();

        var result = await f.Service().ReviewAsync(id,
            new ReviewClassificationRequest("Approve", null, "Relevant", "Complaint", "High"), CancellationToken.None);

        Assert.True(result.Succeeded);
        var c = await f.Db.EmailClassifications.AsNoTracking().SingleAsync();
        Assert.True(c.IsManuallyCorrected);
        Assert.Equal("Complaint", c.CorrectedCategory);
        Assert.Equal(ClassificationPriority.High, c.CorrectedPriority);
        Assert.Equal("Inquiry", c.Category);
        Assert.Equal(ClassificationPriority.Low, c.Priority);
        Assert.Equal("Complaint", (await f.Db.EmailMessages.AsNoTracking().SingleAsync()).Classification);
    }

    [Fact]
    public async Task ReviewTwice_SecondIsConflict_NoSecondCase()
    {
        var f = new Fixture();
        var id = await f.AddPendingAsync();
        var service = f.Service();

        Assert.True((await service.ReviewAsync(id, new ReviewClassificationRequest("Approve", null, null, null, null), CancellationToken.None)).Succeeded);
        var second = await service.ReviewAsync(id, new ReviewClassificationRequest("Reject", null, null, null, null), CancellationToken.None);

        Assert.Equal(ClassificationReviewStatus.Conflict, second.Status);
        Assert.Single(await f.Db.Cases.ToListAsync());
        Assert.Equal(ImportanceDecision.Important, (await f.Db.EmailClassifications.AsNoTracking().SingleAsync()).Decision);
    }

    [Fact]
    public async Task InvalidAndContradictoryRequests_AreRejected_AndNothingChanges()
    {
        var f = new Fixture();
        var id = await f.AddPendingAsync();
        var service = f.Service();

        Assert.Equal(ClassificationReviewStatus.Invalid,
            (await service.ReviewAsync(id, new ReviewClassificationRequest("Maybe", null, null, null, null), CancellationToken.None)).Status);
        Assert.Equal(ClassificationReviewStatus.Invalid,
            (await service.ReviewAsync(id, new ReviewClassificationRequest("Approve", null, "NotRelevant", null, null), CancellationToken.None)).Status);
        Assert.Equal(ClassificationReviewStatus.NotFound,
            (await service.ReviewAsync(Guid.NewGuid(), new ReviewClassificationRequest("Approve", null, null, null, null), CancellationToken.None)).Status);

        Assert.Equal(ImportanceDecision.ReviewRequired, (await f.Db.EmailClassifications.AsNoTracking().SingleAsync()).Decision);
        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public async Task NonReviewRequiredEmail_CannotBeReviewed()
    {
        var f = new Fixture();
        var id = await f.AddPendingAsync();
        var c = await f.Db.EmailClassifications.SingleAsync();
        c.Decision = ImportanceDecision.NotImportant;
        (await f.Db.EmailMessages.SingleAsync()).ProcessingStatus = EmailProcessingStatus.Processed;
        await f.Db.SaveChangesAsync();

        var result = await f.Service().ReviewAsync(id, new ReviewClassificationRequest("Approve", null, null, null, null), CancellationToken.None);

        Assert.Equal(ClassificationReviewStatus.Conflict, result.Status);
        Assert.Empty(await f.Db.Cases.ToListAsync());
    }

    [Fact]
    public async Task ListAndCount_ShowOnlyPending_OldestFirst_FlagStale()
    {
        var f = new Fixture();
        await f.AddPendingAsync("Recent", DateTimeOffset.UtcNow.AddHours(-1));
        var oldId = await f.AddPendingAsync("Old", DateTimeOffset.UtcNow.AddHours(-30));
        var doneId = await f.AddPendingAsync("Done");
        var service = f.Service();
        await service.ReviewAsync(doneId, new ReviewClassificationRequest("Reject", null, null, null, null), CancellationToken.None);

        var list = await service.ListPendingAsync(50, CancellationToken.None);
        var count = await service.GetPendingCountAsync(CancellationToken.None);

        Assert.Equal(new[] { "Old", "Recent" }, list.Select(i => i.Subject).ToArray());
        Assert.Equal(oldId, list[0].EmailMessageId);
        Assert.True(list[0].IsStale);
        Assert.False(list[1].IsStale);
        Assert.Equal("Hello, we might need some help with your product.", list[0].Snippet);
        Assert.Equal(0.55, list[0].AiConfidence);
        Assert.Equal(2, count.Pending);
        Assert.Equal(1, count.Stale);
    }

    [Fact]
    public async Task ClassificationJob_DoesNotPickUpReviewRequiredAgain()
    {
        var f = new Fixture();
        await f.AddPendingAsync();

        // The job selects PendingClassification only; a ReviewRequired message stays in the queue untouched.
        var picked = await f.Db.EmailMessages.CountAsync(m => m.ProcessingStatus == EmailProcessingStatus.PendingClassification);
        Assert.Equal(0, picked);
        Assert.Equal(1, (await f.Service().GetPendingCountAsync(CancellationToken.None)).Pending);
    }
}
