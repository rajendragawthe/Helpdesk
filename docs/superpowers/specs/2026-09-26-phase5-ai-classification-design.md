# Phase 5 — AI Classification & Summary (OpenRouter) — Design

Date: 2026-09-26
Status: Written retrospectively, after implementation (as-built). The Phase 5 work went
straight to a plan without a brainstorming/spec step; this document records the design
decisions that were actually made and shipped so the spec trail matches Phase 1 and 4.
Covers: `implementation-plan.md` Phase 5, tasks 31–35
Plan: `docs/superpowers/plans/2026-09-26-phase5-ai-classification.md`

## Goal

Every newly created ticket is automatically classified (category + confidence) and
summarized by an LLM via OpenRouter, and the result is stored in the existing
`Classification` table. Classification never blocks or fails email ingestion.

## Component map

```
Helpdesk.Core/
  Interfaces/IAiService.cs              — ClassifyAsync(subject, body, ct) -> ClassificationResult
  Interfaces/IClassificationRepository.cs — AddAsync(Classification)
  Models/ClassificationResult.cs        — record (Category, Summary, Confidence)
  Models/TicketCategories.cs            — fixed category set + Normalize()

Helpdesk.Application/
  Classification/IClassificationService.cs — ClassifyTicketAsync(ticketId, ct)
  Classification/ClassificationService.cs  — load ticket -> AI -> persist
  Classification/HtmlText.cs               — HTML body -> bounded plain text
  EmailIngestion/EmailIngestionService.cs  — calls the classifier for NEW tickets only
  DependencyInjection.cs                   — registers IClassificationService (opt-in)

Helpdesk.Infrastructure/
  Ai/OpenRouterAiService.cs        — IAiService over HttpClient (chat/completions, JSON mode)
  Ai/OpenRouterOptions.cs          — ApiKey + Model (ToString omits the key)
  Ai/DependencyInjection.cs        — AddOpenRouter(IConfiguration), opt-in
  Repositories/ClassificationRepository.cs
```

No schema change: the `Classifications` table and the one-to-one `Ticket` <-> `Classification`
relationship already existed from Phase 2.

Dependency direction is unchanged: `Api -> Application -> Core`, `Infrastructure -> Core`.

## Runtime flow

1. `EmailIngestionService` creates a **new** ticket + message and marks the email processed
   (unchanged Phase 4 behaviour).
2. Still inside the per-email DI scope, it resolves `IClassificationService` with
   `GetService` (optional) and, if present, calls `ClassifyTicketAsync(ticketId)`.
3. `ClassificationService` loads the ticket (with messages + classification), skips it if it
   is missing, already classified, or has no customer message, takes the earliest customer
   message, converts its HTML body to plain text (`HtmlText`), and calls `IAiService`.
4. The result is stored as a `Classification` (Category, Summary, Confidence, CreatedAt).

Replies appended to an existing conversation never trigger classification, and an existing
`Classification` is never overwritten.

## One call, not two

`implementation-plan.md` task 31 listed `ClassifyAsync`, `SummarizeAsync`, `DraftReplyAsync`.
Decision: a single `ClassifyAsync` returns category, summary and confidence in one LLM call
(cheaper, consistent, and Phase 8's low-confidence escalation needs the confidence on the
same response). `SummarizeAsync` is folded in; `DraftReplyAsync` is deferred to Phase 6 (no
implementation would exist yet).

## Categories

Fixed set (exact strings): `Billing`, `Technical Issue`, `Account Access`,
`Feature Request`, `General Inquiry`, `Other`. Model output is normalized
case-insensitively; anything outside the set becomes `Other`. Confidence is clamped to
`[0, 1]`. Rationale: a closed set bounds the impact of prompt injection and keeps the
classification usable for later routing.

## OpenRouter client

- `POST https://openrouter.ai/api/v1/chat/completions` via a typed `HttpClient`
  (30 s timeout), `Authorization: Bearer <key>` set per request (no secret in default headers).
- Request: configured model, `temperature: 0`, `max_tokens: 300`,
  `response_format: {type: json_object}`. System prompt lists the categories, demands a single
  JSON object (`category`, `summary`, `confidence`) and states that the email is untrusted
  data, never instructions. The email body is wrapped in `<email_body>` delimiters.
- Parsing is defensive (`TryGetProperty`/`ValueKind`): missing summary or non-numeric
  confidence -> `InvalidOperationException`; summary trimmed and capped at 1000 characters.
