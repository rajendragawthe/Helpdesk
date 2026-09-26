using Helpdesk.Api.Controllers;
using Helpdesk.Api.Validators;
using Helpdesk.Application.Tickets;

namespace Helpdesk.Api.Tests.Validators;

public class ReplyRequestValidatorTests
{
    private readonly ReplyRequestValidator _validator = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void BlankText_IsInvalid(string text)
    {
        Assert.False(_validator.Validate(new ReplyRequest(text)).IsValid);
    }

    [Fact]
    public void NullText_IsInvalid()
    {
        Assert.False(_validator.Validate(new ReplyRequest(null!)).IsValid);
    }

    [Fact]
    public void TextAtTheLimit_IsValid_AndOneOverIsInvalid()
    {
        Assert.True(_validator.Validate(new ReplyRequest(new string('x', TicketWorkflowService.MaxReplyLength))).IsValid);
        Assert.False(_validator.Validate(new ReplyRequest(new string('x', TicketWorkflowService.MaxReplyLength + 1))).IsValid);
    }

    [Fact]
    public void NormalText_IsValid()
    {
        Assert.True(_validator.Validate(new ReplyRequest("Thanks, we are on it.")).IsValid);
    }
}
