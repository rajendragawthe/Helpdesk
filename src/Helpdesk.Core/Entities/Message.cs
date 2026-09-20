namespace Helpdesk.Core.Entities;

public class Message
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public required string Sender { get; set; }
    public required string Body { get; set; }
    public bool IsFromUser { get; set; }
    public string? ExternalMessageId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    public Ticket? Ticket { get; set; }
}
