using FluentValidation;
using Helpdesk.Api.Controllers;
using Helpdesk.Application.Tickets;

namespace Helpdesk.Api.Validators;

public class ReplyRequestValidator : AbstractValidator<ReplyRequest>
{
    public ReplyRequestValidator()
    {
        RuleFor(x => x.Text)
            .NotEmpty()
            .MaximumLength(TicketWorkflowService.MaxReplyLength);
    }
}
