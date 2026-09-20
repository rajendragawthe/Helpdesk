---
name: efcore-migration
description: Use whenever a change is made to this repo's EF Core domain model that would make the database schema out of sync — entity classes under Helpdesk.Core/Entities (adding/removing/renaming an entity or property, changing a property's type or nullability), enum changes that affect stored columns (e.g. Helpdesk.Core/Enums/TicketStatus.cs, Role.cs) if persisted as native enum/int, IEntityTypeConfiguration classes under Helpdesk.Infrastructure/Data/Configurations (new HasOne/HasMany relationship, new index, new constraint, changed max length, changed delete behavior), HelpdeskDbContext.cs (new/removed DbSet), or any rename of an existing entity/table (e.g. the past Agent -> User rename). Also trigger when the user asks to "add a migration", "update the schema", "sync the database", or says a build/runtime error mentions a missing column, missing table, or "pending model changes" in HelpdeskDbContext. Do NOT trigger for changes that don't affect persisted shape: Application-layer services, controllers, DTOs, seed data changes in DbSeeder.cs, or frontend code.
---

# EF Core Migration Workflow

This repo's Postgres schema is derived from `Helpdesk.Core` entities and
`Helpdesk.Infrastructure/Data/Configurations`, versioned as EF Core
migrations under `Helpdesk.Infrastructure/Data/Migrations`. Whenever the
model changes, the migration and the database must be brought back in sync
before the app is considered done.

## Steps

1. **Identify the model change.** Confirm what actually changed: new/removed
   entity, new/removed/renamed property, changed relationship, changed index
   or constraint, or an entity/table rename.

2. **Generate the migration** from the repo root:
   ```
   dotnet ef migrations add <DescriptiveName> --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Api --output-dir Data/Migrations
   ```
   - Use a descriptive PascalCase name for `<DescriptiveName>` (e.g.
     `AddTicketPriority`, `RenameAgentToUser`).
   - This only generates the migration file — it does not touch the database.
   - If this is a pure rename (entity/table/column) rather than an additive
     change, consider whether a clean rewrite is warranted instead of a
     generated add/drop migration (see "Renames" below).

3. **Review the generated migration** in `Data/Migrations/<timestamp>_<Name>.cs`
   before applying it — confirm the `Up()`/`Down()` operations match intent
   (e.g. a rename should ideally use `RenameColumn`/`RenameTable`, not a
   drop+add, to avoid data loss in real environments).

4. **Apply it to the local database:**
   ```
   dotnet ef database update --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Api
   ```
   - Local dev Postgres connection comes from `appsettings.Development.json`
     (`ConnectionStrings:DefaultConnection`).
   - If the backend (`dotnet run`) is currently running, stop it first — it
     holds a lock on the build output and can also hold DB connections.

5. **Verify** the schema landed as expected (e.g. via `psql \dt` / `\d
   <table>`), then rebuild the solution (`dotnet build Helpdesk.slnx`) and
   smoke-test `GET /api/health` plus any endpoint touching the changed
   entity.

## Renames (e.g. entity/table renamed)

When an entity or table is renamed rather than added, EF Core's default
`migrations add` often generates a drop-and-recreate for that table instead
of a rename, which loses data. For a project still in early development
(no real data to preserve), it's acceptable to:
1. Delete the stale migration(s).
2. Drop the affected table(s) directly in Postgres.
3. Regenerate `InitialCreate` (or the next logical migration) fresh.
4. Re-run `database update`.

For anything past early development, hand-edit the generated migration to
use `migrationBuilder.RenameTable`/`RenameColumn` instead, to preserve data.

## Notes specific to this repo

- Migrations live under `Helpdesk.Infrastructure/Data/Migrations`, not the
  project root — always pass `--output-dir Data/Migrations`.
- `Helpdesk.Core` must stay free of EF Core/Npgsql dependencies — only
  `Helpdesk.Infrastructure` should reference migration tooling.
- `DbSeeder.cs` seed data is separate from migrations and does not need a
  migration of its own, but may need updating if a renamed/changed property
  breaks its seed calls.
