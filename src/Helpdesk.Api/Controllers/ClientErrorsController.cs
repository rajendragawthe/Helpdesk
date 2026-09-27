using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.Api.Controllers;

/// <summary>
/// Receives frontend crash reports from the React app's ErrorBoundary and global
/// window.onerror/unhandledrejection handlers. Anonymous on purpose: a crash can happen before
/// sign-in or after a token has expired, and it must still be captured. Logs only - there is no
/// persistence here, this is a log stream, not a feature.
/// </summary>
[ApiController]
[Route("api/client-errors")]
[AllowAnonymous]
public class ClientErrorsController(ILogger<ClientErrorsController> logger) : ControllerBase
{
    [HttpPost]
    public IActionResult Report([FromBody] ClientErrorRequest request)
    {
        logger.LogWarning(
            "Frontend error reported: {Message} at {Url} ({UserAgent}). Stack: {Stack}",
            request.Message,
            request.Url,
            request.UserAgent,
            request.Stack);

        return Accepted();
    }
}

public record ClientErrorRequest(string Message, string? Stack, string Url, string UserAgent);
