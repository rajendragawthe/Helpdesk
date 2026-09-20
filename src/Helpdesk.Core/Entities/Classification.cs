namespace Helpdesk.Core.Entities;

public class Classification
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public required string Category { get; set; }
    public required string Summary { get; set; }
    public double Confidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Ticket? Ticket { get; set; }
}
