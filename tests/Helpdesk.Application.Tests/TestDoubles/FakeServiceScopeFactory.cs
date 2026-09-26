using Helpdesk.Application.Classification;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Application.Tests.TestDoubles;

/// <summary>
/// Minimal hand-written fake of IServiceScopeFactory/IServiceScope/IServiceProvider that always
/// resolves the SAME FakeTicketRepository/FakeMessageRepository instances regardless of how many
/// scopes are created. This mirrors production DI shape (a new scope per message) while still
/// letting tests inspect state across all the "scopes" EmailIngestionService creates in one tick.
/// IClassificationService is optional (null = "AI not configured"), matching production where it is
/// only registered when OpenRouter is configured.
/// No DI container or mocking library involved.
/// </summary>
public class FakeServiceScopeFactory(
    ITicketRepository ticketRepository,
    IMessageRepository messageRepository,
    IClassificationService? classificationService = null)
    : IServiceScopeFactory
{
    public int ScopesCreated { get; private set; }

    public IServiceScope CreateScope()
    {
        ScopesCreated++;
        return new FakeServiceScope(new FakeServiceProvider(ticketRepository, messageRepository, classificationService));
    }

    private sealed class FakeServiceScope(IServiceProvider serviceProvider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public void Dispose()
        {
        }
    }

    private sealed class FakeServiceProvider(
        ITicketRepository ticketRepository,
        IMessageRepository messageRepository,
        IClassificationService? classificationService)
        : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ITicketRepository))
            {
                return ticketRepository;
            }

            if (serviceType == typeof(IMessageRepository))
            {
                return messageRepository;
            }

            if (serviceType == typeof(IClassificationService))
            {
                return classificationService;
            }

            return null;
        }
    }
}
