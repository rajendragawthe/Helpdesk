using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Helpdesk.Api.Observability;

/// <summary>
/// Instruments ASP.NET Core/HttpClient/Npgsql for tracing and metrics. Unlike GraphApi/OpenRouter,
/// there is no failure mode from instrumenting with nowhere to send data, so this is always
/// registered: it exports to the console when no "Otel:OtlpEndpoint" is configured, and adds an
/// OTLP exporter on top when one is. A fresh clone therefore gets visible traces/metrics with zero
/// config, and can be pointed at a real collector later with only a config change.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpEndpoint = configuration["Otel:OtlpEndpoint"];

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Helpdesk.Api"));

        otel.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation();
            tracing.AddHttpClientInstrumentation();

            // Npgsql.OpenTelemetry's AddNpgsql() lives in the "Npgsql" namespace and is called
            // fully qualified here because the OpenTelemetry hosting TracerProviderBuilder also
            // implements IServiceCollection, which makes the unqualified call ambiguous with (and,
            // without qualification, resolve incorrectly to) EF Core's own
            // NpgsqlServiceCollectionExtensions.AddNpgsql<TContext> from
            // Npgsql.EntityFrameworkCore.PostgreSQL (see task 4 of the Phase 9 hardening plan).
            Npgsql.TracerProviderBuilderExtensions.AddNpgsql(tracing);

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            }
            else
            {
                tracing.AddConsoleExporter();
            }
        });

        otel.WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation();
            metrics.AddHttpClientInstrumentation();

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            }
            else
            {
                metrics.AddConsoleExporter();
            }
        });

        return services;
    }
}