- Error handling (added after manual testing): OpenRouter can return HTTP 200 with an
  `error` body and no `choices`. The provider's message/code is surfaced in the exception
  (non-2xx also include a body snippet capped at 300 characters; never headers or the key).
  Transient failures — 408/429/5xx, a 200 with a transient error body
  (`code` 408/429/>=500 or `provider_overloaded`), or an `HttpClient` timeout — are retried
  at most twice (500 ms, then 1500 ms backoff). 402 and other 4xx, malformed model output
  and caller cancellation are not retried.

## Configuration

- `OpenRouter:Model` in `appsettings.Development.json` (default `openai/gpt-4o-mini` in code
  if blank); `OpenRouter:ApiKey` only via `dotnet user-secrets` (same `UserSecretsId` as
  Graph). The key is never committed or logged.
- Opt-in, mirroring Graph: if the `OpenRouter` section is absent or
  `OpenRouter:Enabled` is `"false"`, nothing AI-related is registered and the host still
  starts. `AddApplication` and `AddOpenRouter` each duplicate the presence check because
  Application cannot reference Infrastructure. `IClassificationService` must be registered
  only under that condition, otherwise `ValidateOnBuild` fails Development startup.
- An enabled section without an API key throws `OpenRouter:ApiKey is not configured.` at
  startup (same fail-fast behaviour as Graph's client secret). Consequence: `dotnet run` and
  `dotnet ef` need the user-secret or `OpenRouter__Enabled=false`; E2E sets
  `OpenRouter__Enabled=false`.

## Failure semantics

- AI or persistence failure inside `ClassificationService` is caught and logged; the ticket
  stays unclassified and ingestion continues. Only caller-requested cancellation propagates,
  and `EmailIngestionService` lets it propagate too so host shutdown stops the batch.
- The email is already marked processed before classification, so a ticket whose
  classification fails is **never retried automatically** (accepted gap; Phase 8 flags such
  tickets for manual review, Phase 9 adds broader retry). The in-call retry above only
  covers transient provider errors within one attempt sequence.

## Input safety

- Email content is untrusted: system-prompt framing, delimiters, closed category set, capped
  output (`max_tokens`, summary length).
- `HtmlText` caps raw input at 64,000 characters and uses linear scans (no backtracking
  regexes over untrusted HTML; an earlier regex version was quadratic on crafted input and
  could stall the single ingestion worker). Output is truncated to 8000 characters.

## Testing

xUnit with hand-written test doubles (no mocking library), matching the repo:
`TicketCategories`, `HtmlText` (including 50k-repeat hostile inputs), `ClassificationService`
(guards, failure swallowing, cancellation), ingestion hook (new ticket only, optional
service, cancellation propagation), `OpenRouterAiService` (request shape, parsing, clamping,
retry/backoff, error detail, key never in messages), and the opt-in DI gating.

## Manual verification (2026-09-26)

Run against the shared mailbox with a real key. The default paid model returned 402 for every
ticket (no credits): tickets/messages were still created and one error logged per ticket.
With the free model `nvidia/nemotron-3-super-120b-a12b:free` (env override
`OpenRouter__Model`), a "Charged twice" test email was stored as `Billing`, confidence 0.95,
with a one-sentence summary. One earlier ticket failed on an upstream 503 returned as HTTP 200
with an error body, which motivated the error-detail/retry work. Not verified live: a reply on
the same thread not creating a second classification (unit-tested only).

## Explicit decisions

- One `ClassifyAsync` call instead of separate classify/summarize (above).
- Fixed category set with `Other` fallback rather than free-text categories.
- New tickets only; no re-classification on replies or edits.
- Optional (`GetService`) resolution of `IClassificationService` inside the ingestion scope.
- Fail-fast on a missing API key when the section is enabled, consistent with Graph.
- Plain-text preprocessing of the HTML body before it reaches the model.

## Out of scope / follow-ups

- Confidence threshold and escalation flag (Phase 8); retrying unclassified tickets and
  structured logging (Phase 9).
- Reading `Retry-After`; retrying network-level `HttpRequestException`s without a status code.
- Draft replies and the hardcoded KB (Phase 6).
- Optional hardening noted in review: a body containing a literal `</email_body>` can escape
  the delimiter (mitigated by the untrusted-content instruction and closed categories).
