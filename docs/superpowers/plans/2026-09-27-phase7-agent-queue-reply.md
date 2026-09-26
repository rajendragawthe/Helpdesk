# Phase 7 — Agent Queue, Claim and Reply (Graph send) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agents can list tickets, claim one, review/edit the AI draft, and send it as a threaded Graph reply; the reply is stored on the thread and the ticket becomes `Replied`.

**Architecture:** Core gets `TicketFilter`, `IMailClient.SendReplyAsync` and new `ITicketRepository` methods (implemented in Infrastructure: EF queries, an atomic conditional-UPDATE claim, a single-SaveChanges reply record, and the Graph reply call). An Application `TicketWorkflowService` holds all rules and returns `TicketResult<T>` outcomes. A thin `TicketsController` maps outcomes to HTTP. The React app gets a queue page and a detail page. A `helpdesk_user_id` claim identifies the caller. Tasks 1-6 are independent of Phase 8; Task 7 rebases onto Phase 8's merged backend and adds its `reviewReasons` fields.

**Tech Stack:** .NET 10, ASP.NET Core controllers + FluentValidation, EF Core 10 (Npgsql), Microsoft Graph SDK 6.7, xunit, React 19 + TypeScript + Tailwind v4 + shadcn/ui, react-router, MSAL.

**Spec:** `docs/superpowers/specs/2026-09-27-phase7-agent-queue-reply-design.md`

## Global Constraints

- `Helpdesk.Core` has no EF Core / Npgsql / Graph SDK dependency; `Helpdesk.Application` depends only on `Helpdesk.Core`; controllers call Application only (never Infrastructure types).
- All `/api/tickets` endpoints use `[Authorize(Policy = "AgentOnly")]` (Admin or Agent). The caller is identified by the `helpdesk_user_id` claim (the `Users.Id`), the role claim (`Admin`), and the email claim (`preferred_username`/UPN).
- API contract (exact): `GET /api/tickets?filter=queue|mine|all` (default `queue`; `all` Admin only -> 403; other value -> 400); `GET /api/tickets/{id}`; `POST /api/tickets/{id}/claim`; `POST /api/tickets/{id}/release`; `PUT /api/tickets/{id}/reply` body `{ "text": "..." }`. Error bodies are `{ "message": "..." }` except FluentValidation's standard `ValidationProblemDetails` (400).
- Status codes: unknown ticket 404; claim held by someone else 409; release by a non-assignee non-Admin 403; reply by a non-assignee non-Admin 403; reply on an unassigned ticket 409; no replyable customer message (or defensive blank/oversize) 422; blank/whitespace or `> 10 000` char reply 400 from the validator; Graph send failure 502 (ticket unchanged); email sent but persistence failed 500 with a message saying the email WAS sent.
- Queue rules: `queue` = `Status != Replied` AND (unassigned OR assigned to me); `mine` = assigned to me (any status); `all` = everything. Newest first (`CreatedAt` desc).
- Claim is atomic: a single conditional `UPDATE ... WHERE Id = @id AND (AssignedUserId IS NULL OR AssignedUserId = @me)`; already-mine is idempotent success. Release: assignee or Admin; releasing an unassigned ticket is a no-op success. Reply: only the assignee or an Admin, and only on a claimed ticket; allowed from any status; the result status is always `Replied`; `DraftReply` is kept as the AI's original.
- Reply target: the ticket's most recent `IsFromUser` message with a non-empty `ExternalMessageId`. The stored outbound `Message`: `IsFromUser=false`, `Sender` = the sending agent's email, `Body` = the trimmed text sent, `ReceivedAt` = now, `ExternalMessageId` = null.
- Reply order: validate -> Graph send -> persist (`Message` + `Status=Replied` + `UpdatedAt` in ONE `SaveChanges`). If Graph fails nothing is persisted. If persistence fails after a successful send: log an error naming the ticket id and return the "email WAS sent" outcome.
- Customer content is only ever returned as plain text (`HtmlText.ToPlainText` for `IsFromUser` messages); agent-written bodies are returned as stored.
- No EF migration in this phase. Phase 8 owns `Ticket.ReviewReasons` (`[Flags]` int) and its migration; the DTO fields `reviewReasons` (int) and `needsReview` (bool) are added only in Task 7, after rebasing onto Phase 8's merged backend.
- Frontend: React + TypeScript + shadcn components already vendored in `src/components/ui` (prefer them), relative `/api/...` paths via `apiFetch`, no browser dialogs (`alert/confirm`), inline `Alert`s for errors. Checks are `npm run lint` and `npm run build` (run `npm ci` once in `client/helpdesk-web` of the worktree first).
- Follow existing test style: xunit, hand-written fakes, no mocking library.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`. Work stays on branch `phase7-agent-queue-reply` in worktree `D:/Rajendra/Claude/Learning/HELPDESK-phase7`; do not touch the main checkout or the Phase 8 worktree; do not merge or push.
- Never put secrets in the chat, repo files or memory. `dotnet ef`/`dotnet run` in this repo need `OpenRouter__Enabled=false` (and `GraphApi__Enabled=false` to avoid polling the real mailbox) in the environment unless the user's secrets are set.

## Review Focus

Failure modes the spec implies but no obvious test would catch; each has a test in the owning task unless noted:
1. Graph fails on send: nothing may be stored and the ticket must stay unchanged; the send succeeds but the save fails: the outcome must say the email WAS sent (Task 2).
2. Authorization matrix: non-assignee, unassigned ticket, Admin override, `filter=all` for a non-Admin, and a request whose principal lacks the `helpdesk_user_id` claim (Tasks 2 and 4).
3. Customer HTML is never returned raw: a `<script>` body must come back as inert plain text; agent messages are returned unmodified (Task 2).
4. Reply target selection: the latest customer message WITH an `ExternalMessageId`; agent messages and id-less customer messages are never targets; no eligible message -> 422 with nothing sent (Task 2).
5. Oversize/blank replies rejected at both the validator (400) and the service (defensive 422), including the 10 000 boundary (Tasks 2 and 4).
6. Named gaps (not unit-testable here): the SQL of the atomic claim and reply record (verified by the manual test and later Playwright E2E), and whether Graph preserves newlines in the reply `comment` (verified in the manual test; if lines collapse, switch to createReply + PATCH in a follow-up).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Helpdesk.Core/Enums/TicketFilter.cs` (new) | `Queue`, `Mine`, `All` |
| `src/Helpdesk.Core/Interfaces/IMailClient.cs`, `ITicketRepository.cs` (mod) | new contract members |
| `src/Helpdesk.Infrastructure/Repositories/TicketRepository.cs` (mod) | list/detail queries, atomic claim, release, reply record |
| `src/Helpdesk.Infrastructure/GraphApi/GraphMailClient.cs` (mod) | `SendReplyAsync` |
| `src/Helpdesk.Application/Tickets/TicketDtos.cs`, `TicketMapper.cs`, `ITicketWorkflowService.cs`, `TicketWorkflowService.cs` (new) | DTOs, mapping, rules |
| `src/Helpdesk.Application/DependencyInjection.cs` (mod) | register the service (unconditional) |
| `src/Helpdesk.Api/Auth/HelpdeskUserClaimsTransformation.cs`, `Controllers/AuthController.cs` (mod) | user-id claim; `/me` returns `id` |
| `src/Helpdesk.Api/Controllers/TicketsController.cs`, `Validators/ReplyRequestValidator.cs` (new) | HTTP surface |
| `tests/Helpdesk.Api.Tests/` (new project, added to `Helpdesk.slnx`) | controller/claims/validator tests |
| `client/helpdesk-web/src/api/tickets.ts`, `pages/QueuePage.tsx`, `pages/TicketDetailPage.tsx`, `components/NavBar.tsx`, `hooks/useCurrentUser.ts`, `App.tsx` | UI |
| `CLAUDE.md`, `implementation-plan.md` | docs |

---

### Task 1: Core contracts and their Infrastructure implementations

**Files:**
- Create: `src/Helpdesk.Core/Enums/TicketFilter.cs`
- Modify: `src/Helpdesk.Core/Interfaces/IMailClient.cs`, `src/Helpdesk.Core/Interfaces/ITicketRepository.cs`
- Modify: `src/Helpdesk.Infrastructure/Repositories/TicketRepository.cs`, `src/Helpdesk.Infrastructure/GraphApi/GraphMailClient.cs`
- Modify (test doubles): `tests/Helpdesk.Application.Tests/TestDoubles/FakeMailClient.cs`, `tests/Helpdesk.Application.Tests/TestDoubles/FakeTicketRepository.cs`
- Test: `tests/Helpdesk.Infrastructure.Tests/GraphApi/GraphMailClientTests.cs`

**Interfaces:**
- Consumes: existing `Ticket`, `Message`, `TicketStatus`, `HelpdeskDbContext` (`Tickets`, `Messages` DbSets), `GraphMailClient(GraphServiceClient, GraphApiOptions, ILogger<GraphMailClient>)`.
- Produces: `public enum TicketFilter { Queue, Mine, All }` (`Helpdesk.Core.Enums`); on `IMailClient`: `Task SendReplyAsync(string replyToExternalMessageId, string plainTextBody, CancellationToken cancellationToken = default)`; on `ITicketRepository`: `Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, Guid userId)`, `Task<Ticket?> GetDetailAsync(Guid id)`, `Task<bool> TryClaimAsync(Guid ticketId, Guid userId)`, `Task ReleaseAsync(Guid ticketId)`, `Task RecordReplyAsync(Guid ticketId, Message reply)`. Fakes: `FakeMailClient.SentReplies` (`List<(string ReplyToExternalMessageId, string Text)>`), `SendAttempts` (int), `SendException`; `FakeTicketRepository.Users` (`List<User>`), `RecordReplyException`.

- [ ] **Step 1: Write the failing Graph client test**

`tests/Helpdesk.Infrastructure.Tests/GraphApi/GraphMailClientTests.cs`:
```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Infrastructure.GraphApi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;

namespace Helpdesk.Infrastructure.Tests.GraphApi;

public class GraphMailClientTests
{
    private const string Mailbox = "support@contoso.com";

    private sealed class CapturingHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (GraphMailClient Client, CapturingHandler Handler) Create(HttpStatusCode status, string body = "")
    {
        var handler = new CapturingHandler(status, body);
        var graph = new GraphServiceClient(new HttpClient(handler));
        var options = new GraphApiOptions("tenant", "client", "secret", Mailbox);
        return (new GraphMailClient(graph, options, NullLogger<GraphMailClient>.Instance), handler);
    }

    [Fact]
    public async Task SendReplyAsync_PostsAReplyWithTheTextToTheMailboxMessage()
    {
        var (client, handler) = Create(HttpStatusCode.Accepted);

        await client.SendReplyAsync("AAMkAD-123", "Hello,\nWe are on it.");

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        var path = Uri.UnescapeDataString(handler.Request.RequestUri!.AbsolutePath);
        Assert.EndsWith($"/users/{Mailbox}/messages/AAMkAD-123/reply", path);

        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var comment = doc.RootElement.EnumerateObject()
            .Single(p => string.Equals(p.Name, "comment", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Hello,\nWe are on it.", comment.Value.GetString());
    }

    [Fact]
    public async Task SendReplyAsync_GraphErrorResponse_Throws()
    {
        var (client, _) = Create(HttpStatusCode.TooManyRequests,
            """{"error":{"code":"ApplicationThrottled","message":"slow down"}}""");

        await Assert.ThrowsAnyAsync<Exception>(() => client.SendReplyAsync("AAMkAD-123", "Hi"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Helpdesk.Infrastructure.Tests/Helpdesk.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GraphMailClientTests"`
Expected: build FAIL — `SendReplyAsync` does not exist. (If `new GraphServiceClient(HttpClient)` does not compile with this SDK version, report BLOCKED with the compiler error rather than changing the approach.)

- [ ] **Step 3: Add the Core contracts**

