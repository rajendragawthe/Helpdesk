using Helpdesk.Core.Enums;

namespace Helpdesk.Core.Entities;

public class Ticket
{
    public Guid Id { get; set; }
    public required string Subject { get; set; }
    public required string RequesterEmail { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.New;
    public ReviewReasons ReviewReasons { get; set; } = ReviewReasons.None;
    public string? ConversationId { get; set; }
    public string? DraftReply { get; set; }
    public Guid? AssignedUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public User? AssignedUser { get; set; }
    public Classification? Classification { get; set; }
    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
