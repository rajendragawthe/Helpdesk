using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Api.Observability;

/// <summary>
/// Exports the existing OpenTelemetry instrumentation (see ObservabilityExtensions) to Azure
/// Application Insights when APPLICATIONINSIGHTS_CONNECTION_STRING is configured. Kept separate
/// from ObservabilityExtensions.AddObservability's generic OTLP/console exporter path because
/// Application Insights doesn't accept unauthenticated OTLP - it needs the dedicated Azure Monitor
/// exporter package, which this wraps behind the same opt-in pattern as CorsExtensions.TryAddCors.
/// Absent config leaves today's behavior (no Azure Monitor export) unchanged.
/// </summary>
public static class AzureMonitorExtensions
{
    public static bool TryAddAzureMonitor(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        services.AddOpenTelemetry().UseAzureMonitor(options => options.ConnectionString = connectionString);
        return true;
    }
}
