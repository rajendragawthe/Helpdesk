using Helpdesk.Application.Classification;
using Helpdesk.Application.EmailIngestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Helpdesk.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        // Both EmailIngestionService and its hosted service depend on Graph API being
        // configured (see Helpdesk.Infrastructure.GraphApi.DependencyInjection.IsConfigured
        // for the authoritative presence check). Helpdesk.Application cannot reference
        // Helpdesk.Infrastructure, so the same "GraphApi" section presence/Enabled check is
        // duplicated here rather than introducing a shared abstraction for it.
        //
        // EmailIngestionService itself must also be gated (not just the hosted service):
        // ASP.NET Core's Development-environment service provider validates every
        // registration's constructor dependencies at Build() time (ValidateOnBuild), so
        // registering EmailIngestionService unconditionally while IMailClient is only
        // conditionally registered by AddGraphApi crashes the host at startup whenever
        // Graph API is not configured - "harmless to register a scoped service that's
        // never resolved" does not hold under that validation.
        if (IsGraphApiConfigured(configuration))
        {
            services.AddScoped<EmailIngestionService>();
            services.AddHostedService<EmailIngestionBackgroundService>();
        }

        // ClassificationService needs IAiService, which Infrastructure only registers when the
        // "OpenRouter" section is present and enabled. Same ValidateOnBuild reasoning as above, so
        // the presence check is duplicated here rather than registering unconditionally.
        if (IsOpenRouterConfigured(configuration))
        {
            services.AddScoped<IClassificationService, ClassificationService>();
        }

        return services;
    }

    private static bool IsOpenRouterConfigured(IConfiguration configuration)
    {
        if (!configuration.GetSection("OpenRouter").Exists())
        {
            return false;
        }

        return !string.Equals(configuration["OpenRouter:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGraphApiConfigured(IConfiguration configuration)
    {
        if (!configuration.GetSection("GraphApi").Exists())
        {
            return false;
        }

        if (string.Equals(configuration["GraphApi:Enabled"], "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