`src/Helpdesk.Core/Enums/TicketFilter.cs`:
```csharp
namespace Helpdesk.Core.Enums;

/// <summary>Which tickets the agent queue lists. Queue = open tickets that are unassigned or mine.</summary>
public enum TicketFilter
{
    Queue,
    Mine,
    All,
}
```
Add to `IMailClient` (inside the interface):
```csharp
    /// <summary>
    /// Sends a plain-text reply on the mail thread of the given inbound message (Graph reply), so it threads
    /// with the customer's email. Throws if the provider rejects or cannot be reached.
    /// </summary>
    Task SendReplyAsync(string replyToExternalMessageId, string plainTextBody, CancellationToken cancellationToken = default);
```
Add to `ITicketRepository` (add `using Helpdesk.Core.Enums;`; keep existing members):
```csharp
    /// <summary>Tickets for the queue views, newest first, with Classification and AssignedUser loaded (no tracking).</summary>
    Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, Guid userId);

    /// <summary>One ticket with Messages, Classification and AssignedUser loaded (no tracking), or null.</summary>
    Task<Ticket?> GetDetailAsync(Guid id);

    /// <summary>
    /// Atomically assigns the ticket to the user if it is unassigned or already theirs. Returns false when it
    /// does not exist or is assigned to someone else.
    /// </summary>
    Task<bool> TryClaimAsync(Guid ticketId, Guid userId);

    /// <summary>Clears the assignee.</summary>
    Task ReleaseAsync(Guid ticketId);

    /// <summary>Stores the outbound reply message and marks the ticket Replied, in one save.</summary>
    Task RecordReplyAsync(Guid ticketId, Message reply);
```

- [ ] **Step 4: Implement the repository and Graph client**

In `src/Helpdesk.Infrastructure/Repositories/TicketRepository.cs` add `using Helpdesk.Core.Enums;` and these members to the class:
```csharp
    public async Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, Guid userId)
    {
        var query = dbContext.Tickets
            .AsNoTracking()
            .Include(t => t.Classification)
            .Include(t => t.AssignedUser)
            .AsQueryable();

        query = filter switch
        {
            TicketFilter.Queue => query.Where(t =>
                t.Status != TicketStatus.Replied && (t.AssignedUserId == null || t.AssignedUserId == userId)),
            TicketFilter.Mine => query.Where(t => t.AssignedUserId == userId),
            _ => query,
        };

        return await query.OrderByDescending(t => t.CreatedAt).ToListAsync();
    }

    public async Task<Ticket?> GetDetailAsync(Guid id)
    {
        return await dbContext.Tickets
            .AsNoTracking()
            .Include(t => t.Messages)
            .Include(t => t.Classification)
            .Include(t => t.AssignedUser)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<bool> TryClaimAsync(Guid ticketId, Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await dbContext.Tickets
            .Where(t => t.Id == ticketId && (t.AssignedUserId == null || t.AssignedUserId == userId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.AssignedUserId, (Guid?)userId)
                .SetProperty(t => t.UpdatedAt, now));
        return rows > 0;
    }

    public async Task ReleaseAsync(Guid ticketId)
    {
        var now = DateTimeOffset.UtcNow;
        await dbContext.Tickets
            .Where(t => t.Id == ticketId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.AssignedUserId, (Guid?)null)
                .SetProperty(t => t.UpdatedAt, now));
    }

    public async Task RecordReplyAsync(Guid ticketId, Message reply)
    {
        var ticket = await dbContext.Tickets.FirstAsync(t => t.Id == ticketId);
        ticket.Status = TicketStatus.Replied;
        ticket.UpdatedAt = reply.ReceivedAt;
        reply.TicketId = ticketId;
        dbContext.Messages.Add(reply);
        await dbContext.SaveChangesAsync();
    }
```
(`using Helpdesk.Core.Entities;` is already imported there; `TicketStatus` comes from `Helpdesk.Core.Enums`.)

In `src/Helpdesk.Infrastructure/GraphApi/GraphMailClient.cs` add `using Microsoft.Graph.Users.Item.Messages.Item.Reply;` and this method:
```csharp
    public async Task SendReplyAsync(string replyToExternalMessageId, string plainTextBody, CancellationToken cancellationToken = default)
    {
        await graphClient.Users[options.MailboxAddress]
            .Messages[replyToExternalMessageId]
            .Reply
            .PostAsync(new ReplyPostRequestBody { Comment = plainTextBody }, cancellationToken: cancellationToken);
    }
```

- [ ] **Step 5: Update the test doubles so the solution builds**

In `tests/Helpdesk.Application.Tests/TestDoubles/FakeMailClient.cs` add (keep existing members):
```csharp
    public List<(string ReplyToExternalMessageId, string Text)> SentReplies { get; } = [];
    public int SendAttempts { get; private set; }
    public Exception? SendException { get; set; }

    public Task SendReplyAsync(string replyToExternalMessageId, string plainTextBody, CancellationToken cancellationToken = default)
    {
        SendAttempts++;
        if (SendException is not null)
        {
            throw SendException;
        }

        SentReplies.Add((replyToExternalMessageId, plainTextBody));
        return Task.CompletedTask;
    }
```
In `tests/Helpdesk.Application.Tests/TestDoubles/FakeTicketRepository.cs` add `using Helpdesk.Core.Enums;` and APPEND these members at the end of the class (do not modify the existing methods; another phase edits this file too):
```csharp
    /// <summary>Users the fake can resolve as assignees (mirrors the AssignedUser include of the real repository).</summary>
    public List<User> Users { get; } = [];

    public Exception? RecordReplyException { get; set; }

    public Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, Guid userId)
    {
        IEnumerable<Ticket> query = Tickets;
        query = filter switch
        {
            TicketFilter.Queue => query.Where(t =>
                t.Status != TicketStatus.Replied && (t.AssignedUserId == null || t.AssignedUserId == userId)),
            TicketFilter.Mine => query.Where(t => t.AssignedUserId == userId),
            _ => query,
        };
        return Task.FromResult<IReadOnlyList<Ticket>>(query.OrderByDescending(t => t.CreatedAt).ToList());
    }

    public Task<Ticket?> GetDetailAsync(Guid id)
    {
        return Task.FromResult(Tickets.FirstOrDefault(t => t.Id == id));
    }

    public Task<bool> TryClaimAsync(Guid ticketId, Guid userId)
    {
        var ticket = Tickets.FirstOrDefault(t => t.Id == ticketId);
        if (ticket is null || (ticket.AssignedUserId is not null && ticket.AssignedUserId != userId))
        {
            return Task.FromResult(false);
        }

        ticket.AssignedUserId = userId;
        ticket.AssignedUser = Users.FirstOrDefault(u => u.Id == userId);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(true);
    }

    public Task ReleaseAsync(Guid ticketId)
    {
        var ticket = Tickets.FirstOrDefault(t => t.Id == ticketId);
        if (ticket is not null)
        {
            ticket.AssignedUserId = null;
            ticket.AssignedUser = null;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task RecordReplyAsync(Guid ticketId, Message reply)
    {
        if (RecordReplyException is not null)
        {
            throw RecordReplyException;
        }

        var ticket = Tickets.First(t => t.Id == ticketId);
        reply.TicketId = ticketId;
        ticket.Messages.Add(reply);
        ticket.Status = TicketStatus.Replied;
        ticket.UpdatedAt = reply.ReceivedAt;
        return Task.CompletedTask;
    }
```

- [ ] **Step 6: Run tests and build**

Run: `dotnet build Helpdesk.slnx` then `dotnet test Helpdesk.slnx`
Expected: 0 errors; all tests pass, including the 2 new Graph tests. (The repository queries are not unit-testable without a database: they are covered by the manual test and later E2E; say so in the report.)

- [ ] **Step 7: Commit**

```bash
git add src/Helpdesk.Core src/Helpdesk.Infrastructure tests
git commit -m "feat: add ticket queue repository methods and Graph reply send

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: `TicketWorkflowService` (Application)

**Files:**
- Create: `src/Helpdesk.Application/Tickets/TicketDtos.cs`, `TicketMapper.cs`, `ITicketWorkflowService.cs`, `TicketWorkflowService.cs`
- Modify: `src/Helpdesk.Application/DependencyInjection.cs`
- Test: `tests/Helpdesk.Application.Tests/Tickets/TicketWorkflowServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 contracts and fakes; existing `HtmlText.ToPlainText(string, int = 8000)` in `Helpdesk.Application.Classification`.
- Produces (all in `Helpdesk.Application.Tickets`): `TicketCaller(Guid UserId, string Email, bool IsAdmin)`; `TicketAssignee(Guid Id, string DisplayName)`; `TicketListItem(Guid Id, string Subject, string RequesterEmail, TicketStatus Status, string? Category, string? Summary, double? Confidence, TicketAssignee? Assignee, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool HasDraft)`; `TicketMessageDto(Guid Id, string Sender, bool IsFromUser, DateTimeOffset ReceivedAt, string BodyText)`; `TicketDetail(<all TicketListItem fields>, string? DraftReply, IReadOnlyList<TicketMessageDto> Messages)`; `enum TicketOutcome { Success, NotFound, Forbidden, Conflict, Invalid, SendFailed, SentButNotSaved }`; `TicketResult<T>(TicketOutcome Outcome, T? Value = default, string? Message = null)` with `IsSuccess`, `static Ok(T)`, `static Fail(TicketOutcome, string)`; `interface ITicketWorkflowService` with `ListAsync(TicketCaller, TicketFilter)`, `GetAsync(Guid)`, `ClaimAsync(TicketCaller, Guid)`, `ReleaseAsync(TicketCaller, Guid)`, `SendReplyAsync(TicketCaller, Guid, string text, CancellationToken = default)`; `public const int MaxReplyLength = 10_000` on `TicketWorkflowService`. The service is registered scoped and unconditionally; its `IMailClient? mailClient = null` constructor parameter defaults to null when Graph is not configured (then a send returns `SendFailed`).

- [ ] **Step 1: Write the failing tests**

