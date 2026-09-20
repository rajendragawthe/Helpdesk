using Helpdesk.Core.Enums;

namespace Helpdesk.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public Role Role { get; set; }
    public string? ExternalObjectId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
