using System.Security.Claims;
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
        var name = User.FindFirstValue("name") ?? User.Identity?.Name;
        var email = User.FindFirstValue(ClaimTypes.Upn) ?? User.FindFirstValue("preferred_username");
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

        return Ok(new { name, email, roles });
    }
}
