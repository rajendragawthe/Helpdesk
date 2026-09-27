using System.Reflection;
using Helpdesk.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Api.Tests.Controllers;

public class ClientErrorsControllerTests
{
    [Fact]
    public void Controller_AllowsAnonymousAccess()
    {
        var attribute = typeof(ClientErrorsController).GetCustomAttribute<AllowAnonymousAttribute>();

        Assert.NotNull(attribute);
    }

    [Fact]
    public void Report_ValidRequest_ReturnsAccepted()
    {
        var controller = new ClientErrorsController(NullLogger<ClientErrorsController>.Instance);

        var result = controller.Report(new ClientErrorRequest(
            "Boom", "at foo (app.js:1:1)", "https://app.example.com/tickets/1", "Mozilla/5.0"));

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public void Report_RequestWithNullStack_StillReturnsAccepted()
    {
        var controller = new ClientErrorsController(NullLogger<ClientErrorsController>.Instance);

        var result = controller.Report(new ClientErrorRequest(
            "Boom", null, "https://app.example.com/tickets/1", "Mozilla/5.0"));

        Assert.IsType<AcceptedResult>(result);
    }
}
