# Phase 4 — Email Ingestion (Microsoft Graph) — Design

Date: 2026-09-20
Status: Approved for planning
Covers: `implementation-plan.md` Phase 4, tasks 24–30

## Goal

Poll a shared mailbox via Microsoft Graph, turn new inbound emails into
`Ticket`/`Message` records, thread replies onto existing tickets by
`conversationId`, and avoid double-processing the same email.

## Component map

```
Helpdesk.Core/
  Interfaces/IMailClient.cs           — Graph-agnostic mail abstraction
  Interfaces/IMessageRepository.cs    — + GetByExternalMessageIdAsync
  Models/InboundEmailMessage.cs       — plain DTO for a fetched email

Helpdesk.Infrastructure/GraphApi/
  GraphMailClient.cs         — IMailClient impl (Graph SDK, app-only auth)
  GraphApiOptions.cs         — bound from "GraphApi" config section
  DependencyInjection.cs     — AddGraphApi(IConfiguration) extension

Helpdesk.Application/
  EmailIngestion/EmailIngestionService.cs           — fetch → dedupe → thread-match → create/append → mark processed
  EmailIngestion/EmailIngestionBackgroundService.cs — BackgroundService, ticks every GraphApi:PollingIntervalSeconds
  DependencyInjection.cs     — AddApplication() extension (new — Application currently has no DI wiring)

Helpdesk.Api/Program.cs — calls AddGraphApi(...) and AddApplication(), alongside the existing AddInfrastructure(...) call
```

### Why the hosted service lives in `Helpdesk.Application`

`Helpdesk.Infrastructure` cannot reference `Helpdesk.Application` (this
repo's dependency direction rule: `Api → Application → Core`,
`Infrastructure → Core`). The poller needs both the mail client and the
ticket/message repositories in the same loop, so it lives in
`Helpdesk.Application`, which already sits above the `Core` interfaces
it depends on (`IMailClient`, `ITicketRepository`, `IMessageRepository`).
This also keeps `EmailIngestionService`'s mapping/threading logic
unit-testable against a fake `IMailClient` with no Graph SDK or real
database involved.

`Helpdesk.Application` currently has no NuGet packages beyond the
`Helpdesk.Core` project reference — this phase adds
`Microsoft.Extensions.Hosting.Abstractions` (for `BackgroundService`)
and establishes its first `DependencyInjection.cs` (`AddApplication()`),
mirroring the existing `AddInfrastructure()` pattern in
`Helpdesk.Infrastructure`.

## Auth & configuration

Graph access uses **app-only auth** (client credentials flow), a
**separate Entra app registration** from the one already configured
under `AzureAd` in `appsettings.json` — that one validates incoming
user JWTs for the API; this one is a `ClientSecretCredential` used to
call Graph as the application itself, with `Mail.ReadWrite` and
`Mail.Send` (needed later in Phase 7) application permissions and admin
consent. **Correction from the original design:** `Mail.Read` alone is
not sufficient — `IMailClient.MarkAsProcessedAsync` PATCHes a message's
`isRead` property, which Graph requires `Mail.ReadWrite` for; this was
only discovered during real E2E testing (a `Mail.Read`-only app
registration got `Access is denied` on the mark-as-processed call after
successfully fetching messages).

New `GraphApi` config section:
- `TenantId`, `ClientId`, `MailboxAddress` — non-secret, go in
  `appsettings.Development.json`.
- `ClientSecret` — goes in `dotnet user-secrets` for
  `Helpdesk.Api`, never committed.
- `PollingIntervalSeconds` — default `60`, in
  `appsettings.Development.json`.

Bound to a `GraphApiOptions` class via `IOptions<GraphApiOptions>` in
`Helpdesk.Infrastructure/GraphApi/GraphApiOptions.cs`.

### Entra portal prerequisites (manual, not automatable from here)

Before end-to-end testing is possible:
1. Register a new Entra app (or reuse an existing app-only app if the
   user already has one) for Graph access.
2. Add **application** permissions `Mail.ReadWrite` and `Mail.Send`
   (Microsoft Graph), then grant admin consent. (`Mail.Read` is not
   enough — see the correction above.)
3. Create a client secret, note its value immediately (shown once).
4. Note the `TenantId`, `ClientId`, and the target shared mailbox's
   email address.
5. Confirm the shared mailbox exists and the app-only credential can
   reach it (app-only Graph mail access does not require the mailbox
   to be "assigned" to a user the way delegated access does, but the
   mailbox must exist as a real mail-enabled object in the tenant).

These steps are handed to the user separately; the code in this phase
is written against the config shape above so it can be wired up the
moment those values exist.

## Data model changes

- `IMessageRepository` gains `Task<Message?> GetByExternalMessageIdAsync(string externalMessageId)`,
  implemented in `Helpdesk.Infrastructure/Repositories/MessageRepository.cs`
  via a straightforward EF Core query on `Message.ExternalMessageId`.
- No new entities. `Ticket.ConversationId` and `Message.ExternalMessageId`
  already exist and were evidently designed for this phase — no
  migration needed as long as `ExternalMessageId` is indexed enough for
  the dedupe lookup to be cheap (check existing configuration; add an
  index in `Helpdesk.Infrastructure/Data/Configurations/MessageConfiguration.cs`
  if missing, via `efcore-migration` skill if a migration is required).

