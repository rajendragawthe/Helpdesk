using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.EmailIngestion;

public class EmailIngestionBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<EmailIngestionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = int.TryParse(configuration["GraphApi:PollingIntervalSeconds"], out var seconds)
            ? seconds
            : 60;

        // Guard against a misconfigured (zero or negative) polling interval crashing the
        // host via PeriodicTimer's constructor before the tick loop's try/catch even runs.
        intervalSeconds = Math.Max(1, intervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var ingestionService = scope.ServiceProvider.GetRequiredService<EmailIngestionService>();

                await ingestionService.IngestNewEmailsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email ingestion tick failed unexpectedly.");
            }
        }
        while (!stoppingToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(stoppingToken));
    }
}
