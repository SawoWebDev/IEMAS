using Iemas.Application.Common.Ai;
using Iemas.Application.EmailClassification;
using Iemas.Domain.Ai;
using Iemas.Domain.Email;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iemas.Tests.EmailClassification;

/// <summary>The AI audit columns on AiClassificationLog are filled from the provider result, and a fallback model is flagged.</summary>
public class ClassificationAuditColumnsTests
{
    private const string AmbiguousBody = "I visited your website and wanted to know whether your team could support us on an upcoming project.";

    private static async Task<(TestDbContext Db, EmailClassificationService Service, Guid MessageId, FakeAiClassificationProvider Ai)> ArrangeAsync(bool twoModels)
    {
        var db = TestDbContext.CreateNew();
        var account = new EmailAccount
        {
            EmailAddress = "sales@sawo.com", Purpose = EmailAccountPurpose.Inbound, Protocol = EmailProtocol.Imap, Host = "imap.example.com", Port = 993,
            Encryption = "SSL/TLS", Username = "sales@sawo.com", AuthMethod = EmailAuthMethod.Password, IsActive = true, MonitoringEnabled = true,
        };
        db.EmailAccounts.Add(account);
        db.AiModelConfigs.Add(new AiModelConfig { Provider = "OpenRouter", ModelIdentifier = "primary", DisplayName = "p", Enabled = true, TaskCapability = "EmailClassification", TimeoutSeconds = 30, MaxRetries = 0 });
        if (twoModels)
            db.AiModelConfigs.Add(new AiModelConfig { Provider = "OpenRouter", ModelIdentifier = "backup", DisplayName = "b", Enabled = true, TaskCapability = "EmailClassification", TimeoutSeconds = 30, MaxRetries = 0, FallbackOrder = 1 });
        var message = new EmailMessage
        {
            EmailAccountId = account.Id, Provider = EmailProtocol.Imap, ProviderMessageId = Guid.NewGuid().ToString(), FromAddress = "lee@prospect.example",
            ToAddresses = "sales@sawo.com", Subject = "Upcoming project", BodyText = AmbiguousBody, ReceivedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = EmailProcessingStatus.PendingClassification,
        };
        db.EmailMessages.Add(message);
        await db.SaveChangesAsync();

        var ai = new FakeAiClassificationProvider();
        var service = new EmailClassificationService(db, ai, new NoOpRetryDelay(), new AiCircuitBreakerStore(TimeProvider.System), NullLogger<EmailClassificationService>.Instance);
        return (db, service, message.Id, ai);
    }

    private static ClassificationAttemptResult Ok(string model) => new(true, "OpenRouter", model,
        new ClassificationResponse(true, "PROJECT_INQUIRY", true, true, "MEDIUM", 0.93, "Prospect asking about a project.", true), null, 12,
        Usage: new AiTokenUsage(100, 20, 120, 0.0031m, "gen-1"), ModelVersion: model + "-2026-01", PromptVersion: "classify-v9");

    [Fact]
    public async Task Success_CopiesModelVersion_PromptVersion_Confidence_Tokens_Cost_AndMarksPrimaryAsNotFallback()
    {
        var (db, service, id, ai) = await ArrangeAsync(false);
        using var _ = db;
        ai.Behavior = (_, model, _, _) => Task.FromResult(Ok(model));

        await service.ClassifyOneAsync(id, CancellationToken.None);

        var log = await db.AiClassificationLogs.SingleAsync(l => l.EmailMessageId == id);
        Assert.Equal(AiClassificationOutcome.Classified, log.Outcome);
        Assert.Equal("primary-2026-01", log.ModelVersion);
        Assert.Equal("classify-v9", log.PromptVersion);
        Assert.Equal(0.93, log.Confidence);
        Assert.Equal(120, log.TotalTokens);
        Assert.Equal(0.0031m, log.CostUsd);
        Assert.False(log.FallbackUsed);
    }

    [Fact]
    public async Task Success_OnTheFallbackModel_SetsFallbackUsed()
    {
        var (db, service, id, ai) = await ArrangeAsync(true);
        using var _ = db;
        ai.Behavior = (_, model, _, _) => Task.FromResult(model == "primary"
            ? new ClassificationAttemptResult(false, "OpenRouter", model, null, "down", 5, ClassificationFailureCategory.Transient)
            : Ok(model));

        await service.ClassifyOneAsync(id, CancellationToken.None);

        var log = await db.AiClassificationLogs.SingleAsync(l => l.EmailMessageId == id);
        Assert.Equal("backup", log.AiModel);
        Assert.True(log.FallbackUsed);
    }
}
