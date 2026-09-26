using Helpdesk.Core.Models;

namespace Helpdesk.Core.Tests;

public class TicketCategoriesTests
{
    [Theory]
    [InlineData("Billing", "Billing")]
    [InlineData("billing", "Billing")]
    [InlineData("  TECHNICAL ISSUE ", "Technical Issue")]
    [InlineData("Account Access", "Account Access")]
    public void Normalize_KnownCategory_ReturnsCanonicalCasing(string input, string expected)
    {
        Assert.Equal(expected, TicketCategories.Normalize(input));
    }

    [Theory]
    [InlineData("Refunds")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_UnknownOrEmpty_ReturnsOther(string? input)
    {
        Assert.Equal(TicketCategories.Other, TicketCategories.Normalize(input));
    }

    [Fact]
    public void All_ContainsOther()
    {
        Assert.Contains(TicketCategories.Other, TicketCategories.All);
    }
}
