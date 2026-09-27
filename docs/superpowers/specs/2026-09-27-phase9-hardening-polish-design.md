# Phase 9 — Hardening & Polish — Design Spec

Implementation plan tasks covered: `implementation-plan.md` Phase 9, items 49–53.

## Context

Phases 0–8 built the full MVP core loop (ingest → classify → draft → agent review/reply → reopen on reply) with per-phase resilience already baked in: `EmailIngestionBackgroundService` catches and logs per-tick failures, `EmailIngestionService` isolates each email in its own DI scope/catch block, `OpenRouterAiService` retries transient failures with backoff, and `TicketWorkflowService` maps failure modes to distinct HTTP statuses. What's missing before calling the MVP "hardened":

1. Logging is the ASP.NET Core default console logger — unstructured, no correlation across a request/ticket, no tracing.
2. No CORS configuration at all (only works today because Vite proxies `/api/*` same-origin in dev; a separately-hosted frontend, or an already-authenticated cross-origin call, would fail).
3. Frontend crashes are invisible — nothing captures them.
4. Input validation and the AI/Graph error-handling guarantees from earlier phases haven't been reviewed together as a checklist.
5. No smoke test has exercised 5–10 varied real-style emails end-to-end in one pass.

## Goals

- Structured, correlated backend logging (Serilog) and basic distributed tracing/metrics (OpenTelemetry), both opt-in for their external destinations so the app still starts cleanly with zero config, matching the existing `GraphApi`/`OpenRouter` opt-in pattern.
- Frontend crash capture without adding a new external service or self-hosted dashboard — an open-source, in-house equivalent to Sentry, reusing the backend's own logging pipeline as the sink.
- CORS support via a configurable origin allow-list, so a non-proxied frontend deployment (Phase 10) needs no code change, only config.
- A verification pass confirming the input-validation and error-handling guarantees already documented for Phases 4–8 still hold, with any real gap fixed.
- A documented manual smoke-test procedure for the user to run against a real mailbox.

## Non-goals

- No new persistence for client errors (no `ClientError` table) — this is a log stream, not a feature; can graduate later if volume justifies it.
- No self-hosted error-tracking dashboard (GlitchTip/Highlight.io) — too much new infra for a hardening phase with no deployment story yet (that's Phase 10).
- No rewrite of existing retry/backoff logic in `OpenRouterAiService`/`EmailIngestionService` — only fix a genuine gap if the review finds one.
- No change to `TicketsController`'s hand-rolled `filter` query-param validation (not a DTO; FluentValidation doesn't fit a bare query string).

## Design

### 1. Backend structured logging (Serilog)

