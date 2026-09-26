namespace Helpdesk.Core.Enums;

/// <summary>Which tickets the agent queue lists. Queue = open tickets that are unassigned or mine.</summary>
public enum TicketFilter
{
    Queue,
    Mine,
    All,
}
