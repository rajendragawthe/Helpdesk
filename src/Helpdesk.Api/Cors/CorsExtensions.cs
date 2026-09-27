using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Api.Cors;

/// <summary>
/// Registers a CORS policy from the "CorsOrigins" config section (a string array), only when at
/// least one origin is configured. Mirrors the GraphApi/OpenRouter opt-in rule: an absent or
/// empty section leaves today's behavior (no CORS middleware at all) unchanged, so a fresh clone
/// or the E2E process - both proxied same-origin in dev - need no config to keep working.
/// </summary>
public static class CorsExtensions
{
    public const string DefaultPolicyName = "Frontend";

    public static bool TryAddCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("CorsOrigins").Get<string[]>() ?? [];
        if (origins.Length == 0)
        {
            return false;
        }

        services.AddCors(options => options.AddPolicy(
            DefaultPolicyName,
            policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

        return true;
    }
}
