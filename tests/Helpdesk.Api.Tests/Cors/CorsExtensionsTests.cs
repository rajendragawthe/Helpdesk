using Helpdesk.Api.Cors;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tests.Cors;

public class CorsExtensionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void TryAddCors_NoSection_ReturnsFalseAndRegistersNoPolicy()
    {
        var services = new ServiceCollection();

        var added = services.TryAddCors(Config());

        Assert.False(added);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ICorsService));
    }

    [Fact]
    public void TryAddCors_EmptyArray_ReturnsFalse()
    {
        var services = new ServiceCollection();

        // An explicitly-present but empty CorsOrigins section behaves the same as an absent one.
        var added = services.TryAddCors(Config(("CorsOrigins", null)));

        Assert.False(added);
    }

    [Fact]
    public void TryAddCors_WithOrigins_RegistersPolicyWithExactlyTheConfiguredOrigins()
    {
        var services = new ServiceCollection();
        var config = Config(("CorsOrigins:0", "http://localhost:5173"));

        var added = services.TryAddCors(config);

        Assert.True(added);
        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(CorsExtensions.DefaultPolicyName);
        Assert.NotNull(policy);
        Assert.Equal(["http://localhost:5173"], policy!.Origins);
    }

    [Fact]
    public void TryAddCors_WithMultipleOrigins_RegistersAllOfThem()
    {
        var services = new ServiceCollection();
        var config = Config(
            ("CorsOrigins:0", "http://localhost:5173"),
            ("CorsOrigins:1", "https://helpdesk.example.com"));

        services.TryAddCors(config);

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(CorsExtensions.DefaultPolicyName);
        Assert.Equal(["http://localhost:5173", "https://helpdesk.example.com"], policy!.Origins);
    }
}
