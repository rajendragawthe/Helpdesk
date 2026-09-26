# Phase 8 — Low-Confidence Escalation (Review Flags) — Design

Date: 2026-09-26
Status: Approved (design in brainstorming; written spec reviewed)
Covers: `implementation-plan.md` Phase 8, tasks 46-48 (46-47 here; 48, the queue badge, is a frontend follow-up after Phase 7 merges)
Plan: `docs/superpowers/plans/2026-09-26-phase8-low-confidence-escalation.md`Runs in parallel with Phase 7 (agent queue + reply UI); see "Contract with Phase 7".

## Goal

Tickets the AI is unsure about, or could not fully process, are tagged for manual review so agents
can see them at a glance. The tag is a simple flag set stored on the ticket, with the reasons it was
set. No separate queue infrastructure, and flagging never blocks or changes the normal flow: a flagged
ticket still gets drafted and still moves `New -> InReview`.

## Decisions (from brainstorming)

1. **Triggers (all four):** low classification confidence, classification failed (no classification),
   category `Other`, draft failed (no draft).
2. **Storage:** a `[Flags]` enum `ReviewReasons` on `Ticket` (int column). "Needs review" is derived
   as `ReviewReasons != None`; there is no separate bool.
3. **Threshold:** configurable `Review:ConfidenceThreshold`, default `0.7`. A confidence `<` threshold
   flags; equal to the threshold does not.
4. **Orchestration: a separate `ReviewFlagService`** that derives flags from the persisted state
   after classification and drafting, instead of setting flags inside `ClassificationService` /
   `DraftReplyService` (whose swallow-all failure paths would otherwise have to grow flag logic).

## Component map

```
Helpdesk.Core/
  Enums/ReviewReasons.cs            — [Flags] None=0, LowConfidence=1, ClassificationFailed=2,
                                      CategoryOther=4, DraftFailed=8
  Entities/Ticket.cs                — + ReviewReasons ReviewReasons { get; set; } = ReviewReasons.None

Helpdesk.Application/Review/
  ReviewOptions.cs                  — record (double ConfidenceThreshold)
  ReviewPolicy.cs                   — pure: Evaluate(Classification?, string? draftReply, double threshold)
  IReviewFlagService.cs, ReviewFlagService.cs — load ticket -> policy -> persist if changed
  DependencyInjection.cs (mod)      — bind ReviewOptions, register IReviewFlagService (OpenRouter gate)
  EmailIngestion/EmailIngestionService.cs (mod) — call reviewer after drafter, new tickets only, own scope

Helpdesk.Infrastructure/
  Data/HelpdeskDbContext.cs (mod)   — ReviewReasons int column, default 0
  Data/Migrations/*_AddTicketReviewReasons — new migration + snapshot update
```

Core keeps no EF/Npgsql/Graph dependency; Application depends only on Core.

## Behaviour

### ReviewPolicy.Evaluate(classification, draftReply, threshold) -> ReviewReasons
- `classification is null` -> `ClassificationFailed` (no confidence/category checks then).
- else: `Confidence < threshold` -> `LowConfidence`; `Category == "Other"` (`TicketCategories.Other`,
  case-insensitive) -> `CategoryOther`.
- `string.IsNullOrWhiteSpace(draftReply)` -> `DraftFailed`.
- Reasons combine with bitwise OR; result `None` when nothing applies.

### ReviewFlagService.EvaluateAsync(ticketId, ct)
1. Load ticket via `ITicketRepository.GetByIdAsync` (includes Classification); return if not found.
2. Compute reasons with `ReviewPolicy` using `ReviewOptions.ConfidenceThreshold`.
3. If equal to the current `ReviewReasons`, do nothing (idempotent). Otherwise set it, set
   `UpdatedAt`, `UpdateAsync`. Ticket `Status`, `DraftReply` and `Classification` are never modified.
4. Cancellation (token cancelled) propagates; any other exception is logged and swallowed (never fails
   ingestion; the ticket keeps `None`).

### Options and gating
- `Review:ConfidenceThreshold` parsed as a double (invariant culture); absent -> `0.7`; present but
  not a number in `[0, 1]` -> `InvalidOperationException` at registration (fail fast on a typo).
- `IReviewFlagService` registered under the existing `IsOpenRouterConfigured` gate: with AI disabled
  nothing is classified or drafted, so nothing is flagged (otherwise every ticket would be
  `ClassificationFailed`). Host starts unchanged without OpenRouter.

### Ingestion wiring
After the drafter, for NEW tickets only, run the reviewer in its own DI scope
(`scopeFactory.CreateScope()`, optional `GetService<IReviewFlagService>()`), the same isolation
pattern the drafter uses, so a failed earlier save cannot poison its change tracker. Order:
classify -> draft -> review.

### Data
Migration `AddTicketReviewReasons`: `ReviewReasons integer NOT NULL DEFAULT 0`. Existing tickets
become `None` (no retroactive flagging). Applied to dev with `dotnet ef database update`.

## Contract with Phase 7 (parallel work)

- Field: `Ticket.ReviewReasons` (`Helpdesk.Core.Enums.ReviewReasons`, `[Flags]`, stored as int).
- Phase 7's queue/detail DTOs expose `reviewReasons` (the flags) and `needsReview`
  (`reviewReasons != None`). Phase 7 must not add its own migration for this column; Phase 8 owns it.
- Merge order: Phase 8 backend first; Phase 7 rebases onto it; the queue badge (task 48, incl. the DTO
  field if Phase 7 merged without it) lands as a short follow-up after both.
- Agent clearing/dismissing a flag is out of scope for both phases unless Phase 7's spec adds it.

## Error handling

| Failure | Result |
|---|---|
| OpenRouter not configured | No review service; host starts; nothing flagged |
| Invalid `Review:ConfidenceThreshold` | Fails fast at startup with a clear message |
| Review evaluation/persistence error | Logged; ticket keeps `None`; ingestion unaffected |
| Classification/draft failed | Flags `ClassificationFailed` / `DraftFailed`; ticket otherwise unchanged |

## Testing

- `ReviewPolicy`: no classification; confidence below/equal/above threshold (boundary); category
  `Other` (and case variants); blank/null draft; every combination of reasons; `None` case.
- `ReviewFlagService`: sets flags and updates; unchanged flags -> no update (idempotent); ticket not
  found; exceptions swallowed; cancellation propagates; status/draft/classification untouched.
- Options: default 0.7; valid override; invalid values (`abc`, `-0.1`, `1.5`) throw.
- Ingestion: reviewer runs after the drafter, in its own scope, for new tickets only; skipped when not
  registered; a reviewer exception does not stop the batch or un-mark the message.
- EF/migration: model snapshot builds; `dotnet ef database update` applies cleanly on the dev DB.
- Manual (user's hands: secrets, sending mail): send a deliberately vague email and a clear billing
  email; `psql` shows `ReviewReasons` non-zero for the vague one and `0` for the clear one.

## Out of scope

Queue/detail API, badge and any UI (Phase 7 and the task 48 follow-up); agent clearing of the flag;
re-evaluating flags after an agent edits/sends; retrying failed classification or drafting; structured
logging (Phase 9); backfilling flags for existing tickets.