`tests/Helpdesk.Application.Tests/Tickets/TicketWorkflowServiceTests.cs`:
```csharp
using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Application.Tickets;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using ClassificationEntity = Helpdesk.Core.Entities.Classification;

namespace Helpdesk.Application.Tests.Tickets;

public class TicketWorkflowServiceTests
{
    private static readonly Guid AliceId = Guid.NewGuid();
    private static readonly Guid BobId = Guid.NewGuid();

    private readonly FakeTicketRepository _tickets = new();
    private readonly FakeMailClient _mail = new();
    private readonly User _alice = new() { Id = AliceId, Email = "alice@example.com", DisplayName = "Alice", Role = Role.Agent };
    private readonly User _bob = new() { Id = BobId, Email = "bob@example.com", DisplayName = "Bob", Role = Role.Agent };

    public TicketWorkflowServiceTests()
    {
        _tickets.Users.AddRange([_alice, _bob]);
    }

    private TicketWorkflowService Create(bool withMailClient = true) =>
        new(_tickets, NullLogger<TicketWorkflowService>.Instance, withMailClient ? _mail : null);

    private static TicketCaller Agent(Guid id, string email) => new(id, email, IsAdmin: false);
    private static TicketCaller AliceCaller => Agent(AliceId, "alice@example.com");
    private static TicketCaller BobCaller => Agent(BobId, "bob@example.com");
    private static TicketCaller AdminCaller => new(Guid.NewGuid(), "admin@example.com", IsAdmin: true);

    private static Message Customer(string body, DateTimeOffset at, string? externalId = "ext-1") => new()
    {
        Id = Guid.NewGuid(),
        Sender = "customer@example.com",
        Body = body,
        IsFromUser = true,
        ExternalMessageId = externalId,
        ReceivedAt = at,
    };

    private static Message AgentMessage(string body, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        Sender = "alice@example.com",
        Body = body,
        IsFromUser = false,
        ExternalMessageId = "agent-ext",
        ReceivedAt = at,
    };

    private Ticket AddTicket(
        TicketStatus status = TicketStatus.InReview,
        User? assignedTo = null,
        string? draft = "AI draft",
        DateTimeOffset? createdAt = null,
        params Message[] messages)
    {
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Charged twice",
            RequesterEmail = "customer@example.com",
            Status = status,
            DraftReply = draft,
            AssignedUserId = assignedTo?.Id,
            AssignedUser = assignedTo,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        ticket.Classification = new ClassificationEntity
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            Category = "Billing",
            Summary = "Double charge.",
            Confidence = 0.9,
        };
        foreach (var message in messages)
        {
            message.TicketId = ticket.Id;
            ticket.Messages.Add(message);
        }

        _tickets.Tickets.Add(ticket);
        return ticket;
    }

    private Ticket AddReplyableTicket(User? assignedTo, TicketStatus status = TicketStatus.InReview) =>
        AddTicket(status, assignedTo, "AI draft", null, Customer("<p>Please help</p>", DateTimeOffset.UtcNow.AddMinutes(-30)));

    // ---- List ----

    [Fact]
    public async Task ListAsync_Queue_ShowsUnassignedAndMineButNotOthersOrReplied()
    {
        var unassigned = AddTicket();
        var mine = AddTicket(assignedTo: _alice);
        AddTicket(assignedTo: _bob);
        AddTicket(status: TicketStatus.Replied);

        var result = await Create().ListAsync(AliceCaller, TicketFilter.Queue);

        Assert.True(result.IsSuccess);
        Assert.Equivalent(new[] { unassigned.Id, mine.Id }, result.Value!.Select(t => t.Id));
    }

    [Fact]
    public async Task ListAsync_Mine_ShowsOnlyMyTicketsIncludingReplied()
    {
        var open = AddTicket(assignedTo: _alice);
        var done = AddTicket(status: TicketStatus.Replied, assignedTo: _alice);
        AddTicket(assignedTo: _bob);
        AddTicket();

        var result = await Create().ListAsync(AliceCaller, TicketFilter.Mine);

        Assert.Equivalent(new[] { open.Id, done.Id }, result.Value!.Select(t => t.Id));
    }

    [Fact]
    public async Task ListAsync_All_AdminSeesEverything()
    {
        AddTicket();
        AddTicket(assignedTo: _bob);
        AddTicket(status: TicketStatus.Replied);

        var result = await Create().ListAsync(AdminCaller, TicketFilter.All);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.Count);
    }

    [Fact]
    public async Task ListAsync_All_NonAdminIsForbidden()
    {
        AddTicket();

        var result = await Create().ListAsync(AliceCaller, TicketFilter.All);

        Assert.Equal(TicketOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task ListAsync_MapsRowFields()
    {
        var ticket = AddTicket(assignedTo: _alice, draft: "AI draft");

        var row = Assert.Single((await Create().ListAsync(AliceCaller, TicketFilter.Mine)).Value!);

        Assert.Equal(ticket.Id, row.Id);
        Assert.Equal("Charged twice", row.Subject);
        Assert.Equal("customer@example.com", row.RequesterEmail);
        Assert.Equal(TicketStatus.InReview, row.Status);
        Assert.Equal("Billing", row.Category);
        Assert.Equal("Double charge.", row.Summary);
        Assert.Equal(0.9, row.Confidence);
        Assert.Equal(new TicketAssignee(AliceId, "Alice"), row.Assignee);
        Assert.True(row.HasDraft);
    }

    [Fact]
    public async Task ListAsync_UnclassifiedUndraftedTicket_HasNullsAndNoDraft()
    {
        var ticket = AddTicket(draft: null);
        ticket.Classification = null;

        var row = Assert.Single((await Create().ListAsync(AliceCaller, TicketFilter.Queue)).Value!);

        Assert.Null(row.Category);
        Assert.Null(row.Summary);
        Assert.Null(row.Confidence);
        Assert.Null(row.Assignee);
        Assert.False(row.HasDraft);
    }

    // ---- Get ----

    [Fact]
    public async Task GetAsync_UnknownTicket_IsNotFound()
    {
        var result = await Create().GetAsync(Guid.NewGuid());

        Assert.Equal(TicketOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetAsync_CustomerHtmlIsReturnedAsInertPlainText_AndAgentTextIsUnmodified()
    {
        var ticket = AddTicket(messages:
        [
            Customer("<p>Hello <b>there</b></p><script>alert('x')</script>", DateTimeOffset.UtcNow.AddMinutes(-20)),
            AgentMessage("Use a < b and <tag> literally.", DateTimeOffset.UtcNow.AddMinutes(-10)),
        ]);

        var detail = (await Create().GetAsync(ticket.Id)).Value!;

        Assert.Equal("Hello there", detail.Messages[0].BodyText);
        Assert.DoesNotContain("script", detail.Messages[0].BodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", detail.Messages[0].BodyText);
        Assert.Equal("Use a < b and <tag> literally.", detail.Messages[1].BodyText);
    }

    [Fact]
    public async Task GetAsync_MessagesAreOldestFirst_AndCarryTheDraft()
    {
        var newer = Customer("second", DateTimeOffset.UtcNow.AddMinutes(-5), "ext-2");
        var older = Customer("first", DateTimeOffset.UtcNow.AddMinutes(-15), "ext-1");
        var ticket = AddTicket(draft: "AI draft", messages: [newer, older]);

        var detail = (await Create().GetAsync(ticket.Id)).Value!;

        Assert.Equal(["first", "second"], detail.Messages.Select(m => m.BodyText));
        Assert.Equal("AI draft", detail.DraftReply);
    }

    // ---- Claim ----

    [Fact]
    public async Task ClaimAsync_UnassignedTicket_AssignsToTheCaller()
    {
        var ticket = AddTicket();

        var result = await Create().ClaimAsync(AliceCaller, ticket.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(AliceId, result.Value!.Assignee!.Id);
        Assert.Equal(AliceId, ticket.AssignedUserId);
    }

    [Fact]
    public async Task ClaimAsync_AlreadyMine_IsAnIdempotentSuccess()
    {
        var ticket = AddTicket(assignedTo: _alice);

        var result = await Create().ClaimAsync(AliceCaller, ticket.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(AliceId, ticket.AssignedUserId);
    }

    [Fact]
    public async Task ClaimAsync_HeldBySomeoneElse_IsAConflictNamingTheHolder()
    {
        var ticket = AddTicket(assignedTo: _bob);

        var result = await Create().ClaimAsync(AliceCaller, ticket.Id);

        Assert.Equal(TicketOutcome.Conflict, result.Outcome);
        Assert.Contains("Bob", result.Message);
        Assert.Equal(BobId, ticket.AssignedUserId);
    }

    [Fact]
    public async Task ClaimAsync_UnknownTicket_IsNotFound()
    {
        var result = await Create().ClaimAsync(AliceCaller, Guid.NewGuid());

        Assert.Equal(TicketOutcome.NotFound, result.Outcome);
    }

    // ---- Release ----

    [Fact]
    public async Task ReleaseAsync_ByTheAssignee_ClearsTheAssignee()
    {
        var ticket = AddTicket(assignedTo: _alice);

        var result = await Create().ReleaseAsync(AliceCaller, ticket.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(ticket.AssignedUserId);
        Assert.Null(result.Value!.Assignee);
    }

    [Fact]
    public async Task ReleaseAsync_ByAnAdmin_ReleasesSomeoneElsesTicket()
    {
        var ticket = AddTicket(assignedTo: _bob);

        var result = await Create().ReleaseAsync(AdminCaller, ticket.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(ticket.AssignedUserId);
    }

    [Fact]
    public async Task ReleaseAsync_ByAnotherAgent_IsForbiddenAndChangesNothing()
    {
        var ticket = AddTicket(assignedTo: _bob);

        var result = await Create().ReleaseAsync(AliceCaller, ticket.Id);

        Assert.Equal(TicketOutcome.Forbidden, result.Outcome);
        Assert.Equal(BobId, ticket.AssignedUserId);
    }

    [Fact]
    public async Task ReleaseAsync_UnassignedTicket_IsANoOpSuccess()
    {
        var ticket = AddTicket();

        var result = await Create().ReleaseAsync(AliceCaller, ticket.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(ticket.AssignedUserId);
    }

    [Fact]
    public async Task ReleaseAsync_UnknownTicket_IsNotFound()
    {
        var result = await Create().ReleaseAsync(AliceCaller, Guid.NewGuid());

        Assert.Equal(TicketOutcome.NotFound, result.Outcome);
    }

    // ---- Reply ----

    [Fact]
    public async Task SendReplyAsync_ByTheAssignee_SendsStoresAndMarksReplied()
    {
        var ticket = AddReplyableTicket(_alice);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "  Hello,\nWe fixed it.  ");

        Assert.True(result.IsSuccess);
        var (replyTo, text) = Assert.Single(_mail.SentReplies);
        Assert.Equal("ext-1", replyTo);
        Assert.Equal("Hello,\nWe fixed it.", text);

        Assert.Equal(TicketStatus.Replied, ticket.Status);
        Assert.Equal("AI draft", ticket.DraftReply);
        var stored = ticket.Messages.Single(m => !m.IsFromUser);
        Assert.Equal("Hello,\nWe fixed it.", stored.Body);
        Assert.Equal("alice@example.com", stored.Sender);
        Assert.Null(stored.ExternalMessageId);
        Assert.Equal(TicketStatus.Replied, result.Value!.Status);
        Assert.Equal(2, result.Value.Messages.Count);
    }

    [Fact]
    public async Task SendReplyAsync_RepliesToTheLatestCustomerMessageThatHasAnExternalId()
    {
        var ticket = AddTicket(assignedTo: _alice, messages:
        [
            Customer("old", DateTimeOffset.UtcNow.AddHours(-3), "ext-old"),
            Customer("newest but no id", DateTimeOffset.UtcNow.AddMinutes(-5), externalId: null),
            AgentMessage("earlier agent reply", DateTimeOffset.UtcNow.AddMinutes(-2)),
            Customer("newer with id", DateTimeOffset.UtcNow.AddHours(-1), "ext-new"),
        ]);

        await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal("ext-new", Assert.Single(_mail.SentReplies).ReplyToExternalMessageId);
    }

    [Fact]
    public async Task SendReplyAsync_NoEligibleCustomerMessage_IsInvalidAndSendsNothing()
    {
        var ticket = AddTicket(assignedTo: _alice, messages:
        [
            AgentMessage("only an agent message", DateTimeOffset.UtcNow.AddMinutes(-10)),
            Customer("no id", DateTimeOffset.UtcNow.AddMinutes(-20), externalId: null),
        ]);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.Invalid, result.Outcome);
        Assert.Equal(0, _mail.SendAttempts);
        Assert.Equal(TicketStatus.InReview, ticket.Status);
    }

    [Fact]
    public async Task SendReplyAsync_UnknownTicket_IsNotFound()
    {
        var result = await Create().SendReplyAsync(AliceCaller, Guid.NewGuid(), "Reply");

        Assert.Equal(TicketOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task SendReplyAsync_UnassignedTicket_IsAConflictAndSendsNothing()
    {
        var ticket = AddReplyableTicket(assignedTo: null);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.Conflict, result.Outcome);
        Assert.Equal(0, _mail.SendAttempts);
    }

    [Fact]
    public async Task SendReplyAsync_ByAnotherAgent_IsForbiddenAndSendsNothing()
    {
        var ticket = AddReplyableTicket(_bob);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.Forbidden, result.Outcome);
        Assert.Equal(0, _mail.SendAttempts);
        Assert.Equal(TicketStatus.InReview, ticket.Status);
    }

    [Fact]
    public async Task SendReplyAsync_ByAnAdmin_CanReplyOnSomeoneElsesClaimedTicket()
    {
        var ticket = AddReplyableTicket(_bob);

        var result = await Create().SendReplyAsync(AdminCaller, ticket.Id, "Reply");

        Assert.True(result.IsSuccess);
        Assert.Equal("admin@example.com", ticket.Messages.Single(m => !m.IsFromUser).Sender);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task SendReplyAsync_BlankText_IsInvalidAndSendsNothing(string text)
    {
        var ticket = AddReplyableTicket(_alice);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, text);

        Assert.Equal(TicketOutcome.Invalid, result.Outcome);
        Assert.Equal(0, _mail.SendAttempts);
    }

    [Fact]
    public async Task SendReplyAsync_TextAtTheLimit_IsSent_AndOneOverIsInvalid()
    {
        var ticket = AddReplyableTicket(_alice);
        var service = Create();

        var over = await service.SendReplyAsync(AliceCaller, ticket.Id, new string('x', TicketWorkflowService.MaxReplyLength + 1));
        Assert.Equal(TicketOutcome.Invalid, over.Outcome);
        Assert.Equal(0, _mail.SendAttempts);

        var atLimit = await service.SendReplyAsync(AliceCaller, ticket.Id, new string('x', TicketWorkflowService.MaxReplyLength));
        Assert.True(atLimit.IsSuccess);
    }

    [Fact]
    public async Task SendReplyAsync_GraphFails_IsSendFailedAndNothingIsStored()
    {
        var ticket = AddReplyableTicket(_alice);
        _mail.SendException = new HttpRequestException("graph down");

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.SendFailed, result.Outcome);
        Assert.Equal(1, _mail.SendAttempts);
        Assert.Equal(TicketStatus.InReview, ticket.Status);
        Assert.Single(ticket.Messages);
    }

    [Fact]
    public async Task SendReplyAsync_SentButPersistenceFails_SaysTheEmailWasSent()
    {
        var ticket = AddReplyableTicket(_alice);
        _tickets.RecordReplyException = new InvalidOperationException("db down");

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.SentButNotSaved, result.Outcome);
        Assert.Contains("sent", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_mail.SentReplies);
    }

    [Fact]
    public async Task SendReplyAsync_NoMailClientConfigured_IsSendFailed()
    {
        var ticket = AddReplyableTicket(_alice);

        var result = await Create(withMailClient: false).SendReplyAsync(AliceCaller, ticket.Id, "Reply");

        Assert.Equal(TicketOutcome.SendFailed, result.Outcome);
        Assert.Equal(TicketStatus.InReview, ticket.Status);
    }

    [Fact]
    public async Task SendReplyAsync_AllowedOnARepliedTicket_AsAFollowUp()
    {
        var ticket = AddReplyableTicket(_alice, TicketStatus.Replied);

        var result = await Create().SendReplyAsync(AliceCaller, ticket.Id, "Follow-up");

        Assert.True(result.IsSuccess);
        Assert.Equal(TicketStatus.Replied, ticket.Status);
        Assert.Single(_mail.SentReplies);
    }

    [Fact]
    public async Task SendReplyAsync_CancelledWhileTokenCancelled_Propagates()
    {
        var ticket = AddReplyableTicket(_alice);
        _mail.SendException = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Create().SendReplyAsync(AliceCaller, ticket.Id, "Reply", cts.Token));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~TicketWorkflowServiceTests"`
