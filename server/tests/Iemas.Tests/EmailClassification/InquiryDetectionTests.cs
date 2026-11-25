using Iemas.Application.Common.Ai;
using Iemas.Application.EmailClassification;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iemas.Tests.EmailClassification;

/// <summary>An employee is alerted for a real inquiry, not for any email that mentions a price.</summary>
public class InquiryDetectionTests
{
    private const string ClearInquiry = "I would like to request a quotation for 20 heaters. How much per unit?";

    private static (TestDbContext Db, EmailAccount Account) Seed()
    {
        var db = TestDbContext.CreateNew();
        var account = new EmailAccount { EmailAddress = "sales@sawo.com", Purpose = EmailAccountPurpose.Inbound, Host = "imap.test", Port = 993 };
        db.EmailAccounts.Add(account);
        db.AiModelConfigs.Add(new AiModelConfig
        {
            Provider = "OpenRouter", ModelIdentifier = "m", DisplayName = "m", Enabled = true, TaskCapability = "EmailClassification", TimeoutSeconds = 30,
        });
        db.SaveChanges();
        return (db, account);
    }

    private static async Task<EmailMessage> Add(TestDbContext db, EmailAccount account, string body, string subject = "Request for quotation",
        string from = "buyer@customer.example")
    {
        var message = new EmailMessage
        {
            EmailAccountId = account.Id, ProviderMessageId = Guid.NewGuid().ToString(), FromAddress = from, ToAddresses = account.EmailAddress,
            Subject = subject, BodyText = body, ReceivedAt = DateTimeOffset.UtcNow, ProcessingStatus = EmailProcessingStatus.PendingClassification,
        };
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();
        return message;
    }

    private static EmailClassificationService Service(TestDbContext db, IAiClassificationProvider ai) =>
        new(db, ai, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);

    private static FakeAiClassificationProvider Answering(bool relevant, string kind, double confidence, List<ClassificationRequest>? seen = null) => new()
    {
        Behavior = (request, model, _, _) =>
        {
            seen?.Add(request);
            return Task.FromResult(new ClassificationAttemptResult(true, "OpenRouter", model,
                new ClassificationResponse(relevant, kind, relevant, relevant, "MEDIUM", confidence, "summary", true, kind), null, 3));
        },
    };

    [Fact]
    public async Task AClearKeywordInquiry_IsStillReadByTheAi_AndTheAiCanRejectIt()
    {
        var (db, account) = Seed();
        var message = await Add(db, account, ClearInquiry);
        var ai = Answering(false, "vendor_pitch", 0.95);

        var decision = await Service(db, ai).ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Single(ai.CallsByModel);
        Assert.Equal(ImportanceDecision.NotImportant, decision);
    }

    [Fact]
    public async Task AnUnsureDisagreement_GoesToAPerson_NotToTheEmployee()
    {
        var (db, account) = Seed();
        var message = await Add(db, account, ClearInquiry);

        var decision = await Service(db, Answering(false, "business_correspondence", 0.6)).ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.ReviewRequired, decision);
        Assert.Contains("disagreed", (await db.EmailClassifications.SingleAsync()).DecisionReason);
    }

    [Fact]
    public async Task WhenTheAiIsDown_AClearInquiryIsNotLost()
    {
        var (db, account) = Seed();
        var message = await Add(db, account, ClearInquiry);
        var ai = new FakeAiClassificationProvider
        {
            Behavior = (_, model, _, _) => Task.FromResult(new ClassificationAttemptResult(false, "OpenRouter", model, null, "service unavailable", 3,
                ClassificationFailureCategory.Transient)),
        };

        var decision = await Service(db, ai).ClassifyOneAsync(message.Id, CancellationToken.None);

        Assert.Equal(ImportanceDecision.Important, decision);
        Assert.Contains("keyword filter's decision was used", (await db.EmailClassifications.SingleAsync()).DecisionReason);
    }

    [Fact]
    public async Task ASenderStaffAlreadyRejected_IsNeverAcceptedOnKeywordsAlone_AndTheAiIsTold()
    {
        var (db, account) = Seed();
        db.Cases.Add(new Case
        {
            CaseNumber = "CASE-000001", CustomerEmailAddress = "buyer@customer.example", Subject = "old", EmailAccountId = account.Id,
            WorkStatus = CaseWorkStatus.Cancelled, CompletionReason = CaseCompletionReason.IncorrectClassification,
        });
        var message = await Add(db, account, ClearInquiry);
        var seen = new List<ClassificationRequest>();
        var ai = new FakeAiClassificationProvider
        {
            Behavior = (request, model, _, _) =>
            {
                seen.Add(request);
                return Task.FromResult(new ClassificationAttemptResult(false, "OpenRouter", model, null, "down", 3, ClassificationFailureCategory.Transient));
            },
        };

        var decision = await Service(db, ai).ClassifyOneAsync(message.Id, CancellationToken.None);

        // With the AI down, an unrejected sender would have been accepted; this one waits for a person.
        Assert.Equal(ImportanceDecision.ReviewRequired, decision);
        Assert.Contains("NOT a customer inquiry", seen[0].SenderHistory);
    }

    [Fact]
    public void QuotedHistory_IsNotScored_SoAThankYouUnderOurPriceListIsNotAnInquiry()
    {
        var body = "Noted with thanks, have a nice weekend.\n\nOn Tue, 24 Nov 2026 at 10:00, Sales <sales@sawo.com> wrote:\n"
            + "> Here is our price list. How much of the heater do you need? Please send your quotation request.";

        var result = ZettelkastenFilter.Evaluate(
            new InquiryFilterInput("Re: Price list", body, "buyer@customer.example", false, false), InquiryFilterSettings.Defaults(), null);

        Assert.NotEqual(InquiryFilterVerdict.Inquiry, result.Verdict);
    }

    [Theory]
    [InlineData("Thanks, can you add two more?\n\n-----Original Message-----\nold quote", "Thanks, can you add two more?")]
    [InlineData("Please see my question below.\n\nFrom: Sales <s@sawo.com>\nSent: Monday\nTo: me\n\nold", "Please see my question below.")]
    [InlineData("We still need the delivery date.\n> earlier line", "We still need the delivery date.")]
    public void TheNewestMessage_IsSeparatedFromWhatItQuotes(string body, string latest) =>
        Assert.Equal(latest, EmailTextCleaner.Split("Re: order", body).Latest);

    [Theory]
    [InlineData("Fwd: customer question", "see this\n\n-----Original Message-----\nHow much is the heater?")]
    [InlineData("Re: order", "see below\n\n-----Original Message-----\nHow much is the heater?")]
    public void ForwardsAndOneLiners_AreLeftWhole(string subject, string body) =>
        Assert.Contains("How much is the heater?", EmailTextCleaner.Split(subject, body).Latest);

    [Fact]
    public void AJobApplication_IsNotAnInquiry()
    {
        var result = ZettelkastenFilter.Evaluate(
            new InquiryFilterInput("Job application", "I am applying for the position of installer. My curriculum vitae is attached, please advise.",
                "applicant@example.org", false, false), InquiryFilterSettings.Defaults(), null);

        Assert.NotEqual(InquiryFilterVerdict.Inquiry, result.Verdict);
    }
}
