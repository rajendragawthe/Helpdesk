# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

AI-assisted support ticket system (MVP stage). Full context lives in three docs at the repo root — read them before making scope decisions:
- `project-scope.md` — problem, full feature vision, and the MVP scope (core loop: ingest email → AI classifies/summarizes → AI drafts reply from a hardcoded KB → agent reviews/edits/sends)
- `tech-stack.md` — stack rationale and intended solution/project layout
- `implementation-plan.md` — phased task breakdown (Phase 0 scaffolding is done; work proceeds phase by phase)

## Development workflow (required)

All implementation work — every new phase, feature, or non-trivial fix — follows the superpowers flow, in this order, without waiting to be asked:
1. `superpowers:brainstorming` — produces the design spec in `docs/superpowers/specs/YYYY-MM-DD-<topic>-design.md` (Phases 1 and 4 have one). Do not skip this step; a plan without a spec is incomplete.
2. `superpowers:writing-plans` — saves the task plan to `docs/superpowers/plans/YYYY-MM-DD-<topic>.md`, with the spec linked in its header.
3. `superpowers:subagent-driven-development` — execute the plan with a fresh subagent per task, a review after each task, and one whole-branch review at the end. Do not default to inline execution.
4. `superpowers:finishing-a-development-branch` — verify tests, then let the user choose merge / PR / keep.

Work happens on a branch or git worktree, never directly on `main`. Do not merge or push without being asked. Never put secrets (API keys, client secrets) in the chat, repo files or memory — the user sets them with `dotnet user-secrets`.

## Commands

### Database
Uses a local PostgreSQL instance (not Docker) on `localhost:5432`. One-time setup, with `psql`/`createdb`/`createuser` on PATH:
```
createuser -s helpdesk
createdb -O helpdesk helpdesk
```
Set the `helpdesk` role's password to `helpdesk` (e.g. `psql -c "ALTER ROLE helpdesk WITH PASSWORD 'helpdesk';"`) to match `appsettings.Development.json`'s connection string.

### Backend (`src/Helpdesk.Api`)
```
cd src/Helpdesk.Api
dotnet run
```
Runs at `http://localhost:5080`. Health check: `GET /api/health`.

Build/restore the whole solution from repo root:
```
dotnet build Helpdesk.slnx
```
Note: this repo uses the newer `.slnx` solution format, not `.sln`.

EF Core migrations:
```
dotnet ef migrations add <Name> --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Api --output-dir Data/Migrations
dotnet ef database update --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Api
```
On startup, `Helpdesk.Infrastructure/Data/DbSeeder.cs` seeds a first Admin user (and a sample Agent user + a few tickets) if the `Users` table is empty — this is how you get an initial account to log in with, since there's no self-registration.

Run all tests:
```
dotnet test Helpdesk.slnx
```
Run a single test project:
```
dotnet test tests/Helpdesk.Core.Tests/Helpdesk.Core.Tests.csproj
```
Run a single test (xUnit filter):
```
dotnet test --filter "FullyQualifiedName~Helpdesk.Core.Tests.SomeTestClass.SomeTestMethod"
```

### E2E tests (`e2e/`)
Playwright, full-stack, against an isolated `helpdesk_test` database — see `.claude/agents/qa-engineer.md` for setup/run commands, test environment lifecycle, and edge-case suite conventions. Use the `qa-engineer` subagent for configuring/extending this E2E infrastructure or writing test suites rather than doing it ad hoc.

### Frontend (`client/helpdesk-web`)
```
cd client/helpdesk-web
cp .env.example .env
npm install
npm run dev      # dev server at http://localhost:5173
npm run build     # tsc -b && vite build
npm run lint      # oxlint
```

The Vite dev server proxies `/api/*` to `http://localhost:5080` (see `vite.config.ts`), so frontend code should call relative `/api/...` paths rather than hardcoding the backend origin.

## Architecture

