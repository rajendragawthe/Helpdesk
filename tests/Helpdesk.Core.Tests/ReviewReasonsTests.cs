using Helpdesk.Core.Enums;

namespace Helpdesk.Core.Tests;

public class ReviewReasonsTests
{
    // These numeric values are stored in the database and exposed by Phase 7 DTOs: never renumber.
    [Theory]
    [InlineData(ReviewReasons.None, 0)]
    [InlineData(ReviewReasons.LowConfidence, 1)]
    [InlineData(ReviewReasons.ClassificationFailed, 2)]
    [InlineData(ReviewReasons.CategoryOther, 4)]
    [InlineData(ReviewReasons.DraftFailed, 8)]
    public void Values_AreTheStoredContract(ReviewReasons reason, int expected)
    {
        Assert.Equal(expected, (int)reason);
    }

    [Fact]
    public void Reasons_CombineAsFlags()
    {
        var combined = ReviewReasons.LowConfidence | ReviewReasons.DraftFailed;

        Assert.True(combined.HasFlag(ReviewReasons.LowConfidence));
        Assert.True(combined.HasFlag(ReviewReasons.DraftFailed));
        Assert.False(combined.HasFlag(ReviewReasons.CategoryOther));
        Assert.Equal(9, (int)combined);
    }
}
