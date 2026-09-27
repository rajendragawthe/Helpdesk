using Helpdesk.Application.Classification;
using Helpdesk.Core.Entities;

namespace Helpdesk.Application.DraftReply;

/// <summary>
/// Builds a plain-text, chronological transcript of a ticket's whole message thread (both
/// directions) for redrafting when a customer reply reopens the ticket. The original single-message
/// draft never uses this — it only ever sees the first customer message.
/// </summary>
internal static class ThreadTranscript
{
    public static string Build(IEnumerable<Message> messages) =>
        string.Join(
            "\n\n",
            messages
                .OrderBy(m => m.ReceivedAt)
                .Select(m => $"[{(m.IsFromUser ? "Customer" : "Agent")} - {m.ReceivedAt:u}]\n{HtmlText.ToPlainText(m.Body)}"));
}
