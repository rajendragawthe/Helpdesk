# Phase 7b: Reopen a Replied Ticket on a Customer Reply

Date: 2026-09-27

## Problem

Today, once an agent sends a reply (`Ticket.Status = Replied`), a further customer email on the
same conversation thread is stored as a `Message` but nothing else happens: the ticket stays
`Replied`, its `Classification` and `DraftReply` are untouched, and the ticket never resurfaces in
the agent queue (`Queue`/`Mine` filters exclude `Replied`). The agent has to notice the new message
some other way. The same gap exists for `InReview`: if a customer sends a second message before an
agent has responded to the first, the stored `DraftReply` still reflects only the first message.

## Scope

When a customer email arrives on an existing ticket's conversation thread (matched by
`ConversationId`, deduplicated by `ExternalMessageId` exactly as today) **and the ticket's status at
that moment is `InReview` or `Replied`**, the ticket reopens and gets a fresh AI draft built from the
whole thread. A follow-up that arrives while a ticket is still `New` (the first-message
classify/draft/review pipeline hasn't produced a result yet, e.g. it's mid-flight or previously
failed) is **not** touched by this phase — it keeps today's behavior (message stored, nothing else).

Out of scope: introducing a new `TicketStatus` value, capping the AI input transcript length,
notifying the agent (email/toast) that a ticket reopened, and reopening through anything other than
a threaded reply on the same `ConversationId`.

## Status transition

No new enum value. `TicketStatus` stays `New` / `InReview` / `Replied`.

- `Replied → InReview`: applied unconditionally as soon as the new customer message is persisted,
  **independent of whether reclassification/redrafting succeed**. This guarantees the ticket
  reappears in the `Queue`/`Mine` views even if the AI calls that follow fail.
- `InReview → InReview`: no-op; the ticket was already visible in the queue.
- `Ticket.AssignedUserId` is never modified by this flow. Because `Replied` tickets always have an
  assignee (replying requires one, per `TicketWorkflowService.SendReplyAsync`) and `InReview` tickets
  keep whichever assignee they already had (possibly none), "keep the assignee" requires no new code
  — it falls out of simply not touching the field.

## Ingestion changes

`EmailIngestionService.ProcessEmailAsync` currently returns `Guid?` (the new ticket's id, or `null`
for a dedup/follow-up). It changes to return a small result so the caller can distinguish three
outcomes for one inbound message:

```csharp
private readonly record struct EmailProcessingResult(Guid TicketId, bool IsNewTicket, bool NeedsReopen);
```

Returned as `EmailProcessingResult?` (`null` for an already-processed/dedup message, same as today).

For an existing ticket (the `ticket is not null` branch, i.e. not a brand-new ticket), before saving:

```csharp
var needsReopen = ticket.Status is TicketStatus.InReview or TicketStatus.Replied;
if (ticket.Status == TicketStatus.Replied)
{
    ticket.Status = TicketStatus.InReview;
}
ticket.UpdatedAt = email.ReceivedAt;
await ticketRepository.UpdateAsync(ticket);
```

`IngestNewEmailsAsync` then branches on the result:

- `IsNewTicket`: existing pipeline, unchanged (classify → draft → review-evaluate, each in its own
  DI scope, gated on the AI services being registered).
- `NeedsReopen` (and not new): new pipeline — reclassify → redraft → review-evaluate, same scope and
  gating pattern as the new-ticket branch.
- Neither (dedup, or a follow-up on a `New` ticket): no further processing, as today.

## Reclassification

New method on the existing service/interface, alongside the current guarded one:

```csharp
// IClassificationService
Task ReclassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default);
```

`ClassificationService.ReclassifyTicketAsync`:

- Loads the ticket (with `Messages` and `Classification`, via the existing tracked `GetByIdAsync`).
- Classifies from the **latest customer message only** (`Messages.Where(IsFromUser).OrderByDescending(ReceivedAt).First()`,
  converted to plain text via the existing `HtmlText.ToPlainText`) — not the full thread. This
  mirrors how the original classification always used the *first* customer message; reclassification
  uses the *latest* one, since that's the new thing needing categorization.
- Calls `IAiService.ClassifyAsync` exactly as `ClassifyTicketAsync` does.
- **Unlike `ClassifyTicketAsync`, this method has no "already classified" guard** — it always
  attempts to classify, then:
  - If `ticket.Classification` already exists (the normal case), updates its `Category`, `Summary`,
    and `Confidence` in place (same row, same `Id`; `CreatedAt` is left unchanged — it still marks the
    original classification time) and calls `ticketRepository.UpdateAsync(ticket)`, relying on EF's
    change tracking of the already-loaded, tracked `Classification` navigation.
  - If `ticket.Classification` is `null` (possible for a ticket that reached `InReview` with
    `ClassificationFailed` because the very first classify attempt failed), creates one via
    `IClassificationRepository.AddAsync`, same as `ClassifyTicketAsync` does today.
