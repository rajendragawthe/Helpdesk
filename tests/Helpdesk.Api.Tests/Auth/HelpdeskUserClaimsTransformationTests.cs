using System.Security.Claims;
using Helpdesk.Api.Auth;
using Helpdesk.Api.Tests.TestDoubles;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;

namespace Helpdesk.Api.Tests.Auth;

public class HelpdeskUserClaimsTransformationTests
{
    private readonly FakeUserRepository _users = new();

    private static ClaimsPrincipal Principal(string? objectId, string? email)
    {
        var identity = new ClaimsIdentity("test");
        if (objectId is not null)
        {
            identity.AddClaim(new Claim("oid", objectId));
        }

        if (email is not null)
        {
            identity.AddClaim(new Claim("preferred_username", email));
        }

        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task RegisteredUser_GetsRoleRegisteredAndUserIdClaims()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "a@example.com", DisplayName = "A", Role = Role.Agent, ExternalObjectId = "obj-1" };
        _users.Users.Add(user);

        var result = await new HelpdeskUserClaimsTransformation(_users).TransformAsync(Principal("obj-1", "a@example.com"));

        Assert.Equal("true", result.FindFirstValue(HelpdeskUserClaimsTransformation.RegisteredClaimType));
        Assert.Equal("Agent", result.FindFirstValue(ClaimTypes.Role));
        Assert.Equal(user.Id.ToString(), result.FindFirstValue(HelpdeskUserClaimsTransformation.UserIdClaimType));
    }

    [Fact]
    public async Task UnknownUser_IsNotRegisteredAndHasNoUserIdClaim()
    {
        var result = await new HelpdeskUserClaimsTransformation(_users).TransformAsync(Principal("nobody", "nobody@example.com"));

        Assert.Equal("false", result.FindFirstValue(HelpdeskUserClaimsTransformation.RegisteredClaimType));
        Assert.Null(result.FindFirstValue(HelpdeskUserClaimsTransformation.UserIdClaimType));
        Assert.Null(result.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task ForgedUserIdClaim_IsReplacedByTheRealUserId()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "a@example.com", DisplayName = "A", Role = Role.Agent, ExternalObjectId = "obj-1" };
        _users.Users.Add(user);
        var principal = Principal("obj-1", "a@example.com");
        ((ClaimsIdentity)principal.Identity!).AddClaim(
            new Claim(HelpdeskUserClaimsTransformation.UserIdClaimType, Guid.NewGuid().ToString()));

        var result = await new HelpdeskUserClaimsTransformation(_users).TransformAsync(principal);

        var claims = result.FindAll(HelpdeskUserClaimsTransformation.UserIdClaimType).ToList();
        Assert.Single(claims);
        Assert.Equal(user.Id.ToString(), claims[0].Value);
    }

    [Fact]
    public async Task ForgedUserIdClaim_OnAnUnknownUser_IsRemoved()
    {
        var principal = Principal("nobody", "nobody@example.com");
        ((ClaimsIdentity)principal.Identity!).AddClaim(
            new Claim(HelpdeskUserClaimsTransformation.UserIdClaimType, Guid.NewGuid().ToString()));

        var result = await new HelpdeskUserClaimsTransformation(_users).TransformAsync(principal);

        Assert.Empty(result.FindAll(HelpdeskUserClaimsTransformation.UserIdClaimType));
    }

    [Fact]
    public async Task TransformingTwice_DoesNotDuplicateTheUserIdClaim()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "a@example.com", DisplayName = "A", Role = Role.Admin, ExternalObjectId = "obj-1" };
        _users.Users.Add(user);
        var transformation = new HelpdeskUserClaimsTransformation(_users);
        var principal = Principal("obj-1", "a@example.com");

        await transformation.TransformAsync(principal);
        await transformation.TransformAsync(principal);

        Assert.Single(principal.FindAll(HelpdeskUserClaimsTransformation.UserIdClaimType));
    }
}