## Ingestion algorithm (`EmailIngestionService.IngestNewEmailsAsync`)

Runs once per background-service tick, inside a DI scope
(`IServiceScopeFactory.CreateScope()`, since `ITicketRepository`/
`IMessageRepository` are scoped services and the background service
itself is a singleton):

1. `IMailClient.FetchNewMessagesAsync()` returns unread Inbox messages
   (`isRead eq false`), ordered by `receivedDateTime` ascending, mapped
   to `InboundEmailMessage` (ExternalMessageId, ConversationId,
   FromAddress, Subject, BodyHtml, ReceivedAt).
2. For each message, in order:
   a. If `IMessageRepository.GetByExternalMessageIdAsync(ExternalMessageId)`
      returns non-null, skip it (already ingested; guards against a
      crash between step 2c and step 3 leaving a message unread but
      already recorded).
   b. `ITicketRepository.GetByConversationIdAsync(ConversationId)`:
      - **Not found** → create a new `Ticket` (`Subject`,
        `RequesterEmail` = `FromAddress`, `Status = New`,
        `ConversationId`, timestamps) and its first `Message`
        (`Body` = raw HTML `BodyHtml`, `IsFromUser = true`,
        `ExternalMessageId`, `ReceivedAt`).
      - **Found** → append a new `Message` to that ticket (same shape),
        and bump `Ticket.UpdatedAt`. **`Ticket.Status` is left
        unchanged** even if it was `Replied` — no auto-reopening in
        this phase (explicit decision; revisit later if needed).
   c. Persist (repository `AddAsync`/`UpdateAsync`).
   d. `IMailClient.MarkAsProcessedAsync(ExternalMessageId)` — Graph PATCH
      setting `isRead = true`.
3. If step 2's per-message body throws, catch, log with the message's
   `ExternalMessageId`/`ConversationId`, and continue to the next
   message — one bad email must not block the rest of the batch.
4. If step 1 (the fetch itself) throws (auth failure, Graph
   unreachable, throttling), catch at the tick level, log, and let the
   next timer tick retry — the host must not crash or crash-loop.

## Body format

`Message.Body` stores the **raw HTML** Graph returns
(`body.content` when `body.contentType == "html"`), unmodified. No
plain-text conversion in this phase (explicit decision — Phase 5/6 AI
prompts and the future thread-view UI will handle HTML as needed, so
this phase doesn't add a conversion step that might need revisiting
anyway).

## Error handling summary

| Failure | Handling |
|---|---|
| Graph fetch fails (auth/network/throttle) for a whole tick | Caught, logged, tick ends; next timer interval retries |
| Mapping/persistence fails for one message | Caught, logged with message id, loop continues to next message; message stays unread in the mailbox and will be retried next tick |
| `MarkAsProcessedAsync` fails after successful ticket/message creation | Logged; message stays unread and will be re-fetched next tick — the `GetByExternalMessageIdAsync` dedupe check in step 2a prevents a duplicate `Ticket`/`Message` from being created on the retry |

### Accepted risk: `isRead` as the "already ingested" marker

Using `isRead = true` as the sole signal that a message has been ingested means the poller cannot distinguish "we processed this" from "a human (or a rule) marked it read some other way" — e.g. an agent previewing the shared mailbox in Outlook, a mobile mail client marking messages read on open, or a forwarding/triage rule. Any message marked read before a poll runs is silently excluded from the `isRead eq false` filter and is never ingested, with no ticket, no log line, and no record it ever existed. This is an accepted risk for the MVP's polling-based design (a dedicated "processed" marker — a category, a custom property, or an external cursor — would avoid it but adds complexity not justified yet), not a bug; it's recorded here so it isn't forgotten before this becomes an operational dependency.

## Testing

- **Unit tests** in a new `tests/Helpdesk.Application.Tests` project
  (mirroring the existing `Helpdesk.Core.Tests`/`Helpdesk.Infrastructure.Tests`
  shape: same SDK/package versions, registered in `Helpdesk.slnx` under
  `/tests/`), exercising `EmailIngestionService` against hand-written
  test doubles for `IMailClient`, `ITicketRepository`, and
  `IMessageRepository` (no Graph SDK, no real database):
  - New `conversationId` → new `Ticket` + first `Message` created.
  - Existing `conversationId` → `Message` appended to existing
    `Ticket`, `Status` unchanged, `UpdatedAt` bumped.
  - Duplicate `ExternalMessageId` → skipped, no second `Ticket`/`Message`.
  - One message throwing during mapping doesn't stop the batch.
- **Manual E2E test** (plan task 30) once the Entra app registration
  and shared mailbox are ready: send a test email to the mailbox,
  confirm a `Ticket` appears (e.g. via `GET /api/tickets` once that
  exists, or a direct DB check pre-Phase-7); reply to it from the same
  thread, confirm it appends to the same `Ticket` rather than creating
  a second one.

## Explicit decisions (from brainstorming)

- Separate Entra app registration for Graph app-only auth, not reused
  from the API's own `AzureAd` config.
- Client secret in `dotnet user-secrets`; other Graph config in
  `appsettings.Development.json`.
- Mapping/threading orchestration in `Helpdesk.Application`, not
  `Helpdesk.Infrastructure`.
- `Message.Body` stores raw HTML, no plain-text conversion.
- Default poll interval: 60 seconds, configurable.
- A reply to an already-`Replied` ticket does **not** reset its status.
