using Helpdesk.Application.Review;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Application.Tests.Review;

public class ReviewDependencyInjectionTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static ServiceCollection Register(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddApplication(configuration);
        return services;
    }

    [Fact]
    public void OpenRouterConfigured_RegistersScopedReviewFlagServiceAndDefaultOptions()
    {
        var services = Register(Config(("OpenRouter:Model", "m")));

        var service = Assert.Single(services, d => d.ServiceType == typeof(IReviewFlagService));
        Assert.Equal(typeof(ReviewFlagService), service.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, service.Lifetime);

        var options = Assert.Single(services, d => d.ServiceType == typeof(ReviewOptions));
        Assert.Equal(0.7, ((ReviewOptions)options.ImplementationInstance!).ConfidenceThreshold);
    }

    [Fact]
    public void ConfiguredThreshold_IsUsed()
    {
        var services = Register(Config(("OpenRouter:Model", "m"), ("Review:ConfidenceThreshold", "0.55")));

        var options = Assert.Single(services, d => d.ServiceType == typeof(ReviewOptions));
        Assert.Equal(0.55, ((ReviewOptions)options.ImplementationInstance!).ConfidenceThreshold);
    }

    [Fact]
    public void InvalidThreshold_WithOpenRouterConfigured_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Register(Config(("OpenRouter:Model", "m"), ("Review:ConfidenceThreshold", "abc"))));

        Assert.Contains("Review:ConfidenceThreshold", ex.Message);
    }

    [Fact]
    public void OpenRouterAbsent_RegistersNothing_EvenWithAnInvalidThreshold()
    {
        var services = Register(Config(("Review:ConfidenceThreshold", "abc")));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IReviewFlagService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ReviewOptions));
    }

    [Fact]
    public void OpenRouterDisabled_RegistersNothing()
    {
        var services = Register(Config(("OpenRouter:Enabled", "false"), ("OpenRouter:Model", "m")));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IReviewFlagService));
    }
}
