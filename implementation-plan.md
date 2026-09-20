# Implementation Plan (MVP)

## Phase 0 — Project Setup
1. Create `Helpdesk.sln` with projects: `Helpdesk.Api`, `Helpdesk.Application`, `Helpdesk.Core`, `Helpdesk.Infrastructure`
2. Wire project references (Api → Application → Core; Infrastructure → Core)
3. Add PostgreSQL + EF Core NuGet packages to `Helpdesk.Infrastructure`
4. Scaffold React + TypeScript + Vite app in `client/helpdesk-web`
5. Set up local dev config: `appsettings.Development.json`, `.env` for frontend, local Postgres (Docker Compose)
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

## Phase 3 — Minimal User Management (Admin adds Agents)
20. `UsersController`: `POST /api/users` (admin-only, create a `User` record with Role = Agent), `GET /api/users` (list)
21. Agent creation flow: admin enters the agent's Entra email; `User` record stored with Role = Agent, linked by email/object ID at first login
22. Frontend: simple "Add Agent" form + user list page (admin-only route)
23. Manual test: admin adds an agent, agent logs in via SSO and is recognized with Agent role

## Phase 4 — Email Ingestion (Microsoft Graph)
24. Register Graph API permissions (Mail.Read) on the app registration for the shared mailbox
25. Implement Graph client in `Helpdesk.Infrastructure/GraphApi`
26. Implement `IHostedService` background poller: fetch new/unread emails on an interval
27. Map incoming email → `Ticket` + initial `Message` (subject, body, sender, received time)
28. Handle threading: match reply emails (by conversationId) to existing ticket, append as new `Message`
29. Mark processed emails as read/handled to avoid duplicate ingestion
30. Manual test: send a test email to the mailbox, confirm a ticket is created; reply to it, confirm it threads onto the same ticket

## Phase 5 — AI Classification & Summary
31. Define `IAiService` interface in `Helpdesk.Core/Interfaces` (`ClassifyAsync`, `SummarizeAsync`, `DraftReplyAsync`)
32. Implement OpenRouter HTTP client in `Helpdesk.Infrastructure/Ai`
33. Implement `ClassificationService` in `Helpdesk.Application`: calls AI on new ticket, stores category + summary on `Classification`
34. Hook classification into the ingestion pipeline (runs right after ticket creation)
35. Manual test: verify a new ticket gets a category and summary populated automatically

## Phase 6 — AI Draft Reply (Hardcoded KB)
36. Create hardcoded KB as static content (JSON/config file or in-code constants) in `Helpdesk.Infrastructure/Ai`
37. Implement `DraftReplyAsync`: prompt includes ticket content + relevant hardcoded KB snippets, returns draft text
38. Store draft on the ticket/message record; set ticket status to `InReview`
39. Manual test: confirm a plausible draft reply is generated and stored for a new ticket

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
