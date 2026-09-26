using Helpdesk.Core.Enums;

namespace Helpdesk.Application.Tickets;

/// <summary>The authenticated agent making a request.</summary>
public record TicketCaller(Guid UserId, string Email, bool IsAdmin);

public record TicketAssignee(Guid Id, string DisplayName);

public record TicketListItem(
    Guid Id,
    string Subject,
    string RequesterEmail,
    TicketStatus Status,
    string? Category,
    string? Summary,
    double? Confidence,
    TicketAssignee? Assignee,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool HasDraft,
    int ReviewReasons,
    bool NeedsReview);

public record TicketMessageDto(Guid Id, string Sender, bool IsFromUser, DateTimeOffset ReceivedAt, string BodyText);

public record TicketDetail(
    Guid Id,
    string Subject,
    string RequesterEmail,
    TicketStatus Status,
    string? Category,
    string? Summary,
    double? Confidence,
    TicketAssignee? Assignee,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool HasDraft,
    int ReviewReasons,
    bool NeedsReview,
    string? DraftReply,
    IReadOnlyList<TicketMessageDto> Messages);

public enum TicketOutcome
{
    Success,
    NotFound,
    Forbidden,
    Conflict,
    Invalid,
    SendFailed,
    SentButNotSaved,
}

public record TicketResult<T>(TicketOutcome Outcome, T? Value = default, string? Message = null)
{
    public bool IsSuccess => Outcome == TicketOutcome.Success;

    public static TicketResult<T> Ok(T value) => new(TicketOutcome.Success, value);

    public static TicketResult<T> Fail(TicketOutcome outcome, string message) => new(outcome, default, message);
}
