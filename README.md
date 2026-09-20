# Helpdesk

AI-assisted support ticket system. See `project-scope.md` for scope, `tech-stack.md` for the stack, and `implementation-plan.md` for the build plan.

## Structure

- `src/Helpdesk.Api` — ASP.NET Core Web API (controllers)
- `src/Helpdesk.Application` — application services / use cases
- `src/Helpdesk.Core` — domain entities and interfaces (no infra dependencies)
- `src/Helpdesk.Infrastructure` — EF Core, repositories, Graph API, AI client
- `client/helpdesk-web` — React + TypeScript + Vite frontend

## Local setup

### Database
Uses a local PostgreSQL instance (not Docker) on `localhost:5432`:
```
createuser -s helpdesk
createdb -O helpdesk helpdesk
psql -c "ALTER ROLE helpdesk WITH PASSWORD 'helpdesk';"
```

### Backend
```
cd src/Helpdesk.Api
dotnet run
```
API runs at `http://localhost:5080` by default. Health check: `GET /api/health`.

### Frontend
```
cd client/helpdesk-web
cp .env.example .env
npm install
npm run dev
```
Runs at `http://localhost:5173`.

### Email ingestion (optional)
The backend runs fine with no `GraphApi` config — the Microsoft Graph email poller is opt-in and simply stays disabled. To enable it, see `CLAUDE.md`'s "Email ingestion (Phase 4 done)" section for the required Entra app registration and config.
