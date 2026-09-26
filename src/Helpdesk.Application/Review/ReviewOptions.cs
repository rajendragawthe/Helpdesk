using System.Globalization;

namespace Helpdesk.Application.Review;

/// <summary>Settings for review flagging. The threshold is compared with the classification confidence.</summary>
public sealed record ReviewOptions(double ConfidenceThreshold)
{
    public const string ConfigKey = "Review:ConfidenceThreshold";
    public const double DefaultConfidenceThreshold = 0.7;

    /// <summary>
    /// Parses the raw configuration value: missing/blank gives the default; otherwise it must be an
    /// invariant-culture number in [0, 1] (so "0,7" is rejected rather than read as 7). A bad value throws
    /// so a typo fails at startup instead of silently changing which tickets are flagged.
    /// </summary>
    public static ReviewOptions Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ReviewOptions(DefaultConfidenceThreshold);
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            || double.IsNaN(threshold)
            || threshold < 0
            || threshold > 1)
        {
            throw new InvalidOperationException($"{ConfigKey} must be a number between 0 and 1 (was '{value}').");
        }

        return new ReviewOptions(threshold);
    }
}
