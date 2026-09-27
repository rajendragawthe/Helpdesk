using Helpdesk.Api.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Helpdesk.Api.Tests.Observability;

public class ObservabilityExtensionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void AddObservability_NoOtelSection_StartsWithConsoleExporterAndResolves()
    {
        var services = new ServiceCollection();

        services.AddObservability(Config());

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<TracerProvider>());
        Assert.NotNull(provider.GetService<MeterProvider>());
    }

    [Fact]
    public void AddObservability_WithOtlpEndpoint_StillResolvesWithoutThrowing()
    {
        var services = new ServiceCollection();

        services.AddObservability(Config(("Otel:OtlpEndpoint", "http://localhost:4317")));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<TracerProvider>());
        Assert.NotNull(provider.GetService<MeterProvider>());
    }
}