Expected: build FAIL — the `Helpdesk.Application.Tickets` types do not exist.

- [ ] **Step 3: Implement the DTOs, mapper and service**

`src/Helpdesk.Application/Tickets/TicketDtos.cs`:
```csharp
using Helpdesk.Core.Enums;

namespace Helpdesk.Application.Tickets;

/// <summary>The authenticated agent making a request.</summary>
public record TicketCaller(Guid UserId, string Email, bool IsAdmin);

public record TicketAssignee(Guid Id, string DisplayName);

public record TicketListItem(
    Guid Id,
    string Subject,
    string RequesterEmail,
    TicketStatus Status,
    string? Category,
    string? Summary,
    double? Confidence,
    TicketAssignee? Assignee,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool HasDraft);

public record TicketMessageDto(Guid Id, string Sender, bool IsFromUser, DateTimeOffset ReceivedAt, string BodyText);

public record TicketDetail(
    Guid Id,
    string Subject,
    string RequesterEmail,
    TicketStatus Status,
    string? Category,
    string? Summary,
    double? Confidence,
    TicketAssignee? Assignee,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool HasDraft,
    string? DraftReply,
    IReadOnlyList<TicketMessageDto> Messages);

public enum TicketOutcome
{
    Success,
    NotFound,
    Forbidden,
    Conflict,
    Invalid,
    SendFailed,
    SentButNotSaved,
}

public record TicketResult<T>(TicketOutcome Outcome, T? Value = default, string? Message = null)
{
    public bool IsSuccess => Outcome == TicketOutcome.Success;

    public static TicketResult<T> Ok(T value) => new(TicketOutcome.Success, value);

    public static TicketResult<T> Fail(TicketOutcome outcome, string message) => new(outcome, default, message);
}
```
`src/Helpdesk.Application/Tickets/TicketMapper.cs`:
```csharp
using Helpdesk.Application.Classification;
using Helpdesk.Core.Entities;

namespace Helpdesk.Application.Tickets;

internal static class TicketMapper
{
    public static TicketListItem ToListItem(Ticket ticket) => new(
        ticket.Id,
        ticket.Subject,
        ticket.RequesterEmail,
        ticket.Status,
        ticket.Classification?.Category,
        ticket.Classification?.Summary,
        ticket.Classification?.Confidence,
        ToAssignee(ticket),
        ticket.CreatedAt,
        ticket.UpdatedAt,
        !string.IsNullOrWhiteSpace(ticket.DraftReply));

    public static TicketDetail ToDetail(Ticket ticket) => new(
        ticket.Id,
        ticket.Subject,
        ticket.RequesterEmail,
        ticket.Status,
        ticket.Classification?.Category,
        ticket.Classification?.Summary,
        ticket.Classification?.Confidence,
        ToAssignee(ticket),
        ticket.CreatedAt,
        ticket.UpdatedAt,
        !string.IsNullOrWhiteSpace(ticket.DraftReply),
        ticket.DraftReply,
        ticket.Messages.OrderBy(m => m.ReceivedAt).Select(ToMessage).ToList());

    // Customer email bodies are arbitrary HTML: only ever return them as plain text. Agent replies are
    // plain text we stored ourselves, so they are returned as written.
    private static TicketMessageDto ToMessage(Message message) => new(
        message.Id,
        message.Sender,
        message.IsFromUser,
        message.ReceivedAt,
        message.IsFromUser ? HtmlText.ToPlainText(message.Body) : message.Body);

    private static TicketAssignee? ToAssignee(Ticket ticket) =>
        ticket.AssignedUser is null ? null : new TicketAssignee(ticket.AssignedUser.Id, ticket.AssignedUser.DisplayName);
}
```
`src/Helpdesk.Application/Tickets/ITicketWorkflowService.cs`:
```csharp
using Helpdesk.Core.Enums;

namespace Helpdesk.Application.Tickets;

public interface ITicketWorkflowService
{
    Task<TicketResult<IReadOnlyList<TicketListItem>>> ListAsync(TicketCaller caller, TicketFilter filter);

    Task<TicketResult<TicketDetail>> GetAsync(Guid ticketId);

    Task<TicketResult<TicketDetail>> ClaimAsync(TicketCaller caller, Guid ticketId);

    Task<TicketResult<TicketDetail>> ReleaseAsync(TicketCaller caller, Guid ticketId);

    /// <summary>
    /// Sends the agent's reply through the mail client, then stores it and marks the ticket Replied. Only the
    /// assignee (or an Admin) may reply, and only on a claimed ticket. Only cancellation propagates; every
    /// other failure is a result outcome.
    /// </summary>
    Task<TicketResult<TicketDetail>> SendReplyAsync(
        TicketCaller caller, Guid ticketId, string text, CancellationToken cancellationToken = default);
}
```
`src/Helpdesk.Application/Tickets/TicketWorkflowService.cs`:
```csharp
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Tickets;

public class TicketWorkflowService(
    ITicketRepository ticketRepository,
    ILogger<TicketWorkflowService> logger,
    IMailClient? mailClient = null) : ITicketWorkflowService
{
    public const int MaxReplyLength = 10_000;

    public async Task<TicketResult<IReadOnlyList<TicketListItem>>> ListAsync(TicketCaller caller, TicketFilter filter)
    {
        if (filter == TicketFilter.All && !caller.IsAdmin)
        {
            return TicketResult<IReadOnlyList<TicketListItem>>.Fail(
                TicketOutcome.Forbidden, "Only admins can list all tickets.");
        }

        var tickets = await ticketRepository.ListAsync(filter, caller.UserId);
        return TicketResult<IReadOnlyList<TicketListItem>>.Ok(tickets.Select(TicketMapper.ToListItem).ToList());
    }

    public async Task<TicketResult<TicketDetail>> GetAsync(Guid ticketId)
    {
        var ticket = await ticketRepository.GetDetailAsync(ticketId);
        return ticket is null ? NotFound() : TicketResult<TicketDetail>.Ok(TicketMapper.ToDetail(ticket));
    }

    public async Task<TicketResult<TicketDetail>> ClaimAsync(TicketCaller caller, Guid ticketId)
    {
        var existing = await ticketRepository.GetDetailAsync(ticketId);
        if (existing is null)
        {
            return NotFound();
        }

        if (!await ticketRepository.TryClaimAsync(ticketId, caller.UserId))
        {
            var holder = existing.AssignedUser?.DisplayName ?? "another agent";
            return TicketResult<TicketDetail>.Fail(TicketOutcome.Conflict, $"This ticket is already assigned to {holder}.");
        }

        return await GetAsync(ticketId);
    }

    public async Task<TicketResult<TicketDetail>> ReleaseAsync(TicketCaller caller, Guid ticketId)
    {
        var ticket = await ticketRepository.GetDetailAsync(ticketId);
        if (ticket is null)
        {
            return NotFound();
        }

        if (ticket.AssignedUserId is null)
        {
            return TicketResult<TicketDetail>.Ok(TicketMapper.ToDetail(ticket));
        }

        if (ticket.AssignedUserId != caller.UserId && !caller.IsAdmin)
        {
            return TicketResult<TicketDetail>.Fail(
                TicketOutcome.Forbidden, "Only the assignee or an admin can release this ticket.");
        }

        await ticketRepository.ReleaseAsync(ticketId);
        return await GetAsync(ticketId);
    }

    public async Task<TicketResult<TicketDetail>> SendReplyAsync(
        TicketCaller caller, Guid ticketId, string text, CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId);
        if (ticket is null)
        {
            return NotFound();
        }

        if (ticket.AssignedUserId is null)
        {
            return TicketResult<TicketDetail>.Fail(TicketOutcome.Conflict, "Claim this ticket before replying.");
        }

        if (ticket.AssignedUserId != caller.UserId && !caller.IsAdmin)
        {
            return TicketResult<TicketDetail>.Fail(
                TicketOutcome.Forbidden, "Only the assigned agent (or an admin) can reply to this ticket.");
        }

        var body = text?.Trim();
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxReplyLength)
        {
            return TicketResult<TicketDetail>.Fail(
                TicketOutcome.Invalid, $"The reply must not be blank and must be at most {MaxReplyLength} characters.");
        }

        var target = ticket.Messages
            .Where(m => m.IsFromUser && !string.IsNullOrEmpty(m.ExternalMessageId))
            .OrderByDescending(m => m.ReceivedAt)
            .FirstOrDefault();
        if (target is null)
        {
            return TicketResult<TicketDetail>.Fail(TicketOutcome.Invalid, "This ticket has no customer message to reply to.");
        }

        if (mailClient is null)
        {
            return TicketResult<TicketDetail>.Fail(TicketOutcome.SendFailed, "Email sending is not configured.");
        }

        try
        {
            await mailClient.SendReplyAsync(target.ExternalMessageId!, body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send the reply for ticket {TicketId}.", ticketId);
            return TicketResult<TicketDetail>.Fail(
                TicketOutcome.SendFailed, "The reply could not be sent. Nothing was changed; please try again.");
        }

        try
        {
            await ticketRepository.RecordReplyAsync(ticketId, new Message
            {
                Id = Guid.NewGuid(),
                Sender = caller.Email,
                Body = body,
                IsFromUser = false,
                ExternalMessageId = null,
                ReceivedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The reply for ticket {TicketId} WAS SENT to the customer but saving it failed.", ticketId);
            return TicketResult<TicketDetail>.Fail(
                TicketOutcome.SentButNotSaved,
                "The email was sent, but saving it failed. Do not send it again; ask an admin to check the ticket.");
        }

        return await GetAsync(ticketId);
    }

    private static TicketResult<TicketDetail> NotFound() =>
        TicketResult<TicketDetail>.Fail(TicketOutcome.NotFound, "Ticket not found.");
}
```
In `src/Helpdesk.Application/DependencyInjection.cs` add `using Helpdesk.Application.Tickets;` and, right before the final `return services;` (outside the OpenRouter gate), add:
```csharp
        // Listing, claiming and replying do not depend on AI. The mail client is optional (only registered when Graph
        // is configured); without it a send returns a SendFailed outcome instead of crashing the host.
        services.AddScoped<ITicketWorkflowService, TicketWorkflowService>();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj`
