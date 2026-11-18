using System.Net;
using System.Text;
using System.Text.Json;
using Iemas.Application.Common.Ai;
using Iemas.Domain.Cases;
using Iemas.Infrastructure.Ai;
using Iemas.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iemas.Tests.Ai;

/// <summary>Timeouts, retries with backoff, Retry-After, strict output validation and one repair call - for both OpenRouter callers.</summary>
public class OpenRouterReliabilityTests
{
    private static readonly ResolutionAssessmentRequest AssessRequest = new(Guid.NewGuid(), "Heater", new[]
    {
        new ConversationTurn("Customer", DateTimeOffset.UtcNow.AddHours(-2), "My heater is broken."),
        new ConversationTurn("Agent", DateTimeOffset.UtcNow.AddHours(-1), "Please reset the breaker."),
    });

    private static readonly ClassificationRequest ClassifyRequest = new(
        "Pricing", "Price for 50 units please.", "c@example.com", "sales@sawo.com", false, "Sales", "PRODUCT_INQUIRY", "price", "newsletter");

    private const string GoodAssessment = "{\"verdict\":\"IN_PROGRESS\",\"confidence\":0.8,\"reason\":\"Waiting for the customer.\",\"checks\":{\"awaiting_customer_approval\":true},\"open_items\":[\"quote not sent\"]}";
    private const string GoodClassification = "{\"relevant\":true,\"legitimate\":true,\"email_kind\":\"customer_inquiry\",\"category\":\"Product\",\"action_required\":true,\"response_expected\":true,\"priority\":\"HIGH\",\"confidence\":0.9,\"summary\":\"Wants a price.\"}";

