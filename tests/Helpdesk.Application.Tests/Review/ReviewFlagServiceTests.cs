using Helpdesk.Application.Review;
using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using ClassificationEntity = Helpdesk.Core.Entities.Classification;

namespace Helpdesk.Application.Tests.Review;

public class ReviewFlagServiceTests
{
    private readonly FakeTicketRepository _tickets = new();

    private ReviewFlagService CreateService(double threshold = 0.7) =>
        new(_tickets, new ReviewOptions(threshold), NullLogger<ReviewFlagService>.Instance);

    private Ticket AddTicket(double? confidence, string category = "Billing", string? draft = "A draft.")
    {
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Charged twice",
            RequesterEmail = "a@example.com",
            Status = TicketStatus.InReview,
            DraftReply = draft,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        if (confidence is { } c)
        {
            ticket.Classification = new ClassificationEntity
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                Category = category,
                Summary = "s",
                Confidence = c,
            };
        }

        _tickets.Tickets.Add(ticket);
        return ticket;
    }

    [Fact]
    public async Task EvaluateAsync_LowConfidence_SetsFlagAndUpdatesOnlyTheFlag()
    {
        var ticket = AddTicket(0.4);
        var classification = ticket.Classification;

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(ReviewReasons.LowConfidence, ticket.ReviewReasons);
        Assert.Equal(1, _tickets.UpdateCount);
        Assert.True(ticket.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(TicketStatus.InReview, ticket.Status);
        Assert.Equal("A draft.", ticket.DraftReply);
        Assert.Same(classification, ticket.Classification);
    }

    [Fact]
    public async Task EvaluateAsync_UsesTheConfiguredThreshold()
    {
        var ticket = AddTicket(0.8);

        await CreateService(threshold: 0.9).EvaluateAsync(ticket.Id);

        Assert.Equal(ReviewReasons.LowConfidence, ticket.ReviewReasons);
    }

    [Fact]
    public async Task EvaluateAsync_CleanTicket_StaysNoneAndIsNotUpdated()
    {
        var ticket = AddTicket(0.95);

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(ReviewReasons.None, ticket.ReviewReasons);
        Assert.Equal(0, _tickets.UpdateCount);
    }

    [Fact]
    public async Task EvaluateAsync_FlagsAlreadyUpToDate_DoesNotUpdateAgain()
    {
        var ticket = AddTicket(0.4);
        ticket.ReviewReasons = ReviewReasons.LowConfidence;

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(0, _tickets.UpdateCount);
    }

    [Fact]
    public async Task EvaluateAsync_StaleFlagsAreClearedWhenNoLongerWarranted()
    {
        var ticket = AddTicket(0.95);
        ticket.ReviewReasons = ReviewReasons.DraftFailed;

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(ReviewReasons.None, ticket.ReviewReasons);
        Assert.Equal(1, _tickets.UpdateCount);
    }

    [Fact]
    public async Task EvaluateAsync_NoClassificationAndNoDraft_SetsBothFailureFlags()
    {
        var ticket = AddTicket(null, draft: null);

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(ReviewReasons.ClassificationFailed | ReviewReasons.DraftFailed, ticket.ReviewReasons);
    }

    [Fact]
    public async Task EvaluateAsync_TicketNotFound_DoesNothing()
    {
        await CreateService().EvaluateAsync(Guid.NewGuid());

        Assert.Equal(0, _tickets.UpdateCount);
    }

    [Fact]
    public async Task EvaluateAsync_PersistenceThrows_Swallows()
    {
        var ticket = AddTicket(0.4);
        _tickets.UpdateException = new InvalidOperationException("db down");

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(1, _tickets.UpdateCount);
    }

    [Fact]
    public async Task EvaluateAsync_CancelledWhileTokenCancelled_Propagates()
    {
        var ticket = AddTicket(0.4);
        _tickets.UpdateException = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateService().EvaluateAsync(ticket.Id, cts.Token));
    }

    [Fact]
    public async Task EvaluateAsync_TimeoutStyleCancellationWithLiveToken_IsSwallowed()
    {
        var ticket = AddTicket(0.4);
        _tickets.UpdateException = new OperationCanceledException();

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(1, _tickets.UpdateCount);
    }
}
