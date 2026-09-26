# Implementation Plan (MVP)

## Phase 0 — Project Setup
1. Create `Helpdesk.sln` with projects: `Helpdesk.Api`, `Helpdesk.Application`, `Helpdesk.Core`, `Helpdesk.Infrastructure`
2. Wire project references (Api → Application → Core; Infrastructure → Core)
3. Add PostgreSQL + EF Core NuGet packages to `Helpdesk.Infrastructure`
4. Scaffold React + TypeScript + Vite app in `client/helpdesk-web`
5. Set up local dev config: `appsettings.Development.json`, `.env` for frontend, local Postgres instance
6. Set up Git repo, `.gitignore`, base README

## Phase 1 — Auth (Entra SSO) — done
7. Register app in Entra ID (API app registration + SPA app registration)
8. Add Microsoft Identity Web to `Helpdesk.Api`, configure JWT bearer validation
9. Define `Role` enum (Admin, Agent) and claim mapping from Entra token
10. Add MSAL React to frontend, implement login flow, acquire/attach bearer token to API calls
11. Add `[Authorize]` policies for Admin-only and Agent-only endpoints
12. Manual test: login as admin, confirm token reaches API and role claim is readable

## Phase 2 — Core Domain & Data Model — done
13. Define entities in `Helpdesk.Core/Entities`: `Ticket`, `Message`, `User`, `Classification` (`User` holds both Admin and Agent accounts, distinguished by `Role` — there is no separate Agent entity)
14. Define `TicketStatus` enum (New, InReview, Replied)
15. Define repository interfaces in `Helpdesk.Core/Interfaces` (`ITicketRepository`, `IUserRepository`, `IMessageRepository`)
16. Implement `HelpdeskDbContext` in `Helpdesk.Infrastructure/Data` with entity configurations
17. Generate and apply initial EF Core migration
18. Implement repository classes in `Helpdesk.Infrastructure/Repositories`
19. Register DbContext + repositories in DI via `Helpdesk.Infrastructure/DependencyInjection.cs` (`AddInfrastructure(IConfiguration)`), called from `Helpdesk.Api/Program.cs`
19a. Seed dev data via `Helpdesk.Infrastructure/Data/DbSeeder.cs` (first Admin user, sample Agent user, sample tickets) — runs on API startup if `Users` table is empty

## Phase 3 — Minimal User Management (Admin adds Agents) — done
20. `UsersController`: `POST /api/users` (admin-only, create a `User` record with Role = Agent), `GET /api/users` (list) — done
21. Agent creation flow: admin enters the agent's Entra email; `User` record stored with Role = Agent, linked by email/object ID at first login — done (linking happens in `AuthController.Me()`)
22. Frontend: simple "Add Agent" form + user list page (admin-only route) — done (`src/pages/AdminUsersPage.tsx`, built with shadcn `Card`/`Table`/`Input`/`Button`/`Badge`)
23. Manual test: admin adds an agent, agent logs in via SSO and is recognized with Agent role — pending manual verification against a real Entra account
23a. Frontend restructured into routed pages (`react-router`) under `src/pages`; styling migrated to Tailwind CSS v4; shadcn/ui installed (blue theme) with all components under `src/components/ui`, and existing pages/NavBar updated to use shadcn `Button`/`Input`/`Label`/`Card`/`Table`/`Badge`/`Alert` in place of raw HTML — done

## Phase 4 — Email Ingestion (Microsoft Graph) — done
24. Register Graph API permissions (`Mail.ReadWrite` and `Mail.Send`, application permissions, admin consent) on a dedicated app-only app registration for the shared mailbox — done. Note: `Mail.Read` alone is insufficient — marking a message as read (`PATCH .../messages/{id}` with `isRead: true`) requires `Mail.ReadWrite`, confirmed by a real `Access is denied` failure during E2E testing with `Mail.Read`-only.
25. Implement Graph client in `Helpdesk.Infrastructure/GraphApi` — done (`GraphMailClient`, `GraphApiOptions`, `AddGraphApi` DI extension)
26. Implement `IHostedService` background poller: fetch new/unread emails on an interval — done (`Helpdesk.Application/EmailIngestion/EmailIngestionBackgroundService`, default 60s, configurable via `GraphApi:PollingIntervalSeconds`)
27. Map incoming email → `Ticket` + initial `Message` (subject, body, sender, received time) — done (`EmailIngestionService`)
28. Handle threading: match reply emails (by conversationId) to existing ticket, append as new `Message` — done in code (matches by `ConversationId` via `ITicketRepository.GetByConversationIdAsync`); not yet exercised with a live reply email — see note below
29. Mark processed emails as read/handled to avoid duplicate ingestion — done (`IMailClient.MarkAsProcessedAsync`, verified real messages no longer reprocessed on subsequent polls)
30. Manual test: send a test email to the mailbox, confirm a ticket is created — done, verified 2026-09-21 against `epm1@prosaressolutions.onmicrosoft.com` (7 real unread emails ingested into 7 tickets/messages, correctly marked read afterward, zero errors on the following poll). Reply-threading half of this test (reply to an email, confirm it appends to the same ticket) was deliberately deferred rather than run in this session — the code path is implemented and covered by `EmailIngestionServiceTests`, but has not been exercised against a real Graph reply. Recommended before relying on this in production.

