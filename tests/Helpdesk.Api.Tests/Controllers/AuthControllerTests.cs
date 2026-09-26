using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Api.Auth;
using Helpdesk.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.Api.Tests.Controllers;

public class AuthControllerTests
{
    private static AuthController ControllerFor(ClaimsPrincipal principal) => new()
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
    };

    [Fact]
    public void Me_RegisteredUser_ReturnsTheUserId()
    {
        var userId = Guid.NewGuid();
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(HelpdeskUserClaimsTransformation.RegisteredClaimType, "true"));
        identity.AddClaim(new Claim(HelpdeskUserClaimsTransformation.UserIdClaimType, userId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Agent"));
        identity.AddClaim(new Claim("name", "Alice"));
        identity.AddClaim(new Claim("preferred_username", "alice@example.com"));

        var result = Assert.IsType<OkObjectResult>(ControllerFor(new ClaimsPrincipal(identity)).Me());

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(userId.ToString(), doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Alice", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("alice@example.com", doc.RootElement.GetProperty("email").GetString());
        Assert.Equal("Agent", doc.RootElement.GetProperty("roles")[0].GetString());
    }

    [Fact]
    public void Me_UnregisteredUser_IsForbidden()
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(HelpdeskUserClaimsTransformation.RegisteredClaimType, "false"));

        var result = Assert.IsType<ObjectResult>(ControllerFor(new ClaimsPrincipal(identity)).Me());

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }
}
