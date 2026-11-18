using Iemas.Application.AiUsage;
using Iemas.Application.Common.Ai;
using Iemas.Application.Inquiries;
using Iemas.Application.Notifications;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Tests.Inquiries;
using Iemas.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iemas.Tests.Ai;

/// <summary>Resolution assessment: audit rows, fallback chain, circuit breaker, graceful failure and the deterministic guard, over the real pipeline.</summary>
public class AiAuditTests
{
    private const string Agent1 = "agent1@sawo.com";
    private const string Customer = "anna@customer.example";
    private static readonly DateTimeOffset Mon = InquiryHarness.Monday0900;

    private sealed class ListRecorder : IAiUsageRecorder
    {
        public List<AiUsageRecord> Records { get; } = new();
        public Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken) { Records.Add(record); return Task.CompletedTask; }
    }

    private sealed class ScriptedAssessor : IAiResolutionAssessor
    {
        public List<string> ModelsCalled { get; } = new();
        public Func<string, ResolutionAssessmentResult> Behavior { get; set; } = m => throw new InvalidOperationException();

        public Task<ResolutionAssessmentResult> AssessAsync(ResolutionAssessmentRequest request, string modelIdentifier, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ModelsCalled.Add(modelIdentifier);
            return Task.FromResult(Behavior(modelIdentifier));
        }
    }

    private static ResolutionAssessmentResult Good(string model, ResolutionVerdict verdict, double confidence = 0.95, IReadOnlyList<string>? open = null) =>
        new(true, verdict, confidence, "Because.", "OpenRouter", model, null, 123, new AiTokenUsage(100, 20, 120, 0.002m, "gen-9"),
            open, model + "-2026-01", "resolution-test-v1", ClassificationFailureCategory.None, 2);

    private static ResolutionAssessmentResult Bad(string model, ClassificationFailureCategory cat = ClassificationFailureCategory.Transient) =>
        new(false, null, 0, null, "OpenRouter", model, "boom", 50, null, null, null, "resolution-test-v1", cat, 3);

    /// <summary>A real inquiry with an agent reply; optionally the customer answers last.</summary>
    private static async Task<(InquiryHarness H, Guid CaseId)> ArrangeAsync(string? customerReply, string agentReply = "Please reset the breaker.")
    {
        var h = new InquiryHarness(Agent1);
        h.Db.AiModelConfigs.Add(new AiModelConfig { Provider = "OpenRouter", ModelIdentifier = "m2", DisplayName = "m2", Enabled = true, TaskCapability = "EmailClassification", TimeoutSeconds = 30, FallbackOrder = 1 });
        await h.Db.SaveChangesAsync();
        var first = h.Receive(Agent1, Customer, "Heater problem", "My heater does not work. Can you help me?", Mon);
        await h.RunAsync(Mon.AddMinutes(1));
        var fix = h.Send(Agent1, Customer, "Re: Heater problem", agentReply, Mon.AddHours(1), inReplyTo: first);
        await h.RunAsync(Mon.AddHours(1).AddMinutes(5));
        if (customerReply is not null)
        {
            h.Receive(Agent1, Customer, "Re: Heater problem", customerReply, Mon.AddHours(2), inReplyTo: fix);
            await h.RunAsync(Mon.AddHours(2).AddMinutes(5));
        }
        var c = await h.CaseAsync(Agent1, Customer);
        return (h, c.Id);
    }

    private static ResolutionAssessmentService Service(InquiryHarness h, ScriptedAssessor assessor, ListRecorder recorder, AiCircuitBreakerStore? breaker = null)
    {
        var notifications = new NotificationService(h.Db, h.PopUps);
        return new ResolutionAssessmentService(h.Db, assessor, new InquiryConversationService(h.Db, notifications),
            new AgentReplyProtector(new PassThroughEncryptionService()), recorder, breaker);
    }

    private static Task<bool> AssessAsync(ResolutionAssessmentService s, Guid caseId) =>
        s.AssessAsync(caseId, SupervisionSettings.Defaults, CancellationToken.None, Mon.AddHours(3));

    [Fact]
    public async Task Assessment_IsAudited_WithModelVersionPromptDecisionTokensCost_CaseAndEmail()
    {
        var (h, caseId) = await ArrangeAsync(null);
        using var harness = h;
        var rec = new ListRecorder();
        var assessor = new ScriptedAssessor { Behavior = m => Good(m, ResolutionVerdict.InProgress, 0.8, new[] { "Quote not sent" }) };

        Assert.True(await AssessAsync(Service(h, assessor, rec), caseId));

        var r = Assert.Single(rec.Records);
        Assert.Equal(AiUsagePurpose.ResolutionAssessment, r.Purpose);
        Assert.Equal(caseId, r.CaseId);
        Assert.NotNull(r.EmailMessageId);
        Assert.Equal("m", r.ModelIdentifier);
        Assert.Equal("m-2026-01", r.ModelVersion);
        Assert.Equal("resolution-test-v1", r.PromptVersion);
        Assert.Equal("InProgress", r.Decision);
        Assert.Equal(0.8, r.Confidence);
        Assert.Contains("Because.", r.Explanation);
        Assert.Equal(123, r.DurationMs);
        Assert.Equal(120, r.TotalTokens);
        Assert.Equal(0.002m, r.CostUsd);
        Assert.True(r.Succeeded);
        Assert.False(r.FallbackUsed);
        Assert.Equal(2, r.AttemptCount);
        var c = await h.Db.Cases.AsNoTracking().SingleAsync(x => x.Id == caseId);
        Assert.Contains("Quote not sent", c.ResolutionReason);
    }

    [Fact]
    public async Task PrimaryFails_FallbackModelAnswers_BothCallsAudited_FallbackFlagged()
    {
        var (h, caseId) = await ArrangeAsync(null);
        using var harness = h;
        var rec = new ListRecorder();
        var assessor = new ScriptedAssessor { Behavior = m => m == "m" ? Bad(m) : Good(m, ResolutionVerdict.InProgress, 0.7) };

        Assert.True(await AssessAsync(Service(h, assessor, rec), caseId));

        Assert.Equal(new[] { "m", "m2" }, assessor.ModelsCalled);
        Assert.Equal(2, rec.Records.Count);
        Assert.False(rec.Records[0].Succeeded);
        Assert.Equal("Transient", rec.Records[0].FailureCategory);
        Assert.True(rec.Records[1].Succeeded);
        Assert.True(rec.Records[1].FallbackUsed);
    }

    [Fact]
    public async Task EveryModelFails_NoVerdict_NoThrow_CaseUntouched_FailuresAudited()
    {
        var (h, caseId) = await ArrangeAsync(null);
        using var harness = h;
        var rec = new ListRecorder();
        var before = (await h.Db.Cases.AsNoTracking().SingleAsync(x => x.Id == caseId)).ResolutionAssessedAt;
        var assessor = new ScriptedAssessor { Behavior = m => Bad(m) };

        Assert.False(await AssessAsync(Service(h, assessor, rec), caseId));

        Assert.Equal(2, rec.Records.Count);
        Assert.All(rec.Records, r => Assert.False(r.Succeeded));
        var c = await h.Db.Cases.AsNoTracking().SingleAsync(x => x.Id == caseId);
        Assert.Equal(before, c.ResolutionAssessedAt);
    }

    [Fact]
    public async Task AssessorThrowing_IsContained()
    {
        var (h, caseId) = await ArrangeAsync(null);
        using var harness = h;
        var rec = new ListRecorder();
        var assessor = new ScriptedAssessor { Behavior = _ => throw new InvalidOperationException("kaboom") };

        Assert.False(await AssessAsync(Service(h, assessor, rec), caseId));
        Assert.Contains(rec.Records, r => r.ErrorMessage!.Contains("kaboom"));
    }

    [Fact]
    public async Task OpenCircuit_SkipsPrimary_UsesFallback()
    {
        var (h, caseId) = await ArrangeAsync(null);
        using var harness = h;
        var breaker = new AiCircuitBreakerStore(TimeProvider.System);
        for (var i = 0; i < 3; i++)
        {
            breaker.TryAcquire("OpenRouter", "m", out _);
            breaker.ReportOutcome("OpenRouter", "m", CircuitOutcome.CountedFailure);
        }
        var assessor = new ScriptedAssessor { Behavior = m => Good(m, ResolutionVerdict.InProgress, 0.7) };

        Assert.True(await AssessAsync(Service(h, assessor, new ListRecorder(), breaker), caseId));
        Assert.Equal(new[] { "m2" }, assessor.ModelsCalled);
    }

    [Fact]
    public async Task AiSaysResolved_ButAgentSpokeLast_GuardDownsgrades_NoPotentiallyResolved()
    {
        var (h, caseId) = await ArrangeAsync(null);
        using var harness = h;
        var rec = new ListRecorder();
        var assessor = new ScriptedAssessor { Behavior = m => Good(m, ResolutionVerdict.AppearsResolved, 0.99) };

        Assert.True(await AssessAsync(Service(h, assessor, rec), caseId));

        var c = await h.Db.Cases.AsNoTracking().SingleAsync(x => x.Id == caseId);
        Assert.NotEqual(InquiryStage.PotentiallyResolved, c.Stage);
        Assert.Equal(ResolutionVerdict.InProgress, c.ResolutionVerdict);
        Assert.Contains("customer has not replied", c.ResolutionReason);
        Assert.Contains("guard", rec.Records.Single().Decision);
    }

    [Fact]
    public async Task CustomerThanksAlone_AiSaysResolved_StaysSupervised()
    {
        var (h, caseId) = await ArrangeAsync("Thanks!", "We received your request and will look into it.");
        using var harness = h;
        var assessor = new ScriptedAssessor { Behavior = m => Good(m, ResolutionVerdict.AppearsResolved, 0.99) };

        Assert.True(await AssessAsync(Service(h, assessor, new ListRecorder()), caseId));

        var c = await h.Db.Cases.AsNoTracking().SingleAsync(x => x.Id == caseId);
        Assert.NotEqual(InquiryStage.PotentiallyResolved, c.Stage);
        Assert.NotEqual(ResolutionVerdict.AppearsResolved, c.ResolutionVerdict);
    }

    [Fact]
    public async Task GenuineConfirmation_StillProposesPotentiallyResolved_NeverCloses()
    {
        var (h, caseId) = await ArrangeAsync("Thank you, it works now!");
        using var harness = h;
        var assessor = new ScriptedAssessor { Behavior = m => Good(m, ResolutionVerdict.AppearsResolved, 0.99) };

        Assert.True(await AssessAsync(Service(h, assessor, new ListRecorder()), caseId));

        var c = await h.Db.Cases.AsNoTracking().SingleAsync(x => x.Id == caseId);
        Assert.Equal(InquiryStage.PotentiallyResolved, c.Stage);
        Assert.Null(c.CompletedAt);
    }

    [Fact]
    public async Task AuditQuery_ReturnsFilteredEntries()
    {
        var (h, caseId) = await ArrangeAsync(null);
        using var harness = h;
        h.Db.AiUsageRecords.Add(new AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "m", Purpose = "ResolutionAssessment", CaseId = caseId, Succeeded = true, PromptVersion = "p1", Decision = "InProgress", Confidence = 0.6, FallbackUsed = false });
        h.Db.AiUsageRecords.Add(new AiUsageRecord { Provider = "OpenRouter", ModelIdentifier = "m", Purpose = "EmailClassification", Succeeded = false, FailureCategory = "RateLimited" });
        await h.Db.SaveChangesAsync();
        var q = new AiUsageQueryService(h.Db);

        var forCase = await q.GetAuditAsync(1, 20, caseId, null, null, null, null, null, null, CancellationToken.None);
        var failures = await q.GetAuditAsync(1, 20, null, null, null, false, null, null, null, CancellationToken.None);

        var entry = Assert.Single(forCase.Items);
        Assert.Equal("p1", entry.PromptVersion);
        Assert.Equal("InProgress", entry.Decision);
        Assert.Equal("RateLimited", Assert.Single(failures.Items).FailureCategory);
    }
}