- The `IAiService.ClassifyAsync` call is wrapped in its own try/catch: on failure, logs a warning and
  returns **without touching the persisted classification** — the previous (possibly still-correct)
  classification is left in place rather than cleared. This matches the project's existing
  philosophy of never discarding good data on a transient AI failure. No `ReviewReasons` flag is
  raised specifically for "reclassification failed" (only "never classified" raises
  `ClassificationFailed`, which doesn't apply once a classification exists).
- The method-level try/catch (ticket not found, thread has no customer message, unexpected
  exceptions) mirrors `ClassifyTicketAsync`'s existing logging pattern and never throws except for
  `OperationCanceledException` on cancellation.

## Redraft

New method on the existing service/interface, alongside the current guarded one:

```csharp
// IDraftReplyService
Task RedraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default);
```

New helper (`Helpdesk.Application/DraftReply/ThreadTranscript.cs`):

```csharp
internal static class ThreadTranscript
{
    // Chronological, plain-text, one block per message:
    // "[Customer - 2026-09-27 08:00:00Z]\n<plain text body>"
    // "[Agent - 2026-09-27 09:00:00Z]\n<plain text body>"
    // blocks joined with a blank line.
    public static string Build(IEnumerable<Message> messages);
}
```

`DraftReplyService.RedraftReplyAsync`:

- Loads the ticket (with `Messages` and `Classification`).
- Builds the transcript from **all messages in the thread** (customer and agent, chronological) via
  `ThreadTranscript.Build`, reusing `HtmlText.ToPlainText` per message body inside the helper.
- Matches KB articles against `(ticket.Subject, transcript, ticket.Classification?.Category)` via the
  existing `KbMatcher.Match` — no signature change needed there.
- Calls `IAiService.DraftReplyAsync(ticket.Subject, transcript, category, articles, cancellationToken)`
  — no interface change: the "body" parameter simply carries the full transcript instead of a single
  message's text.
- The AI call is wrapped in its own try/catch (mirroring the reclassify method): on failure, logs and
  treats the draft as blank rather than aborting the method.
- Unconditionally (whether the AI call succeeded, returned blank, or failed) persists:
  ```csharp
  ticket.DraftReply = string.IsNullOrWhiteSpace(draft) ? null : draft.Trim();
  ticket.Status = TicketStatus.InReview;
  ticket.UpdatedAt = DateTimeOffset.UtcNow;
  await ticketRepository.UpdateAsync(ticket);
  ```
  This is the key difference from the original `DraftReplyAsync`: on failure it **clears** `DraftReply`
  to `null` instead of leaving it untouched, because here "untouched" would mean the agent sees a
  stale draft addressed to the *previous* customer message. A cleared draft plus the existing
  `ReviewPolicy`-derived `DraftFailed` flag (unchanged, reused as-is) correctly signals "needs manual
  attention" the same way a first-time draft failure does.
- No new max-length handling is added for the transcript passed into the prompt; this matches the
  project's current lack of input-length capping for classify/draft bodies elsewhere. Flagged as a
  known follow-up (see Out of scope), not addressed in this phase.
- Method-level try/catch (ticket not found, no messages at all) mirrors `DraftReplyAsync`'s existing
  logging pattern.

## Review flags

No changes. `IReviewFlagService.EvaluateAsync` already recomputes `ReviewReasons` purely from the
ticket's current `Classification` and `DraftReply` via `ReviewPolicy.Evaluate`, so it is reused
unchanged after reclassify + redraft in the reopen pipeline.

## Orchestration recap

`EmailIngestionService.IngestNewEmailsAsync`, reopen-eligible branch (parallel structure to the
existing new-ticket branch, same AI-optional gating via `GetService<T>()`):

```csharp
if (result.NeedsReopen)
{
    if (scope.ServiceProvider.GetService<IClassificationService>() is { } classifier)
    {
        await classifier.ReclassifyTicketAsync(result.TicketId, cancellationToken);
    }

    using var draftScope = scopeFactory.CreateScope();
    if (draftScope.ServiceProvider.GetService<IDraftReplyService>() is { } drafter)
    {
        await drafter.RedraftReplyAsync(result.TicketId, cancellationToken);
    }

    using var reviewScope = scopeFactory.CreateScope();
    if (reviewScope.ServiceProvider.GetService<IReviewFlagService>() is { } reviewer)
    {
        await reviewer.EvaluateAsync(result.TicketId, cancellationToken);
    }
}
```

Each step keeps its own DI scope for the same failure-isolation reason as the new-ticket branch: a
poisoned change tracker from one step's failure must not affect the next step.

## Testing

- `Helpdesk.Application.Tests` (or wherever `ClassificationService`/`DraftReplyService` are already
  tested): `ReclassifyTicketAsync` — updates an existing `Classification` row in place; creates one
  when missing; leaves the previous classification untouched on an AI failure; no-ops with a warning
  when the ticket has no customer message. `RedraftReplyAsync` — builds a transcript covering the
  full thread (both directions, chronological); clears `DraftReply` to `null` and still sets
  `Status = InReview` on an AI failure or blank result; sets a trimmed draft and `InReview` on
  success.
- `EmailIngestionService` tests: a follow-up on a `Replied` ticket flips it to `InReview` and
  triggers the reopen pipeline; a follow-up on an `InReview` ticket stays `InReview` and still
  triggers the reopen pipeline; a follow-up on a `New` ticket triggers neither pipeline; the
  assignee is unchanged across a reopen.
- E2E (via the `qa-engineer` subagent, extending the existing Playwright suite): a customer reply to
  a `Replied` ticket reopens it, it reappears in the queue with the same assignee, and its draft
  reflects the new message (with AI mocked/stubbed as the existing E2E setup already does for
  classify/draft).

## Manual verification (to repeat after merge, needs the user's secrets and a real mailbox)

1. Complete a full reply per the Phase 7 manual test (ticket ends `Replied`).
2. Send a second email from the same sender, replying on the same thread (so Graph keeps the same
   `ConversationId`).
3. Wait one poll interval, then check:
   ```
   psql -U helpdesk -h localhost -d helpdesk -c 'select "Subject", "Status", "AssignedUserId", left("DraftReply", 200) from "Tickets" order by "UpdatedAt" desc limit 3;'
   ```
   `Status` should be back to `1` (InReview), `AssignedUserId` unchanged from before the follow-up,
   and `DraftReply` should reflect the new message (not the original one).
