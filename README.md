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
```
docker compose up -d
```
Starts Postgres on `localhost:5432` (db `helpdesk`, user/password `helpdesk`).

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
