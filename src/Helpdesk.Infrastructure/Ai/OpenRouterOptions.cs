namespace Helpdesk.Infrastructure.Ai;

public record OpenRouterOptions(string ApiKey, string Model)
{
    // Deliberately omits ApiKey so it never leaks via ToString()/string interpolation.
    public override string ToString() => $"OpenRouterOptions {{ Model = {Model} }}";
}
