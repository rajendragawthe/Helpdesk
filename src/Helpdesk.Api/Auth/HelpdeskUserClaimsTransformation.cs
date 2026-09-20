using System.Security.Claims;
using Helpdesk.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Identity.Web;

namespace Helpdesk.Api.Auth;

/// <summary>
/// Resolves the authenticated Entra identity to a row in our own Users table and enriches the
/// principal with a role claim from that row. Authorization policies (AdminOnly/AgentOnly) key off
/// this claim, so a caller with no matching Users row never satisfies them, regardless of what
/// Entra ID itself thinks of them. HelpdeskRegisteredClaimType records whether a match was found, so
/// AuthController.Me() can distinguish "no app account" from other failures.
/// </summary>
public class HelpdeskUserClaimsTransformation(IUserRepository userRepository) : IClaimsTransformation
{
    public const string RegisteredClaimType = "helpdesk_registered";
    private const string ProcessedClaimType = "helpdesk_claims_processed";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == ProcessedClaimType))
        {
            return principal;
        }

        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(ProcessedClaimType, "true"));

        var objectId = principal.GetObjectId();
        var email = principal.FindFirstValue(ClaimTypes.Upn) ?? principal.FindFirstValue("preferred_username");

        var user = string.IsNullOrEmpty(objectId)
            ? null
            : await userRepository.GetByExternalObjectIdAsync(objectId);

        if (user is null && !string.IsNullOrEmpty(email))
        {
            user = await userRepository.GetByEmailAsync(email);
            if (user is not null && user.ExternalObjectId is null && !string.IsNullOrEmpty(objectId))
            {
                user.ExternalObjectId = objectId;
                await userRepository.UpdateAsync(user);
            }
        }

        if (user is not null)
        {
            identity.AddClaim(new Claim(RegisteredClaimType, "true"));
            identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
        }
        else
        {
            identity.AddClaim(new Claim(RegisteredClaimType, "false"));
        }

        principal.AddIdentity(identity);
        return principal;
    }
}