Expected: PASS (all, including existing tests).

- [ ] **Step 5: Commit**

```bash
git add src/Helpdesk.Application tests/Helpdesk.Application.Tests/Tickets
git commit -m "feat: add TicketWorkflowService for queue, claim, release and reply

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: User-id claim, `/api/auth/me` id, and the API test project

**Files:**
- Modify: `src/Helpdesk.Api/Auth/HelpdeskUserClaimsTransformation.cs`, `src/Helpdesk.Api/Controllers/AuthController.cs`
- Create: `tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj`, `tests/Helpdesk.Api.Tests/TestDoubles/FakeUserRepository.cs`, `tests/Helpdesk.Api.Tests/Auth/HelpdeskUserClaimsTransformationTests.cs`, `tests/Helpdesk.Api.Tests/Controllers/AuthControllerTests.cs`
- Modify: `Helpdesk.slnx` (add the project)

**Interfaces:**
- Consumes: existing `IUserRepository`, `HelpdeskUserClaimsTransformation`, `AuthController`.
- Produces: `HelpdeskUserClaimsTransformation.UserIdClaimType = "helpdesk_user_id"` (claim value = `Users.Id` as a string, added only for registered users); `GET /api/auth/me` now returns `{ id, name, email, roles }` (`id` = that claim); a new test project `Helpdesk.Api.Tests` referencing `Helpdesk.Api`, added to the solution; `FakeUserRepository` (in-memory).

- [ ] **Step 1: Create the test project and the fake**

`tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\..\src\Helpdesk.Api\Helpdesk.Api.csproj" />
  </ItemGroup>

</Project>
```
Then run `dotnet sln Helpdesk.slnx add tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj` and confirm `Helpdesk.slnx` now lists it.

`tests/Helpdesk.Api.Tests/TestDoubles/FakeUserRepository.cs`:
```csharp
using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;

namespace Helpdesk.Api.Tests.TestDoubles;

public class FakeUserRepository : IUserRepository
{
    public List<User> Users { get; } = [];

    public Task<User?> GetByIdAsync(Guid id) => Task.FromResult(Users.FirstOrDefault(u => u.Id == id));

