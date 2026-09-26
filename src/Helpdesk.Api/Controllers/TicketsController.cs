using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Helpdesk.Api.Auth;
using Helpdesk.Application.Tickets;
using Helpdesk.Core.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.Api.Controllers;

[ApiController]
[Route("api/tickets")]
[Authorize(Policy = "AgentOnly")]
public class TicketsController(ITicketWorkflowService workflow) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? filter)
    {
        if (!TryGetCaller(out var caller))
        {
            return NoCaller();
        }

        var name = filter ?? nameof(TicketFilter.Queue);
        if (!Enum.GetNames<TicketFilter>().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "filter must be one of: queue, mine, all." });
        }

        var parsed = Enum.Parse<TicketFilter>(name, ignoreCase: true);

        return ToResult(await workflow.ListAsync(caller, parsed));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        if (!TryGetCaller(out _))
        {
            return NoCaller();
        }

        return ToResult(await workflow.GetAsync(id));
    }

    [HttpPost("{id:guid}/claim")]
    public async Task<IActionResult> Claim(Guid id)
    {
        if (!TryGetCaller(out var caller))
        {
            return NoCaller();
        }

        return ToResult(await workflow.ClaimAsync(caller, id));
    }

    [HttpPost("{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id)
    {
        if (!TryGetCaller(out var caller))
        {
            return NoCaller();
        }

        return ToResult(await workflow.ReleaseAsync(caller, id));
    }

    [HttpPut("{id:guid}/reply")]
    public async Task<IActionResult> Reply(Guid id, [FromBody] ReplyRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCaller(out var caller))
        {
            return NoCaller();
        }

        return ToResult(await workflow.SendReplyAsync(caller, id, request.Text, cancellationToken));
    }

    private bool TryGetCaller([NotNullWhen(true)] out TicketCaller? caller)
    {
        caller = null;
        if (!Guid.TryParse(User.FindFirstValue(HelpdeskUserClaimsTransformation.UserIdClaimType), out var userId))
        {
            return false;
        }

        var email = User.FindFirstValue(ClaimTypes.Upn) ?? User.FindFirstValue("preferred_username") ?? string.Empty;
        caller = new TicketCaller(userId, email, User.IsInRole(nameof(Role.Admin)));
        return true;
    }

    private IActionResult NoCaller() =>
        StatusCode(StatusCodes.Status403Forbidden, new { message = "Your account isn't registered in Helpdesk yet." });

    private IActionResult ToResult<T>(TicketResult<T> result)
    {
        var body = new { message = result.Message };
        return result.Outcome switch
        {
            TicketOutcome.Success => Ok(result.Value),
            TicketOutcome.NotFound => NotFound(body),
            TicketOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, body),
            TicketOutcome.Conflict => Conflict(body),
            TicketOutcome.Invalid => UnprocessableEntity(body),
            TicketOutcome.SendFailed => StatusCode(StatusCodes.Status502BadGateway, body),
            _ => StatusCode(StatusCodes.Status500InternalServerError, body),
        };
    }
}

public record ReplyRequest(string Text);
