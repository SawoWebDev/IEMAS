using Iemas.Application.EmailClassification;
using Iemas.Domain.Ai;
using Xunit;

namespace Iemas.Tests.EmailClassification;

/// <summary>§26 AI Confidence Policy — pure decision-table tests, no AI call involved.</summary>
public class ClassificationDecisionPolicyTests
{
    [Fact]
    public void Decide_HighConfidenceRelevant_IsImportant()
    {
        var result = ClassificationDecisionPolicy.Decide(true, 0.9, null, null, null);
        Assert.Equal(ImportanceDecision.Important, result.Decision);
        Assert.Equal(ConfidenceBand.High, result.Band);
    }

    [Fact]
    public void Decide_MediumConfidenceRelevant_DefaultsToReviewRequired()
    {
        var result = ClassificationDecisionPolicy.Decide(true, 0.6, null, null, null);
        Assert.Equal(ImportanceDecision.ReviewRequired, result.Decision);
        Assert.Equal(ConfidenceBand.Medium, result.Band);
    }

    [Fact]
    public void Decide_MediumConfidenceRelevant_TreatedAsImportant_WhenProfileOverridesPolicy()
    {
        var result = ClassificationDecisionPolicy.Decide(true, 0.6, null, null, treatMediumAsReviewRequired: false);
        Assert.Equal(ImportanceDecision.Important, result.Decision);
    }

    [Fact]
    public void Decide_LowConfidence_IsAlwaysReviewRequired_RegardlessOfRelevance()
    {
        var relevant = ClassificationDecisionPolicy.Decide(true, 0.2, null, null, null);
        var notRelevant = ClassificationDecisionPolicy.Decide(false, 0.2, null, null, null);
        Assert.Equal(ImportanceDecision.ReviewRequired, relevant.Decision);
        Assert.Equal(ConfidenceBand.Low, notRelevant.Band);
    }

    [Fact]
    public void Decide_ConfidentNotRelevant_IsNotImportant()
    {
        var result = ClassificationDecisionPolicy.Decide(false, 0.95, null, null, null);
        Assert.Equal(ImportanceDecision.NotImportant, result.Decision);
        Assert.Equal(ConfidenceBand.High, result.Band);
    }

    [Fact]
    public void Decide_UsesProfileSpecificThresholds_WhenProvided()
    {
        // A profile-specific high threshold of 0.99 means even 0.9 confidence falls to Medium.
        var result = ClassificationDecisionPolicy.Decide(true, 0.9, highThreshold: 0.99, mediumThreshold: 0.5, treatMediumAsReviewRequired: true);
        Assert.Equal(ConfidenceBand.Medium, result.Band);
        Assert.Equal(ImportanceDecision.ReviewRequired, result.Decision);
    }

    [Theory]
    [InlineData("business_correspondence")]
    [InlineData("vendor_pitch")]
    [InlineData("automated")]
    [InlineData("spam")]
    [InlineData("other")]
    public void Decide_RelevantButNotAnInquiryKind_NeverBecomesImportant(string kind)
    {
        var result = ClassificationDecisionPolicy.Decide(true, 0.95, null, null, null, true, true, false, kind);

        Assert.Equal(Iemas.Domain.Ai.ImportanceDecision.NotImportant, result.Decision);
        Assert.Contains("not a customer or dealer inquiry", result.Reason);
    }

    [Theory]
    [InlineData("customer_inquiry")]
    [InlineData("dealer_inquiry")]
    [InlineData(null)]
    public void Decide_InquiryKindOrNoKind_StaysImportant(string? kind)
    {
        var result = ClassificationDecisionPolicy.Decide(true, 0.95, null, null, null, true, true, false, kind);

        Assert.Equal(Iemas.Domain.Ai.ImportanceDecision.Important, result.Decision);
    }
}
