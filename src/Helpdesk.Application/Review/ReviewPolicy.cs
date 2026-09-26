using Helpdesk.Core.Enums;
using Helpdesk.Core.Models;
using ClassificationEntity = Helpdesk.Core.Entities.Classification;

namespace Helpdesk.Application.Review;

/// <summary>Derives why a ticket needs manual review from what was actually persisted for it.</summary>
public static class ReviewPolicy
{
    public static ReviewReasons Evaluate(ClassificationEntity? classification, string? draftReply, double threshold)
    {
        var reasons = ReviewReasons.None;

        if (classification is null)
        {
            reasons |= ReviewReasons.ClassificationFailed;
        }
        else
        {
            if (classification.Confidence < threshold)
            {
                reasons |= ReviewReasons.LowConfidence;
            }

            if (string.Equals(classification.Category, TicketCategories.Other, StringComparison.OrdinalIgnoreCase))
            {
                reasons |= ReviewReasons.CategoryOther;
            }
        }

        if (string.IsNullOrWhiteSpace(draftReply))
        {
            reasons |= ReviewReasons.DraftFailed;
        }

        return reasons;
    }
}