    private static HttpResponseMessage Ok(string content, string model = "vendor/model-2026-01") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "gen-1", model,
                choices = new[] { new { message = new { content } } },
                usage = new { prompt_tokens = 100, completion_tokens = 20, total_tokens = 120, cost = 0.002 },
            }), Encoding.UTF8, "application/json"),
        };

    private static OpenRouterResolutionAssessor Assessor(FakeHttpMessageHandler handler, NoOpRetryDelay delay, Action<AiClassificationOptions>? tweak = null)
    {
        var options = new AiClassificationOptions();
        tweak?.Invoke(options);
        return new OpenRouterResolutionAssessor(new HttpClient(handler), new StaticAiProviderConnectionResolver("sk-test"),
            NullLogger<OpenRouterResolutionAssessor>.Instance, Options.Create(options), delay);
    }

    private static OpenRouterClassificationProvider Classifier(FakeHttpMessageHandler handler, NoOpRetryDelay delay, Action<AiClassificationOptions>? tweak = null)
    {
        var options = new AiClassificationOptions();
        tweak?.Invoke(options);
        return new OpenRouterClassificationProvider(new HttpClient(handler), new StaticAiProviderConnectionResolver("sk-test"),
            Options.Create(options), NullLogger<OpenRouterClassificationProvider>.Instance, delay);
    }

    private static FakeHttpMessageHandler Sequence(List<int> calls, params Func<HttpResponseMessage>[] responses) => new()
    {
        Behavior = (_, _) =>
        {
            var i = calls.Count;
            calls.Add(i);
            return Task.FromResult(responses[Math.Min(i, responses.Length - 1)]());
        },
    };

    [Fact]
    public async Task Assessor_ServerError_RetriesWithBackoff_ThenSucceeds_AndCapturesVersionAndPrompt()
    {
        var calls = new List<int>();
        var delay = new NoOpRetryDelay();
        var a = Assessor(Sequence(calls, () => new HttpResponseMessage(HttpStatusCode.BadGateway), () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), () => Ok(GoodAssessment)), delay);

        var r = await a.AssessAsync(AssessRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(r.Succeeded);
        Assert.Equal(3, calls.Count);
        Assert.Equal(3, r.Attempts);
        Assert.Equal(2, delay.RequestedDelays.Count);
        Assert.True(delay.RequestedDelays[1] > delay.RequestedDelays[0]); // exponential
        Assert.Equal("vendor/model-2026-01", r.ModelVersion);
        Assert.Equal(OpenRouterResolutionAssessor.PromptVersion, r.PromptVersion);
        Assert.Equal(120, r.Usage!.TotalTokens);
        Assert.Contains("Awaiting customer approval or confirmation", r.OpenItems!);
        Assert.Contains("quote not sent", r.OpenItems!);
    }

    [Fact]
    public async Task Assessor_RateLimited_HonoursRetryAfter()
    {
        var calls = new List<int>();
        var delay = new NoOpRetryDelay();
        var limited = () =>
        {
            var m = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return m;
        };
        var a = Assessor(Sequence(calls, limited, () => Ok(GoodAssessment)), delay);

        var r = await a.AssessAsync(AssessRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(r.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delay.RequestedDelays));
    }

    [Fact]
    public async Task Assessor_HugeRetryAfter_IsCapped()
    {
        var calls = new List<int>();
        var delay = new NoOpRetryDelay();
        var limited = () =>
        {
            var m = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return m;
        };
        var a = Assessor(Sequence(calls, limited, () => Ok(GoodAssessment)), delay, o => o.OpenRouter.MaxRetryDelaySeconds = 10);

        await a.AssessAsync(AssessRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(delay.RequestedDelays));
    }

    [Fact]
    public async Task Assessor_AuthFailure_IsNotRetried()
    {
        var calls = new List<int>();
        var a = Assessor(Sequence(calls, () => new HttpResponseMessage(HttpStatusCode.Unauthorized)), new NoOpRetryDelay());

        var r = await a.AssessAsync(AssessRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(r.Succeeded);
        Assert.Single(calls);
        Assert.Equal(ClassificationFailureCategory.AuthenticationFailure, r.FailureCategory);
    }

    [Fact]
    public async Task Assessor_RetriesAreBounded_AndFailureDoesNotThrow()
    {
        var calls = new List<int>();
        var a = Assessor(Sequence(calls, () => new HttpResponseMessage(HttpStatusCode.InternalServerError)), new NoOpRetryDelay(), o => o.OpenRouter.AssessmentMaxRetries = 2);

        var r = await a.AssessAsync(AssessRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(r.Succeeded);
        Assert.Null(r.Verdict);
        Assert.Equal(3, calls.Count); // 1 + 2 retries
        Assert.Equal(ClassificationFailureCategory.Transient, r.FailureCategory);
    }

    [Fact]
    public async Task Assessor_NetworkException_DoesNotThrow()
    {
        var a = Assessor(new FakeHttpMessageHandler { Behavior = (_, _) => throw new HttpRequestException("connection refused") }, new NoOpRetryDelay(),
            o => o.OpenRouter.AssessmentMaxRetries = 0);

        var r = await a.AssessAsync(AssessRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(r.Succeeded);
        Assert.Contains("connection refused", r.Error);
    }

    [Fact]
    public async Task Assessor_PerAttemptTimeout_BecomesFailureNotException()
    {
        var a = Assessor(new FakeHttpMessageHandler
        {
            Behavior = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Ok(GoodAssessment); },
        }, new NoOpRetryDelay(), o => o.OpenRouter.AssessmentMaxRetries = 0);

        var r = await a.AssessAsync(AssessRequest, "vendor/model", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.False(r.Succeeded);
        Assert.Contains("timed out", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"verdict\":\"APPEARS_RESOLVED\",\"confidence\":1.7,\"reason\":\"x\"}")]
    [InlineData("{\"verdict\":\"MAYBE\",\"confidence\":0.5}")]
    [InlineData("{\"verdict\":\"IN_PROGRESS\"}")]
    public async Task Assessor_MalformedOutput_IsRepairedOnce(string bad)
    {
        var calls = new List<int>();
        var bodies = new List<string>();
        var handler = new FakeHttpMessageHandler
        {
            Behavior = async (req, _) =>
            {
                bodies.Add(await req.Content!.ReadAsStringAsync());
                calls.Add(calls.Count);
                return calls.Count == 1 ? Ok(bad) : Ok(GoodAssessment);
            },
        };
        var a = Assessor(handler, new NoOpRetryDelay());

        var r = await a.AssessAsync(AssessRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(r.Succeeded);
        Assert.Equal(2, calls.Count);
        Assert.Contains("previous reply was rejected", bodies[1]);
        Assert.Equal(240, r.Usage!.TotalTokens); // both calls billed and summed
    }

    [Fact]
    public async Task Assessor_StillMalformedAfterRepair_FailsWithoutVerdict()
    {
        var calls = new List<int>();
        var a = Assessor(Sequence(calls, () => Ok("{\"verdict\":\"APPEARS_RESOLVED\",\"confidence\":9}")), new NoOpRetryDelay());

        var r = await a.AssessAsync(AssessRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(r.Succeeded);
        Assert.Null(r.Verdict);
        Assert.Equal(2, calls.Count);
        Assert.Equal(ClassificationFailureCategory.MalformedResponse, r.FailureCategory);
    }

    [Fact]
    public async Task Assessor_RepairCanBeDisabled()
    {
        var calls = new List<int>();
        var a = Assessor(Sequence(calls, () => Ok("garbage")), new NoOpRetryDelay(), o => o.OpenRouter.RepairMalformedOutput = false);

        var r = await a.AssessAsync(AssessRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(r.Succeeded);
        Assert.Single(calls);
    }

    [Fact]
    public async Task Classifier_MalformedThenValid_IsRepaired_AndRecordsVersions()
    {
        var calls = new List<int>();
        var c = Classifier(Sequence(calls, () => Ok("{\"relevant\":true,\"confidence\":4,\"priority\":\"HIGH\"}"), () => Ok(GoodClassification)), new NoOpRetryDelay());

        var r = await c.ClassifyAsync(ClassifyRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(r.Succeeded);
        Assert.Equal(2, r.Attempts);
        Assert.Equal(0.9, r.Response!.Confidence);
        Assert.Equal("vendor/model-2026-01", r.ModelVersion);
        Assert.Equal(OpenRouterClassificationProvider.PromptVersion, r.PromptVersion);
        Assert.Equal(240, r.Usage!.TotalTokens);
    }

    [Fact]
    public async Task Classifier_UnknownEmailKind_IsRejected()
    {
        var calls = new List<int>();
        var bad = GoodClassification.Replace("customer_inquiry", "wizard");
        var c = Classifier(Sequence(calls, () => Ok(bad)), new NoOpRetryDelay());

        var r = await c.ClassifyAsync(ClassifyRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(r.Succeeded);
        Assert.Equal(ClassificationFailureCategory.MalformedResponse, r.FailureCategory);
        Assert.Equal(2, calls.Count); // original + one repair
    }

    [Fact]
    public async Task Classifier_DoesNotRetryTransport_ByDefault_ServiceLayerOwnsThat()
    {
        var calls = new List<int>();
        var c = Classifier(Sequence(calls, () => new HttpResponseMessage(HttpStatusCode.BadGateway)), new NoOpRetryDelay());

        var r = await c.ClassifyAsync(ClassifyRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(r.Succeeded);
        Assert.Single(calls);
        Assert.Equal(ClassificationFailureCategory.Transient, r.FailureCategory);
    }

    [Fact]
    public async Task Classifier_TransportRetries_CanBeEnabled()
    {
        var calls = new List<int>();
        var delay = new NoOpRetryDelay();
        var c = Classifier(Sequence(calls, () => new HttpResponseMessage(HttpStatusCode.BadGateway), () => Ok(GoodClassification)), delay, o => o.OpenRouter.ClassificationTransportRetries = 1);

        var r = await c.ClassifyAsync(ClassifyRequest, "vendor/model", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(r.Succeeded);
        Assert.Single(delay.RequestedDelays);
    }

    [Fact]
    public void Parser_RejectsOutOfRangeConfidence_AndAcceptsLowercaseVerdict()
    {
        Assert.Null(OpenRouterResolutionAssessor.TryParse("{\"verdict\":\"IN_PROGRESS\",\"confidence\":-0.1}", out var err));
        Assert.Contains("0..1", err);
        var ok = OpenRouterResolutionAssessor.TryParse("{\"verdict\":\"appears_resolved\",\"confidence\":0.5}", out _);
        Assert.Equal(ResolutionVerdict.AppearsResolved, ok!.Verdict);
    }
}
