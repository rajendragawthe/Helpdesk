using Azure.Identity;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;

namespace Helpdesk.Infrastructure.GraphApi;

public static class DependencyInjection
{
    public static IServiceCollection AddGraphApi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new GraphApiOptions(
            TenantId: configuration["GraphApi:TenantId"]
                ?? throw new InvalidOperationException("GraphApi:TenantId is not configured."),
            ClientId: configuration["GraphApi:ClientId"]
                ?? throw new InvalidOperationException("GraphApi:ClientId is not configured."),
            ClientSecret: configuration["GraphApi:ClientSecret"]
                ?? throw new InvalidOperationException("GraphApi:ClientSecret is not configured."),
            MailboxAddress: configuration["GraphApi:MailboxAddress"]
                ?? throw new InvalidOperationException("GraphApi:MailboxAddress is not configured."),
            PollingIntervalSeconds: int.TryParse(configuration["GraphApi:PollingIntervalSeconds"], out var seconds)
                ? seconds
                : 60);

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
