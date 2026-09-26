using Helpdesk.Application.Review;
using Helpdesk.Core.Enums;
using ClassificationEntity = Helpdesk.Core.Entities.Classification;

namespace Helpdesk.Application.Tests.Review;

public class ReviewPolicyTests
{
    private const double Threshold = 0.7;
    private const string Draft = "Hello, we are looking into it.";

    private static ClassificationEntity Cls(string category, double confidence) => new()
    {
        Id = Guid.NewGuid(),
        TicketId = Guid.NewGuid(),
        Category = category,
        Summary = "s",
        Confidence = confidence,
    };

    [Fact]
    public void Evaluate_ConfidentKnownCategoryWithDraft_IsNone()
    {
        Assert.Equal(ReviewReasons.None, ReviewPolicy.Evaluate(Cls("Billing", 0.95), Draft, Threshold));
    }

    [Fact]
    public void Evaluate_NoClassificationButDraft_IsClassificationFailedOnly()
    {
        Assert.Equal(ReviewReasons.ClassificationFailed, ReviewPolicy.Evaluate(null, Draft, Threshold));
    }

    [Fact]
    public void Evaluate_NoClassificationAndNoDraft_IsClassificationFailedAndDraftFailed()
    {
        Assert.Equal(
            ReviewReasons.ClassificationFailed | ReviewReasons.DraftFailed,
            ReviewPolicy.Evaluate(null, null, Threshold));
    }

    [Fact]
    public void Evaluate_ConfidenceBelowThreshold_IsLowConfidence()
    {
        Assert.Equal(ReviewReasons.LowConfidence, ReviewPolicy.Evaluate(Cls("Billing", 0.69), Draft, Threshold));
    }

    [Fact]
    public void Evaluate_ConfidenceEqualToThreshold_IsNotFlagged()
    {
        Assert.Equal(ReviewReasons.None, ReviewPolicy.Evaluate(Cls("Billing", 0.7), Draft, 0.7));
    }

    [Fact]
    public void Evaluate_ConfidenceAboveThreshold_IsNotFlagged()
    {
        Assert.Equal(ReviewReasons.None, ReviewPolicy.Evaluate(Cls("Billing", 0.71), Draft, Threshold));
    }

    [Fact]
    public void Evaluate_ThresholdZero_NeverFlagsLowConfidence()
    {
        Assert.Equal(ReviewReasons.None, ReviewPolicy.Evaluate(Cls("Billing", 0.0), Draft, 0.0));
    }

    [Fact]
    public void Evaluate_ThresholdOne_FlagsAnythingBelowCertainty()
    {
        Assert.Equal(ReviewReasons.LowConfidence, ReviewPolicy.Evaluate(Cls("Billing", 0.99), Draft, 1.0));
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("other")]
    [InlineData("OTHER")]
    public void Evaluate_CategoryOther_IsCategoryOtherRegardlessOfCase(string category)
    {
        Assert.Equal(ReviewReasons.CategoryOther, ReviewPolicy.Evaluate(Cls(category, 0.95), Draft, Threshold));
    }

    [Fact]
    public void Evaluate_LowConfidenceOtherAndNoDraft_CombinesAllThree()
    {
        Assert.Equal(
            ReviewReasons.LowConfidence | ReviewReasons.CategoryOther | ReviewReasons.DraftFailed,
            ReviewPolicy.Evaluate(Cls("Other", 0.2), null, Threshold));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Evaluate_BlankDraft_IsDraftFailed(string? draft)
    {
        Assert.Equal(ReviewReasons.DraftFailed, ReviewPolicy.Evaluate(Cls("Billing", 0.95), draft, Threshold));
    }
}
