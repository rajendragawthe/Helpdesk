using Helpdesk.Api.Controllers;
using Helpdesk.Api.Validators;

namespace Helpdesk.Api.Tests.Validators;

public class ClientErrorRequestValidatorTests
{
    private readonly ClientErrorRequestValidator _validator = new();

    private static ClientErrorRequest Valid(
        string message = "Boom",
        string? stack = "at foo (app.js:1:1)",
        string url = "https://app.example.com/tickets/1",
        string userAgent = "Mozilla/5.0") =>
        new(message, stack, url, userAgent);

    [Fact]
    public void ValidRequest_IsValid()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void ValidRequest_WithNullStack_IsValid()
    {
        Assert.True(_validator.Validate(Valid(stack: null)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankMessage_IsInvalid(string message)
    {
        Assert.False(_validator.Validate(Valid(message: message)).IsValid);
    }

    [Fact]
    public void MessageOverLimit_IsInvalid()
    {
        Assert.False(_validator.Validate(Valid(message: new string('x', 2001))).IsValid);
    }

    [Fact]
    public void MessageAtLimit_IsValid()
    {
        Assert.True(_validator.Validate(Valid(message: new string('x', 2000))).IsValid);
    }

    [Fact]
    public void StackOverLimit_IsInvalid()
    {
        Assert.False(_validator.Validate(Valid(stack: new string('x', 8001))).IsValid);
    }

    [Fact]
    public void StackAtLimit_IsValid()
    {
        Assert.True(_validator.Validate(Valid(stack: new string('x', 8000))).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BlankUrl_IsInvalid(string? url)
    {
        Assert.False(_validator.Validate(Valid(url: url!)).IsValid);
    }

    [Fact]
    public void UrlOverLimit_IsInvalid()
    {
        Assert.False(_validator.Validate(Valid(url: new string('x', 501))).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BlankUserAgent_IsInvalid(string? userAgent)
    {
        Assert.False(_validator.Validate(Valid(userAgent: userAgent!)).IsValid);
    }

    [Fact]
    public void UserAgentOverLimit_IsInvalid()
    {
        Assert.False(_validator.Validate(Valid(userAgent: new string('x', 501))).IsValid);
    }
}
