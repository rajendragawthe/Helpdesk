using Azure.Identity;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;

namespace Helpdesk.Infrastructure.GraphApi;

public static class DependencyInjection
{
    /// <summary>
    /// Determines whether the "GraphApi" configuration section is present and enabled.
    /// Email ingestion is opt-in: a fresh clone or an E2E test run with no GraphApi
    /// configuration at all should not crash the host. A section that is present but
    /// explicitly disabled via "GraphApi:Enabled" = "false" is also treated as not configured.
    /// </summary>
    public static bool IsConfigured(IConfiguration configuration)
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

    public static IServiceCollection AddGraphApi(this IServiceCollection services, IConfiguration configuration)
    {
        if (!IsConfigured(configuration))
        {
            // No GraphApi section (or explicitly disabled) - email ingestion is opt-in,
            // so skip registration entirely rather than failing the whole host at startup.
            return services;
        }

        var options = new GraphApiOptions(
            TenantId: configuration["GraphApi:TenantId"]
                ?? throw new InvalidOperationException("GraphApi:TenantId is not configured."),
            ClientId: configuration["GraphApi:ClientId"]
                ?? throw new InvalidOperationException("GraphApi:ClientId is not configured."),
            ClientSecret: configuration["GraphApi:ClientSecret"]
                ?? throw new InvalidOperationException("GraphApi:ClientSecret is not configured."),
            MailboxAddress: configuration["GraphApi:MailboxAddress"]
                ?? throw new InvalidOperationException("GraphApi:MailboxAddress is not configured."));

        services.AddSingleton(options);

        services.AddSingleton(_ =>
        {
            var credential = new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
            return new GraphServiceClient(credential);
        });

        services.AddScoped<IMailClient, GraphMailClient>();

        return services;
    }
}
