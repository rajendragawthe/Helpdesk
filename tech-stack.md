# Tech Stack

## Frontend
- **React + TypeScript**, bundled with **Vite**
- SPA calling the backend Web API over HTTP
- Auth: MSAL for React (`@azure/msal-react`) for Entra SSO, calling the API with a bearer token

## Backend
- **.NET 10**, **ASP.NET Core Web API**, controller-based (no Minimal API)
- **Entity Framework Core** as ORM
- **PostgreSQL** as the database (via `Npgsql.EntityFrameworkCore.PostgreSQL`)
- Auth: Microsoft Identity Web (Entra ID / Azure AD, JWT bearer validation), role claims for Admin/Agent

## Solution structure
Clean, multi-project solution separating API, domain, and data access:

```
Helpdesk.sln
├── src/
│   ├── Helpdesk.Api/                # ASP.NET Core Web API (controllers, DI wiring, Program.cs)
│   │   ├── Controllers/
│   │   ├── Program.cs
│   │   └── appsettings.json
│   │
│   ├── Helpdesk.Core/                # Domain layer — no EF/infra dependencies
│   │   ├── Entities/                 # Ticket, Message, Agent, Classification, etc.
│   │   ├── Enums/                    # TicketStatus (New/InReview/Replied), Role
│   │   └── Interfaces/               # IRepository<T>, ITicketRepository, IEmailIngestionService, IAiService, etc.
│   │
│   ├── Helpdesk.Infrastructure/      # EF Core, repositories, external integrations
│   │   ├── Data/
│   │   │   ├── HelpdeskDbContext.cs
│   │   │   └── Migrations/
│   │   ├── Repositories/             # EF Core implementations of Core interfaces
│   │   ├── GraphApi/                 # Microsoft Graph email ingestion client
│   │   └── Ai/                       # OpenRouter client implementing IAiService
│   │
│   └── Helpdesk.Application/         # (optional) services/use-cases orchestrating Core + Infrastructure
│       └── Services/                 # TicketService, ClassificationService, ReplyDraftService
│
├── tests/
│   ├── Helpdesk.Core.Tests/
│   └── Helpdesk.Infrastructure.Tests/
│
└── client/
    └── helpdesk-web/                 # React + TypeScript + Vite frontend
```

### Project dependency direction
`Helpdesk.Api` → `Helpdesk.Application` → `Helpdesk.Core`
`Helpdesk.Infrastructure` → `Helpdesk.Core` (implements its interfaces)
`Helpdesk.Api` references `Helpdesk.Infrastructure` only for DI registration (composition root), not for business logic.

`Helpdesk.Core` has no NuGet dependencies on EF Core, Npgsql, or Graph SDK — keeps entities and interfaces persistence-agnostic and testable.

## Supporting pieces
- **Email ingestion**: Microsoft Graph SDK, polled via a hosted background service (`IHostedService`) — avoids Graph webhook subscription/renewal complexity at MVP volume (~100 tickets/day)
- **AI calls**: OpenRouter via `HttpClient` (typed client in `Helpdesk.Infrastructure/Ai`)
- **Migrations**: EF Core Migrations, generated from `Helpdesk.Infrastructure`, applied via `dotnet ef database update` or on startup for dev
