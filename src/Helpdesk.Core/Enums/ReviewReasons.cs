namespace Helpdesk.Core.Enums;

/// <summary>
/// Why a ticket was tagged for manual review. Stored as an int; API DTOs must expose it as an int
/// (<c>(int)</c>) because the global JSON string-enum converter would otherwise emit strings. The numeric
/// values are a contract, so never renumber them. A ticket "needs review" when this is not None.
/// </summary>
[Flags]
public enum ReviewReasons
{
    None = 0,
    LowConfidence = 1,
    ClassificationFailed = 2,
    CategoryOther = 4,
    DraftFailed = 8,
}
