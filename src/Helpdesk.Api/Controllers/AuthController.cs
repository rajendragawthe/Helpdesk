using System.Security.Claims;
using Helpdesk.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;

namespace Helpdesk.Api.Controllers;

[ApiController]
[Route("api/auth")]
[Authorize]
public class AuthController(IUserRepository userRepository) : ControllerBase
{
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var name = User.FindFirstValue("name") ?? User.Identity?.Name;
        var email = User.FindFirstValue(ClaimTypes.Upn) ?? User.FindFirstValue("preferred_username");
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

        var objectId = User.GetObjectId();
        if (!string.IsNullOrEmpty(objectId))
        {
            var linkedUser = await userRepository.GetByExternalObjectIdAsync(objectId);
            if (linkedUser is null && !string.IsNullOrEmpty(email))
            {
                var userByEmail = await userRepository.GetByEmailAsync(email);
                if (userByEmail is not null && userByEmail.ExternalObjectId is null)
                {
                    userByEmail.ExternalObjectId = objectId;
                    await userRepository.UpdateAsync(userByEmail);
                }
            }
        }

        return Ok(new { name, email, roles });
    }
}