### Backend: layered, dependency direction matters
Four .NET projects under `src/`, referenced via `Helpdesk.slnx`:
- `Helpdesk.Core` — domain entities (`Ticket`, `Message`, `User`, `Classification`), enums (`TicketStatus`, `Role`), and interfaces (`ITicketRepository`, `IUserRepository`, `IMessageRepository`, `IAiService`, etc.). Must have **no** dependency on EF Core, Npgsql, or the Graph SDK — it defines contracts, not implementations.
- `Helpdesk.Application` — use-case/orchestration services (e.g. ticket workflow, classification flow), depends only on `Helpdesk.Core`.
- `Helpdesk.Infrastructure` — implements `Helpdesk.Core` interfaces: EF Core `HelpdeskDbContext` + migrations (`Data/Migrations`), repositories (`Repositories/`), a dev-only `DbSeeder`, the Microsoft Graph email client, and the OpenRouter AI client. `DependencyInjection.cs` exposes `AddInfrastructure(IConfiguration)`, which registers the DbContext (Npgsql) and repositories as scoped services — call this from `Program.cs` rather than registering them inline.
- `Helpdesk.Api` — ASP.NET Core Web API, controller-based (not Minimal API) by explicit choice. References `Application` for business logic and `Infrastructure` only for DI/composition-root wiring in `Program.cs` (via `AddInfrastructure`) — controllers should not call Infrastructure types directly.

