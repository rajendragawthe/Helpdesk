using Helpdesk.Application.EmailIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Helpdesk.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<EmailIngestionService>();
        services.AddHostedService<EmailIngestionBackgroundService>();

        return services;
    }
}
