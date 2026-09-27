using FluentValidation;
using Helpdesk.Api.Controllers;

namespace Helpdesk.Api.Validators;

public class ClientErrorRequestValidator : AbstractValidator<ClientErrorRequest>
{
    public ClientErrorRequestValidator()
    {
        RuleFor(x => x.Message).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Stack).MaximumLength(8000);
        RuleFor(x => x.Url).NotEmpty().MaximumLength(500);
        RuleFor(x => x.UserAgent).NotEmpty().MaximumLength(500);
    }
}