### Request validation
Request DTOs are validated with **FluentValidation** (`FluentValidation.AspNetCore`), wired up in `Program.cs` via `AddValidatorsFromAssemblyContaining<Program>()` + `AddFluentValidationAutoValidation()` — validators are picked up automatically from the `Helpdesk.Api` assembly, no per-endpoint registration needed. A failing validator short-circuits with a `400` and a standard `ValidationProblemDetails` body before the action runs, so controllers should not hand-roll null/whitespace checks on request bodies. Validators live alongside the API in `Helpdesk.Api/Validators/` (e.g. `CreateUserRequestValidator.cs` for `UsersController`'s `CreateUserRequest`) — add one per new request DTO rather than validating inline.

When adding a feature: define the interface/entity in `Core`, implement it in `Infrastructure`, orchestrate it in `Application`, and expose it via a controller in `Api`.

Note: the single `User` entity/table holds both Admin and Agent accounts, distinguished by the `Role` enum — there is no separate "Agent" entity.

### Frontend
React + TypeScript SPA scaffolded with Vite, routed with `react-router` (pages under `src/pages`, top-level routing in `src/App.tsx`). Talks to the backend exclusively over `/api/*`, proxied to the .NET API in dev.

Styling is Tailwind CSS v4 (`@tailwindcss/vite`, config-free — tokens live in `src/index.css`). UI components come from **shadcn/ui** (`new-york` style, blue theme, Lucide icons); component source is vendored into `src/components/ui` (all standard components installed) rather than pulled from a package. Prefer an existing shadcn component over hand-rolled markup when one fits (`Button`, `Input`, `Label`, `Card`, `Table`, `Badge`, `Alert`, etc. are already used across `NavBar`/`LandingPage`/`HomePage`/`AdminUsersPage`). Add more components with:
```
cd client/helpdesk-web
npx shadcn@3.8.5 add <component>
```
Pinned to `3.8.5` rather than `@latest` — the current `shadcn@4.x` CLI dropped `--base-color`/simple theming in favor of a browser-based preset builder that doesn't fit this non-interactive workflow; `3.8.5` still supports Tailwind v4 and `-b <base-color>`. Re-evaluate the pin if that changes upstream.

### Auth model (Phase 1 done)
Microsoft Entra ID SSO end-to-end: MSAL for React on the frontend, Microsoft Identity Web (JWT bearer) on the backend, with Admin/Agent role claims (`AdminOnly`/`AgentOnly` authorization policies in `Program.cs`). Admins create Agent accounts; there's no self-registration.

### User management (Phase 3 done)
`UsersController` (`AdminOnly`) exposes `GET /api/users` and `POST /api/users` to create `Role.Agent` records by email/display name. `AuthController.Me()` links a pre-created record to the caller's Entra object ID (`ExternalObjectId`) on first authenticated call, matched by email. The admin-only "Add Agent" UI is built at `src/pages/AdminUsersPage.tsx` (shadcn `Card`/`Table`/`Input`/`Button`/`Badge`), routed at `/admin/users`.

### Email ingestion (Phase 4 done)
Microsoft Graph app-only mail polling: `GraphApi` config section (`TenantId`/`ClientId`/`MailboxAddress`/`PollingIntervalSeconds` in `appsettings.Development.json`; `ClientSecret` is never committed — set it via `dotnet user-secrets set GraphApi:ClientSecret <value>` from `src/Helpdesk.Api`, matching the `UserSecretsId` already in `Helpdesk.Api.csproj`). Requires its own Entra app registration (distinct from the API's `AzureAd` app registration used for user sign-in) with `Mail.ReadWrite` and `Mail.Send` Graph **application** permissions and admin consent — `Mail.ReadWrite` (not `Mail.Read`) is required because marking a message processed is a PATCH that sets `isRead = true`. Ingestion is opt-in: `AddGraphApi`/`AddApplication` skip registering the Graph client, `IMailClient`, `EmailIngestionService`, and the `EmailIngestionBackgroundService` hosted service entirely when the `GraphApi` section is absent (or `GraphApi:Enabled` is explicitly `false`), rather than throwing — a fresh clone or an E2E run with no Graph credentials starts normally with the poller simply disabled, instead of crashing at startup.

### AI classification (Phase 5 done)
`IAiService.ClassifyAsync` (Core) returns category + summary + confidence in one OpenRouter chat-completions call (`Helpdesk.Infrastructure/Ai/OpenRouterAiService`, JSON-mode output, fixed category set in `Helpdesk.Core/Models/TicketCategories.cs`, unknown categories normalize to `Other`). `ClassificationService` (`Helpdesk.Application/Classification`) runs right after `EmailIngestionService` creates a **new** ticket, strips the HTML body to plain text, and stores a `Classification`; AI/persistence failures are logged and leave the ticket unclassified (never fail ingestion). Config: `OpenRouter:Model` in `appsettings.Development.json`; `OpenRouter:ApiKey` via `dotnet user-secrets set OpenRouter:ApiKey <value>` from `src/Helpdesk.Api`. Opt-in like Graph: no `OpenRouter` section (or `OpenRouter:Enabled=false`) -> nothing AI is registered and the host still starts (E2E sets `OpenRouter__Enabled=false`). Because `appsettings.Development.json` now contains the `OpenRouter` section, `dotnet run` and the `dotnet ef` commands above fail at startup with "OpenRouter:ApiKey is not configured." unless the user-secret is set or `OpenRouter__Enabled=false` is set in the environment. `SummarizeAsync` was folded into `ClassifyAsync`; `DraftReplyAsync` was implemented in Phase 6 (see the AI draft reply section). Manual end-to-end verification (implementation-plan task 35) was performed on 2026-09-26: with the default paid model and an unfunded key every ticket got HTTP 402, one error was logged per ticket, and tickets/messages were still created; with the free model `nvidia/nemotron-3-super-120b-a12b:free` (via `OpenRouter__Model`) a test email was stored as a `Billing` classification with a summary. Not verified live: a reply on the same thread not creating a second classification (unit-tested only). `OpenRouterAiService` surfaces the provider's `error.message`/`code` even when OpenRouter answers HTTP 200 with an error body and no choices, retries transient failures (HTTP 408/429/5xx, or a 200 whose error code is 408/429/5xx, and client timeouts) up to 2 times with 500 ms then 1500 ms backoff, and does not retry other 4xx (e.g. 402) or malformed model output. Caution: the poller ingests up to 50 unread messages per poll and marks them read, so pointing a dev environment at a mailbox with a backlog floods the dev DB with tickets (and, with a funded key, many LLM calls); clear or redirect the mailbox before manual testing. To repeat the verification:
1. From `src/Helpdesk.Api`, run `dotnet user-secrets set OpenRouter:ApiKey <value>` and `dotnet user-secrets set GraphApi:ClientSecret <value>`, then `dotnet run`.
2. Send an email to the monitored mailbox and wait one poll interval (`GraphApi:PollingIntervalSeconds`, 60s by default).
3. Verify with `psql -U helpdesk -h localhost -d helpdesk -c 'select t."Subject", c."Category", c."Confidence", c."Summary" from "Tickets" t join "Classifications" c on c."TicketId" = t."Id" order by c."CreatedAt" desc limit 3;'`. A reply on the same email thread must not create a second classification, and a wrong API key must log a single "Failed to classify ticket" error while the ticket and message are still created.

### AI draft reply (Phase 6 done)
`IAiService.DraftReplyAsync` (Core) drafts a plain-text reply from the ticket's first customer message plus matched KB articles. The KB is the embedded `Helpdesk.Infrastructure/Ai/Kb/kb.json` (~10 hand-keyworded articles, illustrative placeholder policies — replace with real ones), served through `IKnowledgeBase`/`JsonKnowledgeBase` and registered unconditionally by `AddKnowledgeBase()`. `KbMatcher` (`Helpdesk.Application/DraftReply`) scores articles by distinct whole-word keyword hits in subject + body (+1 when the article's category equals the ticket's classified category; a category match alone never selects an article), top 3. `DraftReplyService` runs in `EmailIngestionService` right after classification for **new** tickets only, stores `Ticket.DraftReply` and sets `New -> InReview`; a failed/blank draft is logged and leaves the ticket `New` with a null draft (never fails ingestion, never retried). Same opt-in gate as classification (`IDraftReplyService` is only registered when OpenRouter is configured). `OpenRouterAiService` shares its post/retry code between classify and draft; the draft call is plain-text (no JSON mode), `max_tokens` 600, output trimmed and capped at 4000 chars, and the email body has any `</email_body>` tag stripped before it goes into the prompt. Spec: `docs/superpowers/specs/2026-09-26-phase6-ai-draft-reply-design.md`; plan: `docs/superpowers/plans/2026-09-26-phase6-ai-draft-reply.md`. Manual verification (task 39) was performed on 2026-09-26 with the free model `nvidia/nemotron-3-super-120b-a12b:free` (via `OpenRouter__Model`): a real "Charged multiple times" email became a `Billing` (0.95) ticket with `Status` 1 (InReview) and a draft grounded in the refund KB article (asks for order/invoice number and reason, cites the 5-10 business day refund window, signed "The Support Team"), no errors logged. Not verified live: a reply on the same thread not producing a second draft (unit-tested only). To repeat (needs the user's secrets and a real email): after the Phase 5 steps, `psql -U helpdesk -h localhost -d helpdesk -c 'select "Subject", "Status", left("DraftReply", 200) from "Tickets" order by "CreatedAt" desc limit 3;'` should show `1` (InReview; status is stored as an integer, New=0/InReview=1/Replied=2) and a plausible draft.

### Data flow (MVP core loop)
Email arrives in an O365 mailbox → polled via Microsoft Graph API (`Helpdesk.Infrastructure/GraphApi`, background `IHostedService`, per `implementation-plan.md` Phase 4) → ticket + message created → AI classification/summary via OpenRouter (`Helpdesk.Infrastructure/Ai`) → AI drafts a reply against a hardcoded KB → ticket status `New → InReview` → agent reviews/edits in the queue UI → reply sent back through Graph API as a threaded email reply → status `Replied`.
