using Iemas.Application.Common.Interfaces;
using Iemas.Application.Escalations;
using Iemas.Domain.Cases;
using Xunit;

namespace Iemas.Tests.Escalations;

public class EscalationEmailComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static Case SampleCase() => new()
    {
        CaseNumber = "CASE-000042",
        CustomerDisplayName = "Anna <script>alert(1)</script>",
        CustomerEmailAddress = "anna@customer.example",
        Subject = "Price for <b>heaters</b> & stoves",
        FirstEmailReceivedAt = Now.AddHours(-30),
        LastCustomerMessageAt = Now.AddHours(-30),
        Stage = InquiryStage.AwaitingInitialResponse,
    };

    private static OutboundEmail Compose(string? cmsBaseUrl = null, bool isTest = false, Case? c = null) =>
        EscalationEmailComposer.Compose(
            new EscalationEmailContext(c ?? SampleCase(), "Rafael", "rafael@sawo.com", 1, "No initial response within 4 business hour(s).",
                new[] { Now.AddHours(-20), Now.AddHours(-18) }, TimeZoneInfo.Utc, Now, "SAWO", cmsBaseUrl, isTest),
            new[] { new EmailRecipient("Jeremiah", "jeremiah@sawo.com") });

    [Fact]
    public void Compose_SendsBrandedHtml_WithInlineLogo_AndKeepsPlainText()
    {
        var email = Compose();

        Assert.NotNull(email.HtmlBody);
        Assert.Contains("Inquiry needs your attention", email.HtmlBody);
        Assert.Contains("Escalation level 1", email.HtmlBody);
        Assert.Contains("Why this was escalated", email.HtmlBody);
        Assert.Contains($"cid:{EscalationEmailComposer.LogoContentId}", email.HtmlBody);
        var logo = Assert.Single(email.InlineImages!);
        Assert.Equal("image/png", logo.ContentType);
        Assert.True(logo.Content.Length > 1000);
        Assert.Contains("Previous reminders: 2", email.TextBody); // plain-text alternative unchanged
    }

    [Fact]
    public void Compose_EscapesCustomerText_InHtml()
    {
        var html = Compose().HtmlBody!;

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Price for &lt;b&gt;heaters&lt;/b&gt; &amp; stoves", html);
    }

    [Fact]
    public void Compose_WithCmsAddress_LinksToTheCase()
    {
        var c = SampleCase();
        var email = Compose("http://172.16.2.68:8191/", c: c);

        Assert.Contains($"http://172.16.2.68:8191/cases/{c.Id}", email.HtmlBody);
        Assert.Contains("Open case in IEMAS", email.HtmlBody);
        Assert.Contains($"http://172.16.2.68:8191/cases/{c.Id}", email.TextBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    public void Compose_WithoutValidCmsAddress_HasNoLink(string? cmsBaseUrl)
    {
        var email = Compose(cmsBaseUrl);

        Assert.DoesNotContain("Open case in IEMAS", email.HtmlBody);
        Assert.DoesNotContain("javascript:", email.HtmlBody);
        Assert.Contains("Cases &rarr; CASE-000042", email.HtmlBody);
    }

    [Fact]
    public void Compose_TestEmail_ShowsBanner_AndLinksToCaseList()
    {
        var test = Compose("http://cms.example", isTest: true);
        var real = Compose("http://cms.example");

        Assert.Contains("TEST EMAIL", test.HtmlBody);
        Assert.Contains("http://cms.example/cases\"", test.HtmlBody);
        Assert.DoesNotContain("TEST EMAIL", real.HtmlBody);
    }
}
