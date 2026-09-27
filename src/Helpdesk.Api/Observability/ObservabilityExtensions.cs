using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Helpdesk.Api.Observability;

/// <summary>
/// Instruments ASP.NET Core/HttpClient/Npgsql for tracing and metrics. Unlike GraphApi/OpenRouter,
/// there is no failure mode from instrumenting with nowhere to send data, so this is always
/// registered. Exactly one exporter (or none) is attached per signal: an OTLP exporter when
/// "Otel:OtlpEndpoint" is configured, otherwise a console exporter only when
/// "Otel:ConsoleExporter" is explicitly set to true, otherwise no exporter at all (spans/metrics
/// are still produced by the instrumentation above but go nowhere) - the console exporter is
/// opt-in, not on by default, because it writes plain-text span/metric dumps to the same stdout
/// stream as Serilog's structured JSON log lines and would defeat the "every console line is a
/// JSON object" contract described in CLAUDE.md. A fresh clone therefore starts with zero console
/// noise and can be pointed at a real collector, or have console output turned on for local
/// debugging, with only a config change.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpEndpoint = configuration["Otel:OtlpEndpoint"];
        var consoleExporterEnabled = configuration.GetValue<bool>("Otel:ConsoleExporter");

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
            else if (consoleExporterEnabled)
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
            else if (consoleExporterEnabled)
            {
                metrics.AddConsoleExporter();
            }
        });

        return services;
    }
}
