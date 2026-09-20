# Tech Stack

## Frontend
- **React + TypeScript**, bundled with **Vite**
- SPA calling the backend Web API over HTTP
- Routing: `react-router` (routed pages under `src/pages`)
- Forms: `react-hook-form` + `zod` (`@hookform/resolvers`)
- Styling: **Tailwind CSS v4** (`@tailwindcss/vite`)
- UI components: **shadcn/ui** (`new-york` style, blue theme, Lucide icons) — component source lives in `src/components/ui`; add more with `npx shadcn@3.8.5 add <component>` (pinned below `@latest`, see note)
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
│   │   ├── Entities/                 # Ticket, Message, User, Classification, etc. (User holds both Admin and Agent accounts, via Role)
│   │   ├── Enums/                    # TicketStatus (New/InReview/Replied), Role (Admin/Agent)
│   │   └── Interfaces/               # ITicketRepository, IUserRepository, IMessageRepository, IEmailIngestionService, IAiService, etc.
│   │
│   ├── Helpdesk.Infrastructure/      # EF Core, repositories, external integrations
│   │   ├── DependencyInjection.cs    # AddInfrastructure(IConfiguration) — registers DbContext + repositories as scoped services
│   │   ├── Data/
│   │   │   ├── HelpdeskDbContext.cs
│   │   │   ├── Configurations/       # IEntityTypeConfiguration<T> per entity
│   │   │   ├── DbSeeder.cs           # dev-only seed data (first Admin user, sample tickets)
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

## Notes
- The `shadcn@latest` CLI (v4.x) dropped the simple `--base-color` flag in favor of a browser-based preset builder, so component installs are pinned to `shadcn@3.8.5`, which still supports `-b <base-color>` and Tailwind v4. Re-evaluate the pin if a future `shadcn` release restores CLI-only theming.
