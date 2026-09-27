using Helpdesk.Api.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Api.Tests.Observability;

public class AzureMonitorExtensionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void TryAddAzureMonitor_NoConnectionString_ReturnsFalseAndRegistersNothing()
    {
        var services = new ServiceCollection();

        var result = services.TryAddAzureMonitor(Config());

        Assert.False(result);
        Assert.Empty(services);
    }

    [Fact]
    public void TryAddAzureMonitor_WithConnectionString_ReturnsTrueAndRegistersOpenTelemetry()
    {
        var services = new ServiceCollection();

        var result = services.TryAddAzureMonitor(Config(
            ("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=00000000-0000-0000-0000-000000000000")));

        Assert.True(result);
        Assert.NotEmpty(services);
    }
}