- Add `Serilog.AspNetCore` to `Helpdesk.Api.csproj`.
- `Program.cs`: replace the default logging provider with `builder.Host.UseSerilog((context, services, config) => config.ReadFrom.Configuration(context.Configuration).Enrich.FromLogContext())`, console sink with a structured/JSON formatter (`Serilog.Formatting.Compact.CompactJsonFormatter` — add `Serilog.Formatting.Compact` package). Minimum levels stay driven by the existing `Logging:LogLevel` section (Serilog's `ReadFrom.Configuration` understands the standard ASP.NET Core `Logging` section shape, so `appsettings.json`/`appsettings.Development.json` need no restructuring).
- Add correlation via `Serilog.Context.LogContext.PushProperty`: `EmailIngestionService` pushes `ExternalMessageId`/`ConversationId` once per email at the top of its per-email loop iteration (wrapping the existing try/catch body in a `using (LogContext.PushProperty(...))` block) so every log line emitted while processing that email — including from the classifier/drafter/reviewer it calls into — carries it without changing any of those services' method signatures. `TicketWorkflowService` pushes `TicketId` the same way around claim/release/reply.
- No behavior change to what is logged today, only how (structured fields instead of an interpolated string) and the added correlation properties.

### 2. OpenTelemetry (traces + metrics)

- New optional `Otel` config section: `Otel:Enabled` (bool, default true when the section exists) and `Otel:OtlpEndpoint` (string, optional).
- `Program.cs`: `builder.Services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql()...).WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()...)`. Exporter selection:
  - No `Otel` section, or `Otel:Enabled=false` → still instrument (harmless, low overhead) but export to the console exporter (`AddConsoleExporter()`), so a fresh clone gets *something* visible without any config, same spirit as the rest of the app rather than truly "off". This differs slightly from the strict Graph/OpenRouter "skip registration entirely" pattern, deliberately — there's no failure mode from having OTel instrumented with no consumer, unlike Graph/OpenRouter needing real credentials to avoid throwing at first use.
  - `Otel:OtlpEndpoint` set → add `AddOtlpExporter(o => o.Endpoint = new Uri(config["Otel:OtlpEndpoint"]))` instead of/alongside the console exporter.
- New packages on `Helpdesk.Api.csproj`: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Exporter.Console`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`. EF Core/Npgsql tracing via `Npgsql.OpenTelemetry` on `Helpdesk.Infrastructure.csproj` (registered from the `AddInfrastructure` DI extension, keeping the layering rule that `Api` doesn't reach into `Infrastructure` internals directly — `AddInfrastructure` can itself call `.AddNpgsql()` onto the already-built tracing provider builder if it's passed in, or more simply, `Api`'s `Program.cs` adds the Npgsql instrumentation since it already composes `AddOpenTelemetry()` there; no interface change needed either way — implementation detail resolved in the plan, not blocking here).

### 3. Frontend error capture

- Backend: `ClientErrorsController` (`api/client-errors`, `[AllowAnonymous]`, `[HttpPost]`) taking a new `ClientErrorRequest(string Message, string? Stack, string Url, string UserAgent)` DTO. `ClientErrorRequestValidator` (FluentValidation, alongside the existing two validators): `Message` required, max 2000 chars; `Stack` optional, max 8000 chars; `Url`/`UserAgent` required, max 500 chars each. Controller logs at `Warning` via `ILogger<ClientErrorsController>` with all four fields as structured properties, returns `202 Accepted`. No auth required (a crash can happen before/without a token) and no DB write.
- Frontend (`client/helpdesk-web/src`):
  - `lib/clientErrorReporter.ts`: `reportClientError({ message, stack, url, userAgent })` — `fetch('/api/client-errors', { method: 'POST', ... })`, wrapped in try/catch that swallows its own failure (a reporting failure must never throw again).
  - `components/ErrorBoundary.tsx`: a class component implementing `componentDidCatch`, calls `reportClientError`, renders a minimal fallback ("Something went wrong — please reload") instead of a blank screen.
  - `main.tsx`: wrap `<App />` in `<ErrorBoundary>`; register `window.addEventListener('error', ...)` and `window.addEventListener('unhandledrejection', ...)` once at startup, both calling `reportClientError` for crashes `ErrorBoundary` can't see (event handlers, promises, code outside the React tree).

### 4. CORS

- New config section `CorsOrigins: string[]`, `appsettings.Development.json` gets `["http://localhost:5173"]`; `appsettings.json` (production defaults) leaves it empty/absent — Phase 10 sets the real origin via environment-specific config, no code change.
- `Program.cs`: if `builder.Configuration.GetSection("CorsOrigins").Get<string[]>()` is non-empty, `builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()))` and `app.UseCors()` right after `UseHttpsRedirection()`/before `UseAuthentication()`. No `AllowCredentials()` — the API uses bearer-token auth (MSAL), not cookies, so credentialed CORS isn't needed and skipping it keeps the policy simpler. Empty/absent section → CORS middleware isn't registered at all (today's behavior, unchanged).

### 5. Validation & error-handling review

A checklist pass against the existing code (this spec's item most likely to produce zero diff):

- Every request DTO with a body has a FluentValidation validator: `CreateUserRequest` ✅, `ReplyRequest` ✅, new `ClientErrorRequest` (added above).
- Re-confirm from reading the code (already done during brainstorming): `EmailIngestionBackgroundService` catches per-tick; `EmailIngestionService` isolates each email in its own scope/catch and is idempotent on `ExternalMessageId` (a `MarkAsProcessedAsync` failure after save just gets safely re-seen next tick); `OpenRouterAiService` retries 408/429/5xx up to twice with backoff and doesn't retry other 4xx; `TicketWorkflowService` maps every failure mode to a distinct `TicketOutcome`. If the implementing subagent finds a real gap while working through this list, it fixes it and calls it out in its task report — this item is verify-and-patch, not rewrite.

### 6. Manual smoke test (item 53)

Documented procedure (added to the plan and, after the user runs it, to `CLAUDE.md`'s Phase 9 status the same way Phases 5–8 recorded their manual verification): send 5–10 varied sample emails to the monitored mailbox —
1. A clear, unambiguous billing question (expect confident classification, grounded draft, no review flags).
2. A one-line vague message like "hi" (expect `LowConfidence` review flag, holding draft).
3. An HTML email with a link, an image, and inline formatting (expect correct rendering per the Phase 7 email-viewer work — no regression expected, just re-confirming).
4. A reply on an existing thread after the first ticket was replied to (expect reopen to `InReview`, redraft from full thread transcript).
5. One email sent while the API process is briefly stopped, then started again (expect the poller to pick it up on its next tick — proves no email is lost across a restart).

Then verify via `psql` (query pattern already documented in `CLAUDE.md` for prior phases) that subjects/categories/confidences/statuses/review reasons match expectations, and check the console log output is structured JSON with the expected correlation properties present.

## Testing

- `ClientErrorRequestValidator`: valid request passes; empty `Message`, over-length `Message`/`Stack`/`Url`/`UserAgent` each fail (Api.Tests, following the existing validator test pattern).
- `ClientErrorsController`: posts a valid body → `202`; confirms no auth is required (no `[Authorize]` and a test without a bearer token still gets `202`).
- Host starts with no `Otel` section (console exporter, no crash) and with `Otel:OtlpEndpoint` set to a syntactically valid URI (no crash) — mirrors the existing "host starts with AI/Graph off" test pattern.
- Host starts with no `CorsOrigins` section (unchanged today's behavior) and with it set (CORS middleware registered, an OPTIONS preflight from the configured origin succeeds).
- No new tests for Serilog output formatting itself or for OpenTelemetry span content — verified by manual inspection during the smoke test, not asserted in xUnit.

## Files touched (indicative, finalized in the plan)

- `src/Helpdesk.Api/Helpdesk.Api.csproj` — new packages (Serilog, OTel).
- `src/Helpdesk.Api/Program.cs` — Serilog, OTel, CORS wiring.
- `src/Helpdesk.Api/appsettings.json`, `appsettings.Development.json` — `Otel`, `CorsOrigins` sections.
- `src/Helpdesk.Api/Controllers/ClientErrorsController.cs` — new.
- `src/Helpdesk.Api/Validators/ClientErrorRequestValidator.cs` — new.
- `src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs` — `LogContext.PushProperty` around the per-email loop body.
- `src/Helpdesk.Application/Tickets/TicketWorkflowService.cs` — `LogContext.PushProperty` around claim/release/reply.
- `src/Helpdesk.Infrastructure/Helpdesk.Infrastructure.csproj` — `Npgsql.OpenTelemetry`.
- `client/helpdesk-web/src/lib/clientErrorReporter.ts` — new.
- `client/helpdesk-web/src/components/ErrorBoundary.tsx` — new.
- `client/helpdesk-web/src/main.tsx` — wrap with `ErrorBoundary`, register global handlers.
- `tests/Helpdesk.Api.Tests/...` — new validator/controller tests, opt-in-startup tests.
- `CLAUDE.md` — Phase 9 section documenting what was built and the smoke-test results once the user runs it.
