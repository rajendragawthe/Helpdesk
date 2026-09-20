# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

AI-assisted support ticket system (MVP stage). Full context lives in three docs at the repo root — read them before making scope decisions:
- `project-scope.md` — problem, full feature vision, and the MVP scope (core loop: ingest email → AI classifies/summarizes → AI drafts reply from a hardcoded KB → agent reviews/edits/sends)
- `tech-stack.md` — stack rationale and intended solution/project layout
- `implementation-plan.md` — phased task breakdown (Phase 0 scaffolding is done; work proceeds phase by phase)

## Commands

### Database
```
docker compose up -d
```
Starts Postgres on `localhost:5432` (db/user/password: `helpdesk`).

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

When adding a feature: define the interface/entity in `Core`, implement it in `Infrastructure`, orchestrate it in `Application`, and expose it via a controller in `Api`.

Note: the single `User` entity/table holds both Admin and Agent accounts, distinguished by the `Role` enum — there is no separate "Agent" entity.

### Frontend
Plain React + TypeScript SPA scaffolded with Vite (no framework router/state library added yet). Talks to the backend exclusively over `/api/*`, proxied to the .NET API in dev.

### Auth model (Phase 1 done)
Microsoft Entra ID SSO end-to-end: MSAL for React on the frontend, Microsoft Identity Web (JWT bearer) on the backend, with Admin/Agent role claims (`AdminOnly`/`AgentOnly` authorization policies in `Program.cs`). Admins create Agent accounts; there's no self-registration. User management UI (Phase 3) is not yet built.

### Data flow (MVP core loop)
Email arrives in an O365 mailbox → polled via Microsoft Graph API (`Helpdesk.Infrastructure/GraphApi`, background `IHostedService`, per `implementation-plan.md` Phase 4) → ticket + message created → AI classification/summary via OpenRouter (`Helpdesk.Infrastructure/Ai`) → AI drafts a reply against a hardcoded KB → ticket status `New → InReview` → agent reviews/edits in the queue UI → reply sent back through Graph API as a threaded email reply → status `Replied`.
