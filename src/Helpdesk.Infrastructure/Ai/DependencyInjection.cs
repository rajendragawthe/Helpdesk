using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Infrastructure.Ai;

public static class DependencyInjection
{
    private const string DefaultModel = "openai/gpt-4o-mini";
    private const string BaseUrl = "https://openrouter.ai/api/v1/";

    /// <summary>
    /// Mirrors the GraphApi opt-in rule: AI classification is skipped entirely when the
    /// "OpenRouter" section is absent or "OpenRouter:Enabled" is "false", so a fresh clone or an
    /// E2E run without an API key still starts.
    /// </summary>
    public static bool IsConfigured(IConfiguration configuration)
    {
        if (!configuration.GetSection("OpenRouter").Exists())
        {
            return false;
        }

        return !string.Equals(configuration["OpenRouter:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
    }

    public static IServiceCollection AddOpenRouter(this IServiceCollection services, IConfiguration configuration)
    {
        if (!IsConfigured(configuration))
        {
            return services;
        }

        var apiKey = configuration["OpenRouter:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenRouter:ApiKey is not configured.");
        }

        var model = configuration["OpenRouter:Model"];
        services.AddSingleton(new OpenRouterOptions(apiKey, string.IsNullOrWhiteSpace(model) ? DefaultModel : model));

        services.AddHttpClient<IAiService, OpenRouterAiService>(client =>
        {
            client.BaseAddress = new Uri(BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