    public Task<User?> GetByEmailAsync(string email) =>
        Task.FromResult(Users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task<User?> GetByExternalObjectIdAsync(string externalObjectId) =>
        Task.FromResult(Users.FirstOrDefault(u => u.ExternalObjectId == externalObjectId));

    public Task<IReadOnlyList<User>> GetAllAsync() => Task.FromResult<IReadOnlyList<User>>(Users);

    public Task AddAsync(User user)
    {
        Users.Add(user);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(User user) => Task.CompletedTask;
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Helpdesk.Api.Tests/Auth/HelpdeskUserClaimsTransformationTests.cs`:
```csharp
using System.Security.Claims;
using Helpdesk.Api.Auth;
using Helpdesk.Api.Tests.TestDoubles;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;

namespace Helpdesk.Api.Tests.Auth;

public class HelpdeskUserClaimsTransformationTests
{
    private readonly FakeUserRepository _users = new();

    private static ClaimsPrincipal Principal(string? objectId, string? email)
    {
        var identity = new ClaimsIdentity("test");
        if (objectId is not null)
        {
            identity.AddClaim(new Claim("oid", objectId));
        }

        if (email is not null)
        {
            identity.AddClaim(new Claim("preferred_username", email));
        }

        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task RegisteredUser_GetsRoleRegisteredAndUserIdClaims()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "a@example.com", DisplayName = "A", Role = Role.Agent, ExternalObjectId = "obj-1" };
        _users.Users.Add(user);

        var result = await new HelpdeskUserClaimsTransformation(_users).TransformAsync(Principal("obj-1", "a@example.com"));

        Assert.Equal("true", result.FindFirstValue(HelpdeskUserClaimsTransformation.RegisteredClaimType));
        Assert.Equal("Agent", result.FindFirstValue(ClaimTypes.Role));
        Assert.Equal(user.Id.ToString(), result.FindFirstValue(HelpdeskUserClaimsTransformation.UserIdClaimType));
    }

    [Fact]
    public async Task UnknownUser_IsNotRegisteredAndHasNoUserIdClaim()
    {
        var result = await new HelpdeskUserClaimsTransformation(_users).TransformAsync(Principal("nobody", "nobody@example.com"));

        Assert.Equal("false", result.FindFirstValue(HelpdeskUserClaimsTransformation.RegisteredClaimType));
        Assert.Null(result.FindFirstValue(HelpdeskUserClaimsTransformation.UserIdClaimType));
        Assert.Null(result.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task TransformingTwice_DoesNotDuplicateTheUserIdClaim()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "a@example.com", DisplayName = "A", Role = Role.Admin, ExternalObjectId = "obj-1" };
        _users.Users.Add(user);
        var transformation = new HelpdeskUserClaimsTransformation(_users);
        var principal = Principal("obj-1", "a@example.com");

        await transformation.TransformAsync(principal);
        await transformation.TransformAsync(principal);

        Assert.Single(principal.FindAll(HelpdeskUserClaimsTransformation.UserIdClaimType));
    }
}
```
`tests/Helpdesk.Api.Tests/Controllers/AuthControllerTests.cs`:
```csharp
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Api.Auth;
using Helpdesk.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.Api.Tests.Controllers;

public class AuthControllerTests
{
    private static AuthController ControllerFor(ClaimsPrincipal principal) => new()
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
    };

    [Fact]
    public void Me_RegisteredUser_ReturnsTheUserId()
    {
        var userId = Guid.NewGuid();
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(HelpdeskUserClaimsTransformation.RegisteredClaimType, "true"));
        identity.AddClaim(new Claim(HelpdeskUserClaimsTransformation.UserIdClaimType, userId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Agent"));
        identity.AddClaim(new Claim("name", "Alice"));
        identity.AddClaim(new Claim("preferred_username", "alice@example.com"));

        var result = Assert.IsType<OkObjectResult>(ControllerFor(new ClaimsPrincipal(identity)).Me());

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(userId.ToString(), doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Alice", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("alice@example.com", doc.RootElement.GetProperty("email").GetString());
        Assert.Equal("Agent", doc.RootElement.GetProperty("roles")[0].GetString());
    }

    [Fact]
    public void Me_UnregisteredUser_IsForbidden()
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(HelpdeskUserClaimsTransformation.RegisteredClaimType, "false"));

        var result = Assert.IsType<ObjectResult>(ControllerFor(new ClaimsPrincipal(identity)).Me());

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj`
Expected: build FAIL — `UserIdClaimType` does not exist. (If the project itself fails to restore/compile because of the framework reference, report BLOCKED with the error.)

- [ ] **Step 4: Implement**

In `HelpdeskUserClaimsTransformation.cs` add next to the other constants:
```csharp
    public const string UserIdClaimType = "helpdesk_user_id";
```
and inside `if (user is not null)`, after the role claim:
```csharp
            identity.AddClaim(new Claim(UserIdClaimType, user.Id.ToString()));
```
In `AuthController.Me()` replace the final return with:
```csharp
        var id = User.FindFirstValue(HelpdeskUserClaimsTransformation.UserIdClaimType);

        return Ok(new { id, name, email, roles });
```

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet build Helpdesk.slnx` then `dotnet test Helpdesk.slnx`
Expected: 0 errors; all tests pass.
```bash
git add src/Helpdesk.Api tests/Helpdesk.Api.Tests Helpdesk.slnx
git commit -m "feat: add helpdesk_user_id claim and expose it on /api/auth/me

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: `TicketsController` and `ReplyRequestValidator`

**Files:**
- Create: `src/Helpdesk.Api/Controllers/TicketsController.cs`, `src/Helpdesk.Api/Validators/ReplyRequestValidator.cs`
- Create (tests): `tests/Helpdesk.Api.Tests/TestDoubles/FakeTicketWorkflowService.cs`, `tests/Helpdesk.Api.Tests/Controllers/TicketsControllerTests.cs`, `tests/Helpdesk.Api.Tests/Validators/ReplyRequestValidatorTests.cs`

**Interfaces:**
- Consumes: `ITicketWorkflowService`, `TicketCaller`, `TicketResult<T>`, `TicketOutcome`, `TicketFilter` (Tasks 1-2); `HelpdeskUserClaimsTransformation.UserIdClaimType` and the Api.Tests project (Task 3).
- Produces: `TicketsController` (`[Authorize(Policy = "AgentOnly")]`, route `api/tickets`) with `List`, `Get`, `Claim`, `Release`, `Reply`; `public record ReplyRequest(string Text)` (in `Helpdesk.Api.Controllers`); `ReplyRequestValidator` (`Text` not empty, max `TicketWorkflowService.MaxReplyLength`).

- [ ] **Step 1: Write the failing tests**

`tests/Helpdesk.Api.Tests/TestDoubles/FakeTicketWorkflowService.cs`:
```csharp
using Helpdesk.Application.Tickets;
using Helpdesk.Core.Enums;

namespace Helpdesk.Api.Tests.TestDoubles;

public class FakeTicketWorkflowService : ITicketWorkflowService
{
    public TicketResult<IReadOnlyList<TicketListItem>> ListResult { get; set; } =
        TicketResult<IReadOnlyList<TicketListItem>>.Ok([]);

    public TicketResult<TicketDetail> DetailResult { get; set; } =
        TicketResult<TicketDetail>.Fail(TicketOutcome.NotFound, "Ticket not found.");

    public TicketCaller? LastCaller { get; private set; }
    public TicketFilter? LastFilter { get; private set; }
    public Guid? LastTicketId { get; private set; }
    public string? LastText { get; private set; }
    public string? LastAction { get; private set; }

    public Task<TicketResult<IReadOnlyList<TicketListItem>>> ListAsync(TicketCaller caller, TicketFilter filter)
    {
        LastAction = "list";
        LastCaller = caller;
        LastFilter = filter;
        return Task.FromResult(ListResult);
    }

    public Task<TicketResult<TicketDetail>> GetAsync(Guid ticketId)
    {
        LastAction = "get";
        LastTicketId = ticketId;
        return Task.FromResult(DetailResult);
    }

    public Task<TicketResult<TicketDetail>> ClaimAsync(TicketCaller caller, Guid ticketId)
    {
        LastAction = "claim";
        LastCaller = caller;
        LastTicketId = ticketId;
        return Task.FromResult(DetailResult);
    }

    public Task<TicketResult<TicketDetail>> ReleaseAsync(TicketCaller caller, Guid ticketId)
    {
        LastAction = "release";
        LastCaller = caller;
        LastTicketId = ticketId;
        return Task.FromResult(DetailResult);
    }

    public Task<TicketResult<TicketDetail>> SendReplyAsync(
        TicketCaller caller, Guid ticketId, string text, CancellationToken cancellationToken = default)
    {
        LastAction = "reply";
        LastCaller = caller;
        LastTicketId = ticketId;
        LastText = text;
        return Task.FromResult(DetailResult);
    }
}
```
`tests/Helpdesk.Api.Tests/Controllers/TicketsControllerTests.cs`:
```csharp
using System.Reflection;
using System.Security.Claims;
using Helpdesk.Api.Auth;
using Helpdesk.Api.Controllers;
using Helpdesk.Api.Tests.TestDoubles;
using Helpdesk.Application.Tickets;
using Helpdesk.Core.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.Api.Tests.Controllers;

public class TicketsControllerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private readonly FakeTicketWorkflowService _workflow = new();

    private static ClaimsPrincipal PrincipalFor(bool withUserId = true, string role = "Agent")
    {
        var identity = new ClaimsIdentity("test");
        if (withUserId)
        {
            identity.AddClaim(new Claim(HelpdeskUserClaimsTransformation.UserIdClaimType, UserId.ToString()));
        }

        identity.AddClaim(new Claim(ClaimTypes.Role, role));
        identity.AddClaim(new Claim("preferred_username", "alice@example.com"));
        return new ClaimsPrincipal(identity);
    }

    private TicketsController Create(ClaimsPrincipal? principal = null) => new(_workflow)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal ?? PrincipalFor() },
        },
    };

    private static TicketDetail Detail() => new(
        Guid.NewGuid(), "s", "c@example.com", TicketStatus.InReview, null, null, null, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, null, []);

    [Fact]
    public void Controller_RequiresTheAgentOnlyPolicy()
    {
        var attribute = typeof(TicketsController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("AgentOnly", attribute?.Policy);
    }

    [Fact]
    public async Task List_DefaultsToTheQueueFilter_AndPassesTheCaller()
    {
        var result = await Create().List(null);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(TicketFilter.Queue, _workflow.LastFilter);
        Assert.Equal(new TicketCaller(UserId, "alice@example.com", IsAdmin: false), _workflow.LastCaller);
    }

    [Theory]
    [InlineData("mine", TicketFilter.Mine)]
    [InlineData("ALL", TicketFilter.All)]
    [InlineData("queue", TicketFilter.Queue)]
    public async Task List_ParsesTheFilterCaseInsensitively(string value, TicketFilter expected)
    {
        await Create().List(value);

        Assert.Equal(expected, _workflow.LastFilter);
    }

    [Fact]
    public async Task List_UnknownFilter_IsABadRequest()
    {
        var result = await Create().List("everything");

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(result).StatusCode);
        Assert.Null(_workflow.LastAction);
    }

    [Fact]
    public async Task Caller_IsAdminWhenTheRoleClaimIsAdmin()
    {
        await Create(PrincipalFor(role: "Admin")).List("all");

        Assert.True(_workflow.LastCaller!.IsAdmin);
    }

    [Fact]
    public async Task MissingUserIdClaim_IsForbiddenAndNothingRuns()
    {
        var result = await Create(PrincipalFor(withUserId: false)).Claim(Guid.NewGuid());

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Null(_workflow.LastAction);
    }

    [Theory]
    [InlineData(TicketOutcome.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(TicketOutcome.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(TicketOutcome.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(TicketOutcome.Invalid, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(TicketOutcome.SendFailed, StatusCodes.Status502BadGateway)]
    [InlineData(TicketOutcome.SentButNotSaved, StatusCodes.Status500InternalServerError)]
    public async Task Reply_MapsEveryOutcomeToItsStatusCode(TicketOutcome outcome, int expected)
    {
        _workflow.DetailResult = TicketResult<TicketDetail>.Fail(outcome, "a message");

        var result = await Create().Reply(Guid.NewGuid(), new ReplyRequest("Hi"), CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expected, objectResult.StatusCode);
        Assert.Contains("a message", System.Text.Json.JsonSerializer.Serialize(objectResult.Value));
    }

    [Fact]
    public async Task Reply_Success_ReturnsTheDetailAndPassesTheText()
    {
        var detail = Detail();
        _workflow.DetailResult = TicketResult<TicketDetail>.Ok(detail);
        var ticketId = Guid.NewGuid();

        var result = await Create().Reply(ticketId, new ReplyRequest("Hello there"), CancellationToken.None);

        Assert.Same(detail, Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("reply", _workflow.LastAction);
        Assert.Equal(ticketId, _workflow.LastTicketId);
        Assert.Equal("Hello there", _workflow.LastText);
    }

    [Fact]
    public async Task GetClaimAndRelease_CallTheMatchingWorkflowMethod()
    {
        _workflow.DetailResult = TicketResult<TicketDetail>.Ok(Detail());
        var controller = Create();
        var id = Guid.NewGuid();

        await controller.Get(id);
        Assert.Equal("get", _workflow.LastAction);
        await controller.Claim(id);
        Assert.Equal("claim", _workflow.LastAction);
        await controller.Release(id);
        Assert.Equal("release", _workflow.LastAction);
        Assert.Equal(id, _workflow.LastTicketId);
    }
}
```
`tests/Helpdesk.Api.Tests/Validators/ReplyRequestValidatorTests.cs`:
```csharp
using Helpdesk.Api.Controllers;
using Helpdesk.Api.Validators;
using Helpdesk.Application.Tickets;

namespace Helpdesk.Api.Tests.Validators;

public class ReplyRequestValidatorTests
{
    private readonly ReplyRequestValidator _validator = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void BlankText_IsInvalid(string text)
    {
        Assert.False(_validator.Validate(new ReplyRequest(text)).IsValid);
    }

    [Fact]
    public void NullText_IsInvalid()
    {
        Assert.False(_validator.Validate(new ReplyRequest(null!)).IsValid);
    }

    [Fact]
    public void TextAtTheLimit_IsValid_AndOneOverIsInvalid()
    {
        Assert.True(_validator.Validate(new ReplyRequest(new string('x', TicketWorkflowService.MaxReplyLength))).IsValid);
        Assert.False(_validator.Validate(new ReplyRequest(new string('x', TicketWorkflowService.MaxReplyLength + 1))).IsValid);
    }

    [Fact]
    public void NormalText_IsValid()
    {
        Assert.True(_validator.Validate(new ReplyRequest("Thanks, we are on it.")).IsValid);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj`
Expected: build FAIL — `TicketsController` / `ReplyRequest` / `ReplyRequestValidator` do not exist.

- [ ] **Step 3: Implement the controller and validator**

`src/Helpdesk.Api/Controllers/TicketsController.cs`:
```csharp
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Helpdesk.Api.Auth;
using Helpdesk.Application.Tickets;
using Helpdesk.Core.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.Api.Controllers;

[ApiController]
[Route("api/tickets")]
[Authorize(Policy = "AgentOnly")]
public class TicketsController(ITicketWorkflowService workflow) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? filter)
    {
        if (!TryGetCaller(out var caller))
        {
            return NoCaller();
        }

        if (!Enum.TryParse<TicketFilter>(filter ?? nameof(TicketFilter.Queue), ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            return BadRequest(new { message = "filter must be one of: queue, mine, all." });
        }

        return ToResult(await workflow.ListAsync(caller, parsed));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id) => ToResult(await workflow.GetAsync(id));

    [HttpPost("{id:guid}/claim")]
    public async Task<IActionResult> Claim(Guid id)
    {
        if (!TryGetCaller(out var caller))
        {
            return NoCaller();
        }

        return ToResult(await workflow.ClaimAsync(caller, id));
    }

    [HttpPost("{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id)
    {
        if (!TryGetCaller(out var caller))
        {
            return NoCaller();
        }

        return ToResult(await workflow.ReleaseAsync(caller, id));
    }

    [HttpPut("{id:guid}/reply")]
    public async Task<IActionResult> Reply(Guid id, [FromBody] ReplyRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetCaller(out var caller))
        {
            return NoCaller();
        }

        return ToResult(await workflow.SendReplyAsync(caller, id, request.Text, cancellationToken));
    }

    private bool TryGetCaller([NotNullWhen(true)] out TicketCaller? caller)
    {
        caller = null;
        if (!Guid.TryParse(User.FindFirstValue(HelpdeskUserClaimsTransformation.UserIdClaimType), out var userId))
        {
            return false;
        }

        var email = User.FindFirstValue(ClaimTypes.Upn) ?? User.FindFirstValue("preferred_username") ?? string.Empty;
        caller = new TicketCaller(userId, email, User.IsInRole(nameof(Role.Admin)));
        return true;
    }

    private IActionResult NoCaller() =>
        StatusCode(StatusCodes.Status403Forbidden, new { message = "Your account isn't registered in Helpdesk yet." });

    private IActionResult ToResult<T>(TicketResult<T> result)
    {
        var body = new { message = result.Message };
        return result.Outcome switch
        {
            TicketOutcome.Success => Ok(result.Value),
            TicketOutcome.NotFound => NotFound(body),
            TicketOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, body),
            TicketOutcome.Conflict => Conflict(body),
            TicketOutcome.Invalid => UnprocessableEntity(body),
            TicketOutcome.SendFailed => StatusCode(StatusCodes.Status502BadGateway, body),
            _ => StatusCode(StatusCodes.Status500InternalServerError, body),
        };
    }
}

public record ReplyRequest(string Text);
```
`src/Helpdesk.Api/Validators/ReplyRequestValidator.cs`:
```csharp
using FluentValidation;
using Helpdesk.Api.Controllers;
using Helpdesk.Application.Tickets;

namespace Helpdesk.Api.Validators;

public class ReplyRequestValidator : AbstractValidator<ReplyRequest>
{
    public ReplyRequestValidator()
    {
        RuleFor(x => x.Text)
            .NotEmpty()
            .MaximumLength(TicketWorkflowService.MaxReplyLength);
    }
}
```

- [ ] **Step 4: Run tests and the solution build**

Run: `dotnet build Helpdesk.slnx` then `dotnet test Helpdesk.slnx`
Expected: 0 errors; all tests pass. (`AddValidatorsFromAssemblyContaining<Program>()` in `Program.cs` picks the new validator up automatically: no registration edit is needed.)

- [ ] **Step 5: Commit**

```bash
git add src/Helpdesk.Api tests/Helpdesk.Api.Tests
git commit -m "feat: add TicketsController and reply validator

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 5: Frontend — API types, current-user id, nav link, queue page

**Files:**
- Create: `client/helpdesk-web/src/api/tickets.ts`, `client/helpdesk-web/src/pages/QueuePage.tsx`
- Modify: `client/helpdesk-web/src/hooks/useCurrentUser.ts`, `client/helpdesk-web/src/components/NavBar.tsx`, `client/helpdesk-web/src/App.tsx`

**Interfaces:**
- Consumes: the API from Task 4 (`GET /api/tickets?filter=`), `/api/auth/me` now returning `id` (Task 3), existing `apiFetch(instance, path, init)`, existing shadcn `Alert`, `Badge`, `Card`, `Table`, `Tabs`.
- Produces: `src/api/tickets.ts` exporting `TicketFilter`, `TicketStatus`, `TicketAssignee`, `TicketListItem`, `TicketMessage`, `TicketDetail` and `errorMessage(response): Promise<string>`; `CurrentUser.id: string | null`; a `QueuePage` component (`{ isAdmin: boolean }`) routed at `/tickets`; a "Tickets" NavBar link.

- [ ] **Step 1: Install dependencies in the worktree (once)**

Run in `client/helpdesk-web`: `npm ci`
Expected: installs cleanly (node_modules is git-ignored).

- [ ] **Step 2: Add the API types and error helper**

`client/helpdesk-web/src/api/tickets.ts`:
```ts
export type TicketFilter = 'queue' | 'mine' | 'all'

export type TicketStatus = 'New' | 'InReview' | 'Replied'

export type TicketAssignee = {
  id: string
  displayName: string
}

export type TicketListItem = {
  id: string
  subject: string
  requesterEmail: string
  status: TicketStatus
  category: string | null
  summary: string | null
  confidence: number | null
  assignee: TicketAssignee | null
  createdAt: string
  updatedAt: string
  hasDraft: boolean
}

export type TicketMessage = {
  id: string
  sender: string
  isFromUser: boolean
  receivedAt: string
  bodyText: string
}

export type TicketDetail = TicketListItem & {
  draftReply: string | null
  messages: TicketMessage[]
}

/** Reads the most useful error text from a failed API response ({ message } or a validation problem). */
export async function errorMessage(response: Response): Promise<string> {
  const body = (await response.json().catch(() => null)) as {
    message?: string
    title?: string
    errors?: Record<string, string[]>
  } | null
  if (body?.message) {
    return body.message
  }
  const firstValidation = body?.errors ? Object.values(body.errors).flat()[0] : undefined
  return firstValidation ?? body?.title ?? `Request failed: ${response.status} ${response.statusText}`
}
```
In `client/helpdesk-web/src/hooks/useCurrentUser.ts` add `id: string | null` as the first field of `CurrentUser`:
```ts
export type CurrentUser = {
  id: string | null
  name: string | null
  email: string | null
  roles: string[]
}
```

- [ ] **Step 3: Add the queue page**

`client/helpdesk-web/src/pages/QueuePage.tsx` (if a shadcn export used below differs in this repo's vendored version, read `src/components/ui/tabs.tsx` / `badge.tsx` and adapt the JSX — keep the behaviour):
```tsx
import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router'
import { useMsal } from '@azure/msal-react'
import { formatDistanceToNow } from 'date-fns'
import { apiFetch } from '../api/apiFetch'
import { errorMessage, type TicketFilter, type TicketListItem, type TicketStatus } from '../api/tickets'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'

type QueuePageProps = {
  isAdmin: boolean
}

const TAB_LABELS: Record<TicketFilter, string> = {
  queue: 'Queue',
  mine: 'Mine',
  all: 'All',
}

function statusVariant(status: TicketStatus): 'default' | 'secondary' | 'outline' {
  if (status === 'Replied') return 'secondary'
  if (status === 'InReview') return 'default'
  return 'outline'
}

function QueuePage({ isAdmin }: QueuePageProps) {
  const { instance } = useMsal()
  const [filter, setFilter] = useState<TicketFilter>('queue')
  const [tickets, setTickets] = useState<TicketListItem[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    async (which: TicketFilter) => {
      setError(null)
      setTickets(null)
      try {
        const response = await apiFetch(instance, `/api/tickets?filter=${which}`)
        if (!response.ok) {
          throw new Error(await errorMessage(response))
        }
        setTickets((await response.json()) as TicketListItem[])
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Unknown error')
      }
    },
    [instance],
  )

  useEffect(() => {
    load(filter)
  }, [filter, load])

  const filters: TicketFilter[] = isAdmin ? ['queue', 'mine', 'all'] : ['queue', 'mine']

  return (
    <main className="mx-auto max-w-6xl px-8 py-8">
      <Card>
        <CardHeader>
          <CardTitle>Tickets</CardTitle>
          <Tabs value={filter} onValueChange={(value) => setFilter(value as TicketFilter)}>
            <TabsList>
              {filters.map((f) => (
                <TabsTrigger key={f} value={f}>
                  {TAB_LABELS[f]}
                </TabsTrigger>
              ))}
            </TabsList>
          </Tabs>
        </CardHeader>
        <CardContent>
          {error && (
            <Alert variant="destructive">
              <AlertDescription>{error}</AlertDescription>
            </Alert>
          )}
          {!error && tickets === null && <p className="text-sm text-muted-foreground">Loading…</p>}
          {!error && tickets !== null && tickets.length === 0 && (
            <p className="text-sm text-muted-foreground">No tickets here.</p>
          )}
          {!error && tickets !== null && tickets.length > 0 && (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Subject</TableHead>
                  <TableHead>From</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Category</TableHead>
                  <TableHead>Summary</TableHead>
                  <TableHead>Assignee</TableHead>
                  <TableHead>Age</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {tickets.map((ticket) => (
                  <TableRow key={ticket.id}>
                    <TableCell className="font-medium">
                      <Link to={`/tickets/${ticket.id}`} className="hover:underline">
                        {ticket.subject}
                      </Link>
                    </TableCell>
                    <TableCell>{ticket.requesterEmail}</TableCell>
                    <TableCell>
                      <Badge variant={statusVariant(ticket.status)}>{ticket.status}</Badge>
                    </TableCell>
                    <TableCell>
                      {ticket.category ? <Badge variant="outline">{ticket.category}</Badge> : '—'}
                    </TableCell>
                    <TableCell className="max-w-xs truncate">{ticket.summary ?? '—'}</TableCell>
                    <TableCell>{ticket.assignee?.displayName ?? 'Unassigned'}</TableCell>
                    <TableCell>{formatDistanceToNow(new Date(ticket.createdAt), { addSuffix: true })}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </main>
  )
}

export default QueuePage
```

- [ ] **Step 4: Nav link and route**

In `NavBar.tsx`, inside the left `div`, directly after the `Helpdesk` `span`, add:
```tsx
        <Link to="/tickets" className="text-[15px] text-text hover:text-text-h">
          Tickets
        </Link>
```
In `App.tsx` add `import QueuePage from './pages/QueuePage'` and, after the `/admin/users` route, add a route that mirrors its guard (authenticated, wait for the user to load, and require a registered user):
```tsx
      <Route
        path="/tickets"
        element={
          !isAuthenticated ? (
            <Navigate to="/" replace />
          ) : loading ? null : user ? (
            <>
              <NavBar isAdmin={isAdmin} />
              <QueuePage isAdmin={isAdmin} />
            </>
          ) : (
            <Navigate to="/home" replace />
          )
        }
      />
```

- [ ] **Step 5: Lint, build, commit**

Run in `client/helpdesk-web`: `npm run lint` then `npm run build`
Expected: both succeed with no errors (report any pre-existing warnings separately).
```bash
git add client/helpdesk-web/src
git commit -m "feat: add ticket queue page, API types and nav link

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 6: Frontend — ticket detail page (claim, edit, send)

**Files:**
- Create: `client/helpdesk-web/src/pages/TicketDetailPage.tsx`
- Modify: `client/helpdesk-web/src/App.tsx`

**Interfaces:**
- Consumes: `TicketDetail`, `errorMessage` (Task 5), `CurrentUser` (with `id`), the API from Task 4, shadcn `Alert`, `Badge`, `Button`, `Card`, `Textarea`.
- Produces: `TicketDetailPage` (`{ user: CurrentUser | null; isAdmin: boolean }`) routed at `/tickets/:id`.

- [ ] **Step 1: Add the page**

`client/helpdesk-web/src/pages/TicketDetailPage.tsx`:
```tsx
import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import { useMsal } from '@azure/msal-react'
import { format } from 'date-fns'
import { apiFetch } from '../api/apiFetch'
import { errorMessage, type TicketDetail } from '../api/tickets'
import type { CurrentUser } from '../hooks/useCurrentUser'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Textarea } from '@/components/ui/textarea'

type TicketDetailPageProps = {
  user: CurrentUser | null
  isAdmin: boolean
}

function TicketDetailPage({ user, isAdmin }: TicketDetailPageProps) {
  const { id } = useParams()
  const { instance } = useMsal()
  const [ticket, setTicket] = useState<TicketDetail | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const draftSeeded = useRef(false)

  const load = useCallback(async () => {
    setLoadError(null)
    try {
      const response = await apiFetch(instance, `/api/tickets/${id}`)
      if (!response.ok) {
        throw new Error(await errorMessage(response))
      }
      const data = (await response.json()) as TicketDetail
      setTicket(data)
      // Seed the editor with the AI draft once; later reloads must never overwrite the agent's edits.
      if (!draftSeeded.current) {
        draftSeeded.current = true
        setText(data.draftReply ?? '')
      }
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Unknown error')
    }
  }, [instance, id])

  useEffect(() => {
    load()
  }, [load])

  async function act(path: string, init: RequestInit, successNotice?: string): Promise<boolean> {
    setBusy(true)
    setActionError(null)
    setNotice(null)
    try {
      const response = await apiFetch(instance, path, init)
      if (!response.ok) {
        setActionError(await errorMessage(response))
        return false
      }
      setTicket((await response.json()) as TicketDetail)
      if (successNotice) {
        setNotice(successNotice)
      }
      return true
    } catch (err) {
      setActionError(err instanceof Error ? err.message : 'Unknown error')
      return false
    } finally {
      setBusy(false)
    }
  }

  const claim = () => act(`/api/tickets/${id}/claim`, { method: 'POST' })
  const release = () => act(`/api/tickets/${id}/release`, { method: 'POST' })
  const send = async () => {
    const ok = await act(
      `/api/tickets/${id}/reply`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text }),
      },
      'Reply sent.',
    )
    if (ok) {
      setText('')
    }
  }

  if (loadError) {
    return (
      <main className="mx-auto max-w-4xl px-8 py-8">
        <Alert variant="destructive">
          <AlertDescription>{loadError}</AlertDescription>
        </Alert>
        <Link to="/tickets" className="mt-4 inline-block text-sm hover:underline">
          ← Back to tickets
        </Link>
      </main>
    )
  }

  if (!ticket) {
    return <main className="mx-auto max-w-4xl px-8 py-8 text-sm text-muted-foreground">Loading…</main>
  }

  const isAssignee = ticket.assignee !== null && ticket.assignee.id === user?.id
  const canAct = ticket.assignee !== null && (isAssignee || isAdmin)
  const canRelease = ticket.assignee !== null && (isAssignee || isAdmin)
  const canClaim = ticket.assignee === null

  return (
    <main className="mx-auto flex max-w-4xl flex-col gap-6 px-8 py-8">
      <Link to="/tickets" className="text-sm hover:underline">
        ← Back to tickets
      </Link>

      <header className="flex flex-col gap-2">
        <h1 className="font-heading text-2xl font-semibold text-text-h">{ticket.subject}</h1>
        <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          <span>{ticket.requesterEmail}</span>
          <Badge>{ticket.status}</Badge>
          {ticket.category && <Badge variant="outline">{ticket.category}</Badge>}
        </div>
      </header>

      {ticket.summary && (
        <Card>
          <CardHeader>
            <CardTitle>AI summary</CardTitle>
          </CardHeader>
          <CardContent className="text-sm">
            <p>{ticket.summary}</p>
            {ticket.confidence !== null && (
              <p className="mt-2 text-muted-foreground">Confidence {Math.round(ticket.confidence * 100)}%</p>
            )}
          </CardContent>
        </Card>
      )}

      <section className="flex flex-col gap-3">
        <h2 className="font-heading text-lg font-semibold text-text-h">Conversation</h2>
        {ticket.messages.map((message) => (
          <Card key={message.id}>
            <CardHeader>
              <CardTitle className="flex items-center gap-2 text-sm">
                <Badge variant={message.isFromUser ? 'outline' : 'secondary'}>
                  {message.isFromUser ? 'Customer' : 'Agent'}
                </Badge>
                <span>{message.sender}</span>
                <span className="font-normal text-muted-foreground">
                  {format(new Date(message.receivedAt), 'PPp')}
                </span>
              </CardTitle>
            </CardHeader>
            <CardContent>
              <p className="whitespace-pre-wrap text-sm">{message.bodyText}</p>
            </CardContent>
          </Card>
        ))}
      </section>

      <section className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <span>
            Assignee: <strong>{ticket.assignee?.displayName ?? 'Unassigned'}</strong>
          </span>
          {canClaim && (
            <Button type="button" size="sm" disabled={busy} onClick={claim}>
              Assign to me
            </Button>
          )}
          {canRelease && (
            <Button type="button" size="sm" variant="outline" disabled={busy} onClick={release}>
              Release
            </Button>
          )}
        </div>

        {actionError && (
          <Alert variant="destructive">
            <AlertDescription>{actionError}</AlertDescription>
          </Alert>
        )}
        {notice && (
          <Alert>
            <AlertDescription>{notice}</AlertDescription>
          </Alert>
        )}

        <Card>
          <CardHeader>
            <CardTitle>Reply</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-3">
            {!canAct && (
              <p className="text-sm text-muted-foreground">
                {ticket.assignee === null
                  ? 'Assign this ticket to yourself to edit and send a reply.'
                  : `Only ${ticket.assignee.displayName} (or an admin) can reply to this ticket.`}
              </p>
            )}
            <Textarea
              value={text}
              onChange={(event) => setText(event.target.value)}
              rows={10}
              disabled={!canAct || busy}
              placeholder={ticket.draftReply ? undefined : 'Write your reply…'}
            />
            <div>
              <Button type="button" disabled={!canAct || busy || text.trim().length === 0} onClick={send}>
                {busy ? 'Working…' : 'Send reply'}
              </Button>
            </div>
          </CardContent>
        </Card>
      </section>
    </main>
  )
}

export default TicketDetailPage
```

- [ ] **Step 2: Add the route**

In `App.tsx` add `import TicketDetailPage from './pages/TicketDetailPage'` and, directly after the `/tickets` route, add:
```tsx
      <Route
        path="/tickets/:id"
        element={
          !isAuthenticated ? (
            <Navigate to="/" replace />
          ) : loading ? null : user ? (
            <>
              <NavBar isAdmin={isAdmin} />
              <TicketDetailPage user={user} isAdmin={isAdmin} />
            </>
          ) : (
            <Navigate to="/home" replace />
          )
        }
      />
```

- [ ] **Step 3: Lint, build, commit**

Run in `client/helpdesk-web`: `npm run lint` then `npm run build`
Expected: both succeed (report any new warnings).
```bash
git add client/helpdesk-web/src
git commit -m "feat: add ticket detail page with claim, edit and send

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 7: Rebase onto Phase 8 and add the review-flag DTO fields

**PRECONDITION (controller checks before dispatching):** Phase 8 (`phase8-escalation`) is merged into `main`. If it is not, this task waits; Tasks 1-6 do not depend on it.

**Files:**
- Modify: `src/Helpdesk.Application/Tickets/TicketDtos.cs`, `src/Helpdesk.Application/Tickets/TicketMapper.cs`
- Modify: `tests/Helpdesk.Application.Tests/Tickets/TicketWorkflowServiceTests.cs`, `tests/Helpdesk.Api.Tests/Controllers/TicketsControllerTests.cs`
- Modify: `client/helpdesk-web/src/api/tickets.ts`

**Interfaces:**
- Consumes: Phase 8's `Ticket.ReviewReasons` (`[Flags] Helpdesk.Core.Enums.ReviewReasons`, int column, values `None=0, LowConfidence=1, ClassificationFailed=2, CategoryOther=4, DraftFailed=8`).
- Produces: `TicketListItem` and `TicketDetail` each gain `int ReviewReasons, bool NeedsReview` (placed right after `HasDraft`, before `DraftReply`/`Messages` in `TicketDetail`); frontend `TicketListItem` gains `reviewReasons: number` and `needsReview: boolean`.

- [ ] **Step 1: Rebase**

Run: `git rebase main`
Expect textual conflicts only in files both phases edited: `tests/Helpdesk.Application.Tests/TestDoubles/FakeTicketRepository.cs` (keep BOTH sets of members: Phase 8's `UpdateCount` and this phase's appended members), `CLAUDE.md`, `implementation-plan.md` and possibly `src/Helpdesk.Application/DependencyInjection.cs` (keep both registrations). Resolve by keeping both sides, `git add`, `git rebase --continue`. `implementation-plan.md` contains non-UTF8 bytes: resolve it byte-safely (do not open/rewrite it in a UTF-8 editor). Then `dotnet build Helpdesk.slnx` and `dotnet test Helpdesk.slnx` must be green BEFORE any further change; if not, stop and report.

- [ ] **Step 2: Write the failing tests**

In `TicketWorkflowServiceTests.cs` add `using` nothing new and these tests (inside the class):
```csharp
    [Fact]
    public async Task ListAndDetail_ExposeTheReviewFlagsAsIntAndNeedsReview()
    {
        var flagged = AddTicket(assignedTo: _alice);
        flagged.ReviewReasons = ReviewReasons.LowConfidence | ReviewReasons.DraftFailed;
        var clean = AddTicket(assignedTo: _alice);

        var rows = (await Create().ListAsync(AliceCaller, TicketFilter.Mine)).Value!;
        var flaggedRow = rows.Single(r => r.Id == flagged.Id);
        var cleanRow = rows.Single(r => r.Id == clean.Id);
        var detail = (await Create().GetAsync(flagged.Id)).Value!;

        Assert.Equal(9, flaggedRow.ReviewReasons);
        Assert.True(flaggedRow.NeedsReview);
        Assert.Equal(0, cleanRow.ReviewReasons);
        Assert.False(cleanRow.NeedsReview);
        Assert.Equal(9, detail.ReviewReasons);
        Assert.True(detail.NeedsReview);
    }
```
In `TicketsControllerTests.cs` update the `Detail()` helper to pass the two new arguments (`0, false` after `false` for HasDraft): `new(Guid.NewGuid(), "s", "c@example.com", TicketStatus.InReview, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, 0, false, null, [])`.
Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~ReviewFlags"`
Expected: build FAIL — `ReviewReasons`/`NeedsReview` members do not exist on the DTOs.

- [ ] **Step 3: Add the fields**

In `TicketDtos.cs`, add `int ReviewReasons, bool NeedsReview,` after `bool HasDraft,` in BOTH `TicketListItem` and `TicketDetail` (in `TicketDetail` they go before `string? DraftReply`). In `TicketMapper.cs`, in both `ToListItem` and `ToDetail`, add after the `!string.IsNullOrWhiteSpace(ticket.DraftReply)` argument:
```csharp
        (int)ticket.ReviewReasons,
        ticket.ReviewReasons != ReviewReasons.None,
```
and add `using Helpdesk.Core.Enums;` to the mapper. In `client/helpdesk-web/src/api/tickets.ts` add to `TicketListItem` (after `hasDraft`):
```ts
  reviewReasons: number
  needsReview: boolean
```
(No badge in this phase: the badge is Phase 8 task 48, a separate follow-up.)

- [ ] **Step 4: Verify and commit**

Run: `dotnet build Helpdesk.slnx`, `dotnet test Helpdesk.slnx`, and in `client/helpdesk-web`: `npm run lint`, `npm run build`.
Expected: all green.
```bash
git add src tests client/helpdesk-web/src
git commit -m "feat: expose review flags on ticket list and detail DTOs

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 8: Docs

**Files:**
- Modify: `CLAUDE.md`, `implementation-plan.md`

**Interfaces:** consumes everything above; produces documentation only.

- [ ] **Step 1: Update `CLAUDE.md`**

Insert this section directly after the `### Review flags / escalation (...)` section (added by Phase 8; if it is somehow absent, insert after the `### AI draft reply (Phase 6 done)` section) and before `### Data flow (MVP core loop)`:
```markdown
### Agent queue and reply (Phase 7 done, manual verification pending)
`TicketsController` (`AgentOnly`: Admin or Agent, route `api/tickets`) exposes `GET ?filter=queue|mine|all` (default `queue` = unassigned + mine, excluding `Replied`; `all` is Admin only), `GET {id}`, `POST {id}/claim`, `POST {id}/release` and `PUT {id}/reply` `{ "text" }`; all logic lives in `Helpdesk.Application/Tickets/TicketWorkflowService` (outcomes `Success/NotFound/Forbidden/Conflict/Invalid/SendFailed/SentButNotSaved` mapped to 200/404/403/409/422/502/500 by the controller). Claiming is explicit and atomic (`TicketRepository.TryClaimAsync` is a conditional `UPDATE`, 409 if someone else holds the ticket); only the assignee (or an Admin) may reply or release. A reply is validated (not blank, at most 10 000 chars), sent through `IMailClient.SendReplyAsync` (Graph reply on the latest customer message that has an `ExternalMessageId`, so it threads; needs `Mail.Send`), then stored as an outbound `Message` (`IsFromUser=false`, sender = the agent's email) with `Status=Replied` in one save (`RecordReplyAsync`); `DraftReply` keeps the AI's original. If Graph fails nothing is stored (502); if the email was sent but saving failed the API returns 500 saying so (no outbox at MVP). Customer message bodies are returned only as plain text (`HtmlText`). The caller is identified by the `helpdesk_user_id` claim added by `HelpdeskUserClaimsTransformation` (also returned as `id` by `/api/auth/me`). The DTOs expose `reviewReasons` (int flags) and `needsReview` from Phase 8. Frontend: `QueuePage` (`/tickets`) and `TicketDetailPage` (`/tickets/:id`) with `src/api/tickets.ts`; the review-flag badge (Phase 8 task 48) is not built yet. Tests: `tests/Helpdesk.Api.Tests` (controller, claims, validator) plus Application/Infrastructure tests; the repository SQL (atomic claim, reply record) and Graph reply newline handling are verified by the manual test and later Playwright E2E. Not in this phase (Phase 7b): reopening a `Replied` ticket on a customer follow-up, keeping the assignee, thread-aware AI re-draft. Spec: `docs/superpowers/specs/2026-09-27-phase7-agent-queue-reply-design.md`; plan: `docs/superpowers/plans/2026-09-27-phase7-agent-queue-reply.md`. Manual check (needs the user's secrets, a real email and a mailbox they can read): sign in as an agent, email the monitored mailbox from an address you control, open `/tickets`, claim the ticket, edit the draft, Send; the reply must arrive in the sender's inbox as a threaded reply (check that paragraph line breaks survived; if Graph collapses them, switch `GraphMailClient.SendReplyAsync` to createReply + PATCH) and the ticket must show `Replied`.
```
Also add to the `## Commands` "Run all tests" area a one-line note: `tests/Helpdesk.Api.Tests` is the controller/auth test project.

- [ ] **Step 2: Update `implementation-plan.md` (byte-level)**

The file contains non-UTF8 bytes on other lines: edit it BYTE-LEVEL only (a small Python script doing exact `bytes.replace` on the Phase 7 heading and tasks 40-45, preserving CRLF and every other byte), then confirm with `git diff --stat` that only those lines changed. New text:
```
## Phase 7 — Agent Queue & Reply UI — done (manual test pending)
40. `TicketsController`: `GET /api/tickets` (queue), `GET /api/tickets/{id}` — done (`filter=queue|mine|all`; explicit claim/release added: `POST .../claim`, `POST .../release`; detail returns the plain-text thread, summary and draft)
41. `PUT /api/tickets/{id}/reply` — done (assignee/Admin only; validates, sends via Graph, stores the outbound message, marks `Replied`)
42. Implement outbound email send via Graph API — done (`IMailClient.SendReplyAsync`, Graph reply on the latest customer message, so it threads; deviation: replies to the message rather than using `conversationId`)
43. Frontend: Queue page — done (`QueuePage`, `/tickets`, Queue/Mine/All tabs)
44. Frontend: Ticket detail page — done (`TicketDetailPage`, `/tickets/:id`: thread, AI summary, claim/release, editable draft, Send)
45. Manual test: full loop — pending (needs the user's secrets, a real email and a readable inbox; see CLAUDE.md "Agent queue and reply"). Phase 7b (reopen on customer reply, keep assignee, AI re-draft) is not started.
```

- [ ] **Step 3: Verify and commit**

Run: `dotnet build Helpdesk.slnx` and `dotnet test Helpdesk.slnx`
Expected: green (docs only; sanity check).
```bash
git add CLAUDE.md implementation-plan.md
git commit -m "docs: document the Phase 7 agent queue and reply flow

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Post-merge verification (controller and user, not implementer tasks)

1. **Host starts (controller):** with `OpenRouter__Enabled=false` and `GraphApi__Enabled=false`, run the API and confirm `GET /api/health` is healthy (proves the DI graph, including the optional `IMailClient` on `TicketWorkflowService`, builds); then stop it.
2. **Manual full loop (user's hands):** clear the shared mailbox, start the API with the free model (`OpenRouter__Model=nvidia/nemotron-3-super-120b-a12b:free`), run `npm run dev` in `client/helpdesk-web`, sign in as an agent, send an email to the mailbox from an address you control, wait one poll, open `/tickets`, claim it, edit the draft, Send; confirm the threaded reply arrives, the ticket shows `Replied`, and a second agent sees 409/read-only on a claimed ticket. Record the result in `CLAUDE.md` / `implementation-plan.md`.

## Self-Review Notes

- **Spec coverage:** endpoints and filters (Tasks 2, 4); atomic claim, release, reply record and Graph reply (Task 1); rules, statuses, plain-text bodies and failure semantics (Task 2); claim id plumbing (Task 3); HTTP mapping and validation (Task 4); queue and detail UI (Tasks 5-6); Phase 8 contract fields (Task 7); docs and manual test (Task 8). Out-of-scope items (7b, drafts-without-sending, outbox, attachments, badge, pagination) untouched.
- **Cross-task types:** `TicketFilter`/repository/mail contracts (Task 1) used by Task 2 fakes and service; `TicketResult<T>`/`TicketCaller`/`ITicketWorkflowService` (Task 2) used by Task 4 controller and fake; `UserIdClaimType` (Task 3) used by Task 4; `TicketDetail`/`errorMessage`/`CurrentUser.id` (Task 5) used by Task 6; Task 7 changes the DTO constructors, so the Task 4 test helper `Detail()` is updated there.
- **Known gaps carried forward:** no DB-backed test of the atomic claim/reply SQL; Graph reply newline behaviour unverified until the manual test; sent-but-not-saved has no outbox.
