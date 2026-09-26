using System.Security.Claims;
using Helpdesk.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.Api.Controllers;

[ApiController]
[Route("api/auth")]
[Authorize]
public class AuthController : ControllerBase
{
    [HttpGet("me")]
    public IActionResult Me()
    {
        var isRegistered = User.FindFirstValue(HelpdeskUserClaimsTransformation.RegisteredClaimType) == "true";
        if (!isRegistered)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "Your account isn't registered in Helpdesk yet. Ask an administrator to add you as a user."
            });
        }

        var name = User.FindFirstValue("name") ?? User.Identity?.Name;
        var email = User.FindFirstValue(ClaimTypes.Upn) ?? User.FindFirstValue("preferred_username");
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

        var id = User.FindFirstValue(HelpdeskUserClaimsTransformation.UserIdClaimType);

        return Ok(new { id, name, email, roles });
    }
}
