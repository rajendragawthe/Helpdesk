using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Infrastructure.Tests.Ai;

public class OpenRouterDependencyInjectionTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void AddOpenRouter_NoSection_RegistersNothing()
    {
        var services = new ServiceCollection();

        services.AddOpenRouter(Config());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAiService));
    }

    [Fact]
    public void AddOpenRouter_ExplicitlyDisabled_RegistersNothing()
    {
        var services = new ServiceCollection();

        services.AddOpenRouter(Config(("OpenRouter:Enabled", "false"), ("OpenRouter:Model", "m")));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAiService));
    }

    [Fact]
    public void AddOpenRouter_SectionWithoutApiKey_Throws()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddOpenRouter(Config(("OpenRouter:Model", "m"))));

        Assert.Contains("OpenRouter:ApiKey", ex.Message);
    }

    [Fact]
    public void AddOpenRouter_Configured_ResolvesAiService()
    {
        var services = new ServiceCollection();
        services.AddOpenRouter(Config(("OpenRouter:ApiKey", "k"), ("OpenRouter:Model", "m")));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<OpenRouterAiService>(provider.GetRequiredService<IAiService>());
    }

    [Fact]
    public void AddOpenRouter_NoModel_UsesDefault()
    {
        var services = new ServiceCollection();
        services.AddOpenRouter(Config(("OpenRouter:ApiKey", "k")));

        using var provider = services.BuildServiceProvider();

        Assert.False(string.IsNullOrWhiteSpace(provider.GetRequiredService<OpenRouterOptions>().Model));
    }
}
