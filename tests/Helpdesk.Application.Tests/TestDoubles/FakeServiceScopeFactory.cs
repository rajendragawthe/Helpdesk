using Helpdesk.Application.Classification;
using Helpdesk.Application.DraftReply;
using Helpdesk.Application.Review;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Application.Tests.TestDoubles;

/// <summary>
/// Minimal hand-written fake of IServiceScopeFactory/IServiceScope/IServiceProvider that always
/// resolves the SAME FakeTicketRepository/FakeMessageRepository instances regardless of how many
/// scopes are created. This mirrors production DI shape (a new scope per message) while still
/// letting tests inspect state across all the "scopes" EmailIngestionService creates in one tick.
/// IClassificationService, IDraftReplyService and IReviewFlagService are optional (null = "AI not
/// configured"), matching production where they are only registered when OpenRouter is configured.
/// No DI container or mocking library involved.
/// </summary>
public class FakeServiceScopeFactory(
    ITicketRepository ticketRepository,
    IMessageRepository messageRepository,
    IClassificationService? classificationService = null,
    IDraftReplyService? draftReplyService = null,
    IReviewFlagService? reviewFlagService = null)
    : IServiceScopeFactory
{
    public int ScopesCreated { get; private set; }

    /// <summary>Records (service type, 1-based index of the scope it was resolved from) for AI services.</summary>
    public List<(Type ServiceType, int ScopeIndex)> AiServiceResolutions { get; } = [];

    public IServiceScope CreateScope()
    {
        ScopesCreated++;
        var scopeIndex = ScopesCreated;
        return new FakeServiceScope(new FakeServiceProvider(
            ticketRepository, messageRepository, classificationService, draftReplyService, reviewFlagService,
            serviceType => AiServiceResolutions.Add((serviceType, scopeIndex))));
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
        IClassificationService? classificationService,
        IDraftReplyService? draftReplyService,
        IReviewFlagService? reviewFlagService,
        Action<Type> onAiServiceResolved)
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
                onAiServiceResolved(serviceType);
                return classificationService;
            }

            if (serviceType == typeof(IDraftReplyService))
            {
                onAiServiceResolved(serviceType);
                return draftReplyService;
            }

            if (serviceType == typeof(IReviewFlagService))
            {
                onAiServiceResolved(serviceType);
                return reviewFlagService;
            }

            return null;
        }
    }
}
