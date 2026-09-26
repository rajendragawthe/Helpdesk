using System.Reflection;
using System.Security.Claims;
using Helpdesk.Api.Auth;
using Helpdesk.Api.Controllers;
using Helpdesk.Api.Tests.TestDoubles;
using Helpdesk.Application.Tickets;
using Helpdesk.Core.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.Api.Tests.Controllers;

public class TicketsControllerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private readonly FakeTicketWorkflowService _workflow = new();

    private static ClaimsPrincipal PrincipalFor(bool withUserId = true, string role = "Agent", bool withUpn = false)
    {
        var identity = new ClaimsIdentity("test");
        if (withUserId)
        {
            identity.AddClaim(new Claim(HelpdeskUserClaimsTransformation.UserIdClaimType, UserId.ToString()));
        }

        identity.AddClaim(new Claim(ClaimTypes.Role, role));
        identity.AddClaim(new Claim("preferred_username", "alice@example.com"));
        if (withUpn)
        {
            identity.AddClaim(new Claim(ClaimTypes.Upn, "upn@example.com"));
        }

        return new ClaimsPrincipal(identity);
    }

    private TicketsController Create(ClaimsPrincipal? principal = null) => new(_workflow)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal ?? PrincipalFor() },
        },
    };

    private static TicketDetail Detail() => new(
        Guid.NewGuid(), "s", "c@example.com", TicketStatus.InReview, null, null, null, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, null, []);

    [Fact]
    public void Controller_RequiresTheAgentOnlyPolicy()
    {
        var attribute = typeof(TicketsController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("AgentOnly", attribute?.Policy);
    }

    [Fact]
    public async Task List_DefaultsToTheQueueFilter_AndPassesTheCaller()
    {
        var result = await Create().List(null);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(TicketFilter.Queue, _workflow.LastFilter);
        Assert.Equal(new TicketCaller(UserId, "alice@example.com", IsAdmin: false), _workflow.LastCaller);
    }

    [Theory]
    [InlineData("mine", TicketFilter.Mine)]
    [InlineData("ALL", TicketFilter.All)]
    [InlineData("queue", TicketFilter.Queue)]
    public async Task List_ParsesTheFilterCaseInsensitively(string value, TicketFilter expected)
    {
        await Create().List(value);

        Assert.Equal(expected, _workflow.LastFilter);
    }

    [Fact]
    public async Task List_UnknownFilter_IsABadRequest()
    {
        var result = await Create().List("everything");

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(result).StatusCode);
        Assert.Null(_workflow.LastAction);
    }

    [Fact]
    public async Task Caller_IsAdminWhenTheRoleClaimIsAdmin()
    {
        await Create(PrincipalFor(role: "Admin")).List("all");

        Assert.True(_workflow.LastCaller!.IsAdmin);
    }

    [Fact]
    public async Task MissingUserIdClaim_IsForbiddenAndNothingRuns()
    {
        var result = await Create(PrincipalFor(withUserId: false)).Claim(Guid.NewGuid());

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Null(_workflow.LastAction);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("99")]
    [InlineData("queue,mine")]
    public async Task List_NumericOrListFilter_IsABadRequest(string value)
    {
        var result = await Create().List(value);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(_workflow.LastAction);
    }

    [Fact]
    public async Task Caller_EmailPrefersUpnOverPreferredUsername()
    {
        await Create(PrincipalFor(withUpn: true)).List(null);

        Assert.Equal("upn@example.com", _workflow.LastCaller!.Email);
    }

    [Fact]
    public async Task Get_MissingUserIdClaim_IsForbiddenAndNothingRuns()
    {
        var result = await Create(PrincipalFor(withUserId: false)).Get(Guid.NewGuid());

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Null(_workflow.LastAction);
    }

    [Fact]
    public async Task Release_MissingUserIdClaim_IsForbiddenAndNothingRuns()
    {
        var result = await Create(PrincipalFor(withUserId: false)).Release(Guid.NewGuid());

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Null(_workflow.LastAction);
    }

    [Fact]
    public async Task Reply_MissingUserIdClaim_IsForbiddenAndNothingRuns()
    {
        var result = await Create(PrincipalFor(withUserId: false))
            .Reply(Guid.NewGuid(), new ReplyRequest("Hi"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Null(_workflow.LastAction);
    }

    [Fact]
    public async Task Claim_Conflict_MapsTo409()
    {
        _workflow.DetailResult = TicketResult<TicketDetail>.Fail(TicketOutcome.Conflict, "taken");

        var result = await Create().Claim(Guid.NewGuid());

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    [Theory]
    [InlineData(TicketOutcome.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(TicketOutcome.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(TicketOutcome.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(TicketOutcome.Invalid, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(TicketOutcome.SendFailed, StatusCodes.Status502BadGateway)]
    [InlineData(TicketOutcome.SentButNotSaved, StatusCodes.Status500InternalServerError)]
    public async Task Reply_MapsEveryOutcomeToItsStatusCode(TicketOutcome outcome, int expected)
    {
        _workflow.DetailResult = TicketResult<TicketDetail>.Fail(outcome, "a message");

        var result = await Create().Reply(Guid.NewGuid(), new ReplyRequest("Hi"), CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expected, objectResult.StatusCode);
        Assert.Contains("a message", System.Text.Json.JsonSerializer.Serialize(objectResult.Value));
    }

    [Fact]
    public async Task Reply_Success_ReturnsTheDetailAndPassesTheText()
    {
        var detail = Detail();
        _workflow.DetailResult = TicketResult<TicketDetail>.Ok(detail);
        var ticketId = Guid.NewGuid();

        var result = await Create().Reply(ticketId, new ReplyRequest("Hello there"), CancellationToken.None);

        Assert.Same(detail, Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("reply", _workflow.LastAction);
        Assert.Equal(ticketId, _workflow.LastTicketId);
        Assert.Equal("Hello there", _workflow.LastText);
    }

    [Fact]
    public async Task GetClaimAndRelease_CallTheMatchingWorkflowMethod()
    {
        _workflow.DetailResult = TicketResult<TicketDetail>.Ok(Detail());
        var controller = Create();
        var id = Guid.NewGuid();

        await controller.Get(id);
        Assert.Equal("get", _workflow.LastAction);
        await controller.Claim(id);
        Assert.Equal("claim", _workflow.LastAction);
        await controller.Release(id);
        Assert.Equal("release", _workflow.LastAction);
        Assert.Equal(id, _workflow.LastTicketId);
    }
}
