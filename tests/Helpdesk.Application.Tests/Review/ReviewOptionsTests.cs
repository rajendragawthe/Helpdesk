using Helpdesk.Application.Review;

namespace Helpdesk.Application.Tests.Review;

public class ReviewOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_MissingValue_UsesDefaultOfPointSeven(string? value)
    {
        Assert.Equal(0.7, ReviewOptions.Parse(value).ConfidenceThreshold);
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("0", 0.0)]
    [InlineData("1", 1.0)]
    [InlineData(" 0.85 ", 0.85)]
    public void Parse_ValidValue_IsUsed(string value, double expected)
    {
        Assert.Equal(expected, ReviewOptions.Parse(value).ConfidenceThreshold);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0,7")]
    public void Parse_InvalidValue_ThrowsMentioningTheKey(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ReviewOptions.Parse(value));

        Assert.Contains("Review:ConfidenceThreshold", ex.Message);
    }
}