## Phase 5 — AI Classification & Summary — done (manual test pending)
31. Define `IAiService` interface in `Helpdesk.Core/Interfaces` — done. Deviation: `SummarizeAsync` was folded into `ClassifyAsync` (one call returns category + summary + confidence); `DraftReplyAsync` was added in Phase 6.
32. Implement OpenRouter HTTP client in `Helpdesk.Infrastructure/Ai` — done (`OpenRouterAiService`, JSON-mode output, opt-in DI registration)
33. Implement `ClassificationService` in `Helpdesk.Application`: calls AI on new ticket, stores category + summary on `Classification` — done (`Helpdesk.Application/Classification`, strips HTML body to plain text first)
34. Hook classification into the ingestion pipeline (runs right after ticket creation) — done (`EmailIngestionService` classifies newly created tickets only; failures are logged and never fail ingestion)
35. Manual test: verify a new ticket gets a category and summary populated automatically � done, verified 2026-09-26 against the shared mailbox with a real OpenRouter key. First run with the default paid model `openai/gpt-4o-mini` got HTTP 402 Payment Required for every ticket (account had no credits): tickets and messages were still created, one error was logged per ticket, and ingestion continued, so the failure path behaved as designed. Second run used the free model `nvidia/nemotron-3-super-120b-a12b:free` via the environment override `OpenRouter__Model` (no repo config change): a "Charged twice" test email was stored as a `Classification` with Category `Billing`, Confidence 0.95 and a one-sentence summary. One earlier ticket failed classification because OpenRouter returned HTTP 200 with a 503 "provider_overloaded" error body and no choices; `OpenRouterAiService` now surfaces the provider error and retries transient failures (see CLAUDE.md). Not verified live: a reply on the same email thread not creating a second classification (covered only by unit tests). Caution: the poller ingests up to 50 unread messages per poll and marks them read, so pointing a dev environment at a mailbox with a backlog floods the dev DB with tickets (and, with a funded key, many LLM calls) � clear or redirect the mailbox first.

## Phase 6 — AI Draft Reply (Hardcoded KB) — done (manual test pending)
36. Create hardcoded KB as static content — done (`Helpdesk.Infrastructure/Ai/Kb/kb.json`, embedded; `IKnowledgeBase`/`JsonKnowledgeBase`; `KbMatcher` keyword + category-boost selection in `Helpdesk.Application/DraftReply`)
37. Implement `DraftReplyAsync` — done (`IAiService.DraftReplyAsync`, `OpenRouterAiService`; prompt includes ticket content + matched KB articles, returns draft text)
38. Store draft on the ticket; set ticket status to `InReview` — done (`DraftReplyService` sets `Ticket.DraftReply` + `InReview`; called by `EmailIngestionService` after classification for new tickets)
39. Manual test: confirm a plausible draft reply is generated and stored for a new ticket — pending (needs the user's OpenRouter/Graph secrets and a real test email; see CLAUDE.md "AI draft reply")

## Phase 7 — Agent Queue & Reply UI
40. `TicketsController`: `GET /api/tickets` (queue, filtered to agent's assigned/unassigned tickets), `GET /api/tickets/{id}` (detail incl. thread + draft)
41. `PUT /api/tickets/{id}/reply`: agent submits edited reply text, marks status `Replied`, sends email
42. Implement outbound email send via Graph API (reply-to-thread using conversationId)
43. Frontend: Queue page (list of tickets with status, category, summary)
44. Frontend: Ticket detail page — thread view, AI summary, editable draft textarea, Send button
45. Manual test: full loop — email in → ticket appears in queue with draft → agent edits and sends → reply arrives in test inbox as a threaded reply

## Phase 8 — Escalation Path (Low Confidence)
46. Define confidence threshold on `IAiService.ClassifyAsync` response
47. If below threshold or classification fails, tag ticket for manual review (simple flag/badge, no separate queue infra needed at MVP scale)
48. Frontend: visual indicator on queue for escalated/low-confidence tickets

## Phase 9 — Hardening & Polish
49. Error handling: Graph API failures, AI API failures/timeouts (retry or fallback to "no draft available")
50. Logging (structured, e.g. Serilog) across ingestion, AI calls, email send
51. Basic input validation on API endpoints
52. CORS config for frontend origin
53. Smoke-test full loop end-to-end with 5–10 real-style sample emails

## Phase 10 — Deployment
54. Provision PostgreSQL (managed instance or container)
55. Deploy API (App Service / container)
56. Deploy frontend (static hosting / App Service)
57. Configure production Entra app registration redirect URIs and secrets
58. Configure production Graph API mailbox permissions
59. Run EF Core migrations against production DB
60. Final end-to-end verification in production environment

---

**Suggested build order note:** Phases 0–3 establish scaffolding and auth first since nothing else is testable without them. Phase 4 (ingestion) and Phase 5–6 (AI) can be developed in parallel once Phase 2's data model is stable. Phase 7 (UI) depends on 4–6 being functional enough to have real data to display.
