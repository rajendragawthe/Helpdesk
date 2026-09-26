namespace Helpdesk.Core.Enums;

/// <summary>
/// Why a ticket was tagged for manual review. Stored as an int and exposed by the API: the numeric
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
