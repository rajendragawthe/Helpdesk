# Phase 7b: Reopen on Customer Reply — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a customer replies on an existing ticket's thread while that ticket is `InReview` or `Replied`, reopen it (`Replied → InReview`), keep the assignee, reclassify from the latest message, and redraft the AI reply from the full thread transcript.

**Architecture:** Two new unconditional (non-guarded) sibling methods — `ReclassifyTicketAsync` on `IClassificationService` and `RedraftReplyAsync` on `IDraftReplyService` — reuse the existing `ClassificationService`/`DraftReplyService` classes and `IAiService`/`IKnowledgeBase`/`KbMatcher` dependencies. `EmailIngestionService.ProcessEmailAsync` is extended to detect "this follow-up needs reopening" and `IngestNewEmailsAsync` gets a second orchestration branch (reclassify → redraft → review-evaluate) parallel to the existing new-ticket branch, with the same per-step DI scope isolation and AI-optional gating. `IReviewFlagService` is reused unchanged.

**Tech Stack:** .NET 10, xUnit, no new packages.

**Spec:** `docs/superpowers/specs/2026-09-27-phase7b-reopen-on-reply-design.md`

## Global Constraints

- No new `TicketStatus` enum value; only `Replied → InReview` transitions are added, `InReview` stays `InReview`.
- `Ticket.AssignedUserId` must never be modified by any code added in this plan.
- Reclassification uses the **latest** customer message only (plain text via `HtmlText.ToPlainText`); redraft uses the **full thread transcript** (all messages, both directions, chronological).
- On a redraft AI failure or blank result, `Ticket.DraftReply` must be cleared to `null` (never left stale).
- On a reclassify AI failure, the ticket's existing `Classification` is left untouched (not cleared).
- A follow-up on a `New` ticket must trigger neither reclassify nor redraft (unchanged behavior).
- No interface change to `IAiService` — the thread transcript is passed as the existing `body` string parameter.
- Every new method that talks to `IAiService` propagates only `OperationCanceledException` when `cancellationToken.IsCancellationRequested`; every other exception is logged and swallowed.

## Review Focus

- **A ticket reaches `InReview` with `Classification == null`** (original classify failed, e.g. `ClassificationFailed`) and then gets a customer reply: `ReclassifyTicketAsync` must create a new `Classification` row, not throw on a null navigation. Covered in Task 1.
- **A ticket reaches `InReview` with `DraftReply == null`** (original draft failed, e.g. `DraftFailed`) and then gets a customer reply: `RedraftReplyAsync` must still run (it has no "already drafted" guard) and can set a fresh draft. Covered in Task 2.
- **The redraft AI call throws after a ticket already had a good (non-null) draft from the first thread**: the stale draft must be cleared to `null`, not left showing the old answer, and `Status` must still end up `InReview`. Covered in Task 2.
- **Two customer messages on the same `Replied` ticket arrive in the same poll tick** (sequential `foreach`, each in its own DI scope): the first reopens `Replied → InReview` and redrafts from a 1-reply thread; the second (now `InReview`) triggers `NeedsReopen` again and redrafts from the now-2-reply thread. Covered in Task 3.
- **A reply to a `Replied` ticket that is currently assigned**: `AssignedUserId` must be identical before and after ingestion processes the reply, through both the status-flip and the reclassify/redraft calls. Covered in Task 3.

---

## Task 1: `IClassificationService.ReclassifyTicketAsync`

**Files:**
- Modify: `src/Helpdesk.Application/Classification/IClassificationService.cs`
- Modify: `src/Helpdesk.Application/Classification/ClassificationService.cs`
- Modify: `tests/Helpdesk.Application.Tests/TestDoubles/FakeClassificationService.cs`
- Test: `tests/Helpdesk.Application.Tests/Classification/ClassificationServiceTests.cs`

**Interfaces:**
- Consumes: `ITicketRepository.GetByIdAsync(Guid)` → `Ticket?` (with `Messages`, `Classification` loaded, tracked); `ITicketRepository.UpdateAsync(Ticket)`; `IClassificationRepository.AddAsync(Classification)`; `IAiService.ClassifyAsync(string subject, string body, CancellationToken)` → `Task<ClassificationResult>`; `HtmlText.ToPlainText(string)`.
- Produces: `IClassificationService.ReclassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)` — used by Task 3's `EmailIngestionService`. `FakeClassificationService.ReclassifiedTicketIds` (`List<Guid>`) — used by Task 3's tests.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Helpdesk.Application.Tests/Classification/ClassificationServiceTests.cs` (after the existing tests, inside the `ClassificationServiceTests` class):

```csharp
[Fact]
public async Task ReclassifyTicketAsync_ExistingClassification_UpdatesInPlace()
{
    var ticket = AddTicket(UserMessage("<p>original</p>", DateTimeOffset.UtcNow.AddHours(-1)));
    ticket.Classification = new Helpdesk.Core.Entities.Classification
    {
        Id = Guid.NewGuid(),
        TicketId = ticket.Id,
        Category = "Other",
        Summary = "old summary",
        Confidence = 0.5,
    };
    ticket.Messages.Add(new Message
    {
        Id = Guid.NewGuid(),
        TicketId = ticket.Id,
        Sender = "a@example.com",
        Body = "<p>a new billing question</p>",
        IsFromUser = true,
        ReceivedAt = DateTimeOffset.UtcNow,
    });
    var existingId = ticket.Classification.Id;
    _ai.Result = new ClassificationResult("Billing", "New billing question.", 0.93);

    await CreateService().ReclassifyTicketAsync(ticket.Id);

    Assert.Equal(existingId, ticket.Classification.Id);
    Assert.Equal("Billing", ticket.Classification.Category);
    Assert.Equal("New billing question.", ticket.Classification.Summary);
    Assert.Equal(0.93, ticket.Classification.Confidence);
    Assert.Empty(_classifications.Classifications);
}

[Fact]
public async Task ReclassifyTicketAsync_SendsSubjectAndPlainTextOfLatestUserMessageToAi()
{
    var ticket = AddTicket(
        UserMessage("<p>original question</p>", DateTimeOffset.UtcNow.AddHours(-1)),
        UserMessage("<p>a <b>later</b> follow-up</p>", DateTimeOffset.UtcNow));

    await CreateService().ReclassifyTicketAsync(ticket.Id);

    var call = Assert.Single(_ai.Calls);
    Assert.Equal("Charged twice", call.Subject);
    Assert.Equal("a later follow-up", call.Body);
}

[Fact]
public async Task ReclassifyTicketAsync_NoExistingClassification_CreatesOne()
{
    var ticket = AddTicket(UserMessage("<p>hi</p>", DateTimeOffset.UtcNow));
    _ai.Result = new ClassificationResult("General Inquiry", "A greeting.", 0.6);

    await CreateService().ReclassifyTicketAsync(ticket.Id);

    var stored = Assert.Single(_classifications.Classifications);
    Assert.Equal(ticket.Id, stored.TicketId);
    Assert.Equal("General Inquiry", stored.Category);
    Assert.Equal("A greeting.", stored.Summary);
    Assert.Equal(0.6, stored.Confidence);
    Assert.NotEqual(Guid.Empty, stored.Id);
}

[Fact]
public async Task ReclassifyTicketAsync_TicketNotFound_DoesNothing()
{
    await CreateService().ReclassifyTicketAsync(Guid.NewGuid());

    Assert.Empty(_ai.Calls);
    Assert.Empty(_classifications.Classifications);
}

[Fact]
public async Task ReclassifyTicketAsync_NoUserMessage_DoesNothing()
{
    var ticket = AddTicket();

    await CreateService().ReclassifyTicketAsync(ticket.Id);

    Assert.Empty(_ai.Calls);
}

[Fact]
public async Task ReclassifyTicketAsync_AiThrows_LeavesExistingClassificationUnchanged()
{
    var ticket = AddTicket(UserMessage("<p>hi</p>", DateTimeOffset.UtcNow));
    ticket.Classification = new Helpdesk.Core.Entities.Classification
    {
        Id = Guid.NewGuid(),
        TicketId = ticket.Id,
        Category = "Billing",
        Summary = "kept",
        Confidence = 0.8,
    };
    _ai.ExceptionToThrow = new HttpRequestException("boom");

    await CreateService().ReclassifyTicketAsync(ticket.Id);

    Assert.Equal("Billing", ticket.Classification.Category);
    Assert.Equal("kept", ticket.Classification.Summary);
}

[Fact]
public async Task ReclassifyTicketAsync_Cancelled_PropagatesCancellation()
{
    var ticket = AddTicket(UserMessage("<p>hi</p>", DateTimeOffset.UtcNow));
    _ai.ExceptionToThrow = new OperationCanceledException();
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAsync<OperationCanceledException>(
        () => CreateService().ReclassifyTicketAsync(ticket.Id, cts.Token));
}
```

- [ ] **Step 2: Run tests to verify they fail to compile (method doesn't exist yet)**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~ReclassifyTicketAsync"`
Expected: build error, `ReclassifyTicketAsync` does not exist on `IClassificationService`/`ClassificationService`.

- [ ] **Step 3: Add the method to the interface**

In `src/Helpdesk.Application/Classification/IClassificationService.cs`, add alongside the existing member:

```csharp
    /// <summary>
    /// Unconditionally (re)classifies the ticket from its latest customer message — used when a
    /// customer reply reopens an InReview/Replied ticket. Unlike <see cref="ClassifyTicketAsync"/>,
    /// this has no "already classified" guard: it updates the existing Classification in place, or
    /// creates one if the ticket never had one. Never throws for AI or persistence failures (logs and
    /// leaves the previous classification, if any, unchanged); only cancellation propagates.
    /// </summary>
    Task ReclassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Implement it in `ClassificationService`**

In `src/Helpdesk.Application/Classification/ClassificationService.cs`, add this method to the class:

```csharp
    public async Task ReclassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId);
            if (ticket is null)
            {
                logger.LogWarning("Cannot reclassify ticket {TicketId}: not found.", ticketId);
                return;
            }

            var latestMessage = ticket.Messages
                .Where(m => m.IsFromUser)
                .OrderByDescending(m => m.ReceivedAt)
                .FirstOrDefault();
            if (latestMessage is null)
            {
                logger.LogWarning("Cannot reclassify ticket {TicketId}: it has no customer message.", ticketId);
                return;
            }

            Helpdesk.Core.Models.ClassificationResult result;
            try
            {
                result = await aiService.ClassifyAsync(
                    ticket.Subject,
                    HtmlText.ToPlainText(latestMessage.Body),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to reclassify ticket {TicketId}; leaving its previous classification unchanged.", ticketId);
                return;
            }

            if (ticket.Classification is { } existing)
            {
                existing.Category = result.Category;
                existing.Summary = result.Summary;
                existing.Confidence = result.Confidence;
                await ticketRepository.UpdateAsync(ticket);
            }
            else
            {
                await classificationRepository.AddAsync(new Helpdesk.Core.Entities.Classification
                {
                    Id = Guid.NewGuid(),
                    TicketId = ticket.Id,
                    Category = result.Category,
                    Summary = result.Summary,
                    Confidence = result.Confidence,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reclassify ticket {TicketId}.", ticketId);
        }
    }
```

- [ ] **Step 5: Implement the fake**

In `tests/Helpdesk.Application.Tests/TestDoubles/FakeClassificationService.cs`, replace the file contents with:

```csharp
using Helpdesk.Application.Classification;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeClassificationService : IClassificationService
{
    public List<Guid> ClassifiedTicketIds { get; } = [];
    public List<Guid> ReclassifiedTicketIds { get; } = [];
    public Exception? ExceptionToThrow { get; set; }

    public Task ClassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        ClassifiedTicketIds.Add(ticketId);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.CompletedTask;
    }

    public Task ReclassifyTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        ReclassifiedTicketIds.Add(ticketId);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 6: Run the tests and verify they pass**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~ClassificationServiceTests"`
Expected: all pass (existing + new).

- [ ] **Step 7: Build the whole solution to catch other `IClassificationService` implementers**

Run: `dotnet build Helpdesk.slnx`
Expected: builds clean (the only implementers are `ClassificationService` and `FakeClassificationService`, both updated).

- [ ] **Step 8: Commit**

```bash
git add src/Helpdesk.Application/Classification/IClassificationService.cs src/Helpdesk.Application/Classification/ClassificationService.cs tests/Helpdesk.Application.Tests/TestDoubles/FakeClassificationService.cs tests/Helpdesk.Application.Tests/Classification/ClassificationServiceTests.cs
git commit -m "feat: add unconditional ticket reclassification for reopen flow"
```

---

## Task 2: `IDraftReplyService.RedraftReplyAsync` + `ThreadTranscript`

**Files:**
- Create: `src/Helpdesk.Application/DraftReply/ThreadTranscript.cs`
- Modify: `src/Helpdesk.Application/DraftReply/IDraftReplyService.cs`
- Modify: `src/Helpdesk.Application/DraftReply/DraftReplyService.cs`
- Modify: `tests/Helpdesk.Application.Tests/TestDoubles/FakeDraftReplyService.cs`
- Test: `tests/Helpdesk.Application.Tests/DraftReply/DraftReplyServiceTests.cs`

**Interfaces:**
- Consumes: `ITicketRepository.GetByIdAsync`/`UpdateAsync` (Task 1); `IKnowledgeBase.GetAll()`; `KbMatcher.Match(string subject, string body, string? category, IReadOnlyList<KbArticle> articles)`; `IAiService.DraftReplyAsync(string subject, string body, string? category, IReadOnlyList<KbArticle> articles, CancellationToken)` → `Task<string>`; `HtmlText.ToPlainText(string)`.
- Produces: `ThreadTranscript.Build(IEnumerable<Message> messages)` → `string`, internal to `Helpdesk.Application.DraftReply`. `IDraftReplyService.RedraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default)` — used by Task 3's `EmailIngestionService`. `FakeDraftReplyService.RedraftedTicketIds` (`List<Guid>`) — used by Task 3's tests.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Helpdesk.Application.Tests/DraftReply/DraftReplyServiceTests.cs` (inside the `DraftReplyServiceTests` class):

```csharp
[Fact]
public async Task RedraftReplyAsync_BuildsTranscriptFromFullThreadAndSetsInReview()
{
    var ticket = AddTicket(
        "Billing",
        UserMessage("<p>I was charged twice</p>", DateTimeOffset.UtcNow.AddHours(-2)));
    ticket.Messages.Add(new Message
    {
        Id = Guid.NewGuid(),
        TicketId = ticket.Id,
        Sender = "agent@example.com",
        Body = "We refunded the extra charge.",
        IsFromUser = false,
        ReceivedAt = DateTimeOffset.UtcNow.AddHours(-1),
    });
    ticket.Messages.Add(new Message
    {
        Id = Guid.NewGuid(),
        TicketId = ticket.Id,
        Sender = "a@example.com",
        Body = "<p>It happened again today</p>",
        IsFromUser = true,
        ReceivedAt = DateTimeOffset.UtcNow,
    });
    ticket.DraftReply = "stale draft from before";
    ticket.Status = TicketStatus.Replied;
    _ai.DraftResult = "Sorry about that, we're investigating.";

    await CreateService().RedraftReplyAsync(ticket.Id);

    var call = Assert.Single(_ai.DraftCalls);
    Assert.Contains("I was charged twice", call.Body);
    Assert.Contains("We refunded the extra charge.", call.Body);
    Assert.Contains("It happened again today", call.Body);
    Assert.Contains("[Customer", call.Body);
    Assert.Contains("[Agent", call.Body);
    Assert.Equal("Sorry about that, we're investigating.", ticket.DraftReply);
    Assert.Equal(TicketStatus.InReview, ticket.Status);
}

[Fact]
public async Task RedraftReplyAsync_NotGuardedByExistingDraft()
{
    var ticket = AddTicket("Billing", OneMessage());
    ticket.DraftReply = "old draft";
    _ai.DraftResult = "new draft";

    await CreateService().RedraftReplyAsync(ticket.Id);

    Assert.Single(_ai.DraftCalls);
    Assert.Equal("new draft", ticket.DraftReply);
}

[Fact]
public async Task RedraftReplyAsync_AiThrows_ClearsStaleDraftAndStillSetsInReview()
{
    var ticket = AddTicket("Billing", OneMessage());
    ticket.DraftReply = "stale draft";
    ticket.Status = TicketStatus.Replied;
    _ai.DraftExceptionToThrow = new HttpRequestException("boom");

    await CreateService().RedraftReplyAsync(ticket.Id);

    Assert.Null(ticket.DraftReply);
    Assert.Equal(TicketStatus.InReview, ticket.Status);
}

[Theory]
[InlineData("")]
[InlineData("   \n ")]
public async Task RedraftReplyAsync_BlankDraft_ClearsStaleDraft(string blank)
{
    var ticket = AddTicket("Billing", OneMessage());
    ticket.DraftReply = "stale draft";
    _ai.DraftResult = blank;

    await CreateService().RedraftReplyAsync(ticket.Id);

    Assert.Null(ticket.DraftReply);
    Assert.Equal(TicketStatus.InReview, ticket.Status);
}

[Fact]
public async Task RedraftReplyAsync_TicketNotFound_DoesNothing()
{
    await CreateService().RedraftReplyAsync(Guid.NewGuid());

    Assert.Empty(_ai.DraftCalls);
}

[Fact]
public async Task RedraftReplyAsync_NoMessages_DoesNothing()
{
    var ticket = AddTicket("Billing");

    await CreateService().RedraftReplyAsync(ticket.Id);

    Assert.Empty(_ai.DraftCalls);
}

[Fact]
public async Task RedraftReplyAsync_Cancelled_PropagatesCancellation()
{
    var ticket = AddTicket("Billing", OneMessage());
    _ai.DraftExceptionToThrow = new OperationCanceledException();
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAsync<OperationCanceledException>(
        () => CreateService().RedraftReplyAsync(ticket.Id, cts.Token));
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~RedraftReplyAsync"`
Expected: build error, `RedraftReplyAsync` does not exist yet.

- [ ] **Step 3: Add `ThreadTranscript`**

Create `src/Helpdesk.Application/DraftReply/ThreadTranscript.cs`:

```csharp
using Helpdesk.Application.Classification;
using Helpdesk.Core.Entities;

namespace Helpdesk.Application.DraftReply;

/// <summary>
/// Builds a plain-text, chronological transcript of a ticket's whole message thread (both
/// directions) for redrafting when a customer reply reopens the ticket. The original single-message
/// draft never uses this — it only ever sees the first customer message.
/// </summary>
internal static class ThreadTranscript
{
    public static string Build(IEnumerable<Message> messages) =>
        string.Join(
            "\n\n",
            messages
                .OrderBy(m => m.ReceivedAt)
                .Select(m => $"[{(m.IsFromUser ? "Customer" : "Agent")} - {m.ReceivedAt:u}]\n{HtmlText.ToPlainText(m.Body)}"));
}
```

- [ ] **Step 4: Add the method to the interface**

In `src/Helpdesk.Application/DraftReply/IDraftReplyService.cs`, add:

```csharp
    /// <summary>
    /// Unconditionally redrafts the AI reply from the ticket's full message thread (both directions)
    /// and moves it to InReview — used when a customer reply reopens an InReview/Replied ticket.
    /// Unlike <see cref="DraftReplyAsync"/>, this has no "already drafted" guard. On an AI failure or
    /// a blank result, clears DraftReply to null (never leaves a stale draft addressed to an earlier
    /// message) but still moves the ticket to InReview. Never throws for AI or persistence failures;
    /// only cancellation propagates.
    /// </summary>
    Task RedraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default);
```

- [ ] **Step 5: Implement it in `DraftReplyService`**

In `src/Helpdesk.Application/DraftReply/DraftReplyService.cs`, add this method to the class:

```csharp
    public async Task RedraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId);
            if (ticket is null)
            {
                logger.LogWarning("Cannot redraft a reply for ticket {TicketId}: not found.", ticketId);
                return;
            }

            if (ticket.Messages.Count == 0)
            {
                logger.LogWarning("Cannot redraft a reply for ticket {TicketId}: it has no messages.", ticketId);
                return;
            }

            var transcript = ThreadTranscript.Build(ticket.Messages);
            var category = ticket.Classification?.Category;
            var articles = KbMatcher.Match(ticket.Subject, transcript, category, knowledgeBase.GetAll());

            string? draft;
            try
            {
                draft = await aiService.DraftReplyAsync(ticket.Subject, transcript, category, articles, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to redraft a reply for ticket {TicketId} via AI; clearing the stale draft.", ticketId);
                draft = null;
            }

            if (string.IsNullOrWhiteSpace(draft))
            {
                logger.LogWarning("Redrafting ticket {TicketId} produced no usable draft; clearing the stale draft.", ticketId);
            }

            ticket.DraftReply = string.IsNullOrWhiteSpace(draft) ? null : draft.Trim();
            ticket.Status = TicketStatus.InReview;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;
            await ticketRepository.UpdateAsync(ticket);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to redraft a reply for ticket {TicketId}.", ticketId);
        }
    }
```

- [ ] **Step 6: Implement the fake**

In `tests/Helpdesk.Application.Tests/TestDoubles/FakeDraftReplyService.cs`, replace the file contents with:

```csharp
using Helpdesk.Application.DraftReply;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeDraftReplyService(FakeClassificationService? classifier = null) : IDraftReplyService
{
    public List<Guid> DraftedTicketIds { get; } = [];
    public List<Guid> RedraftedTicketIds { get; } = [];
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>How many tickets the paired classifier had already classified when drafting was called.</summary>
    public int ClassifiedCountAtDraftTime { get; private set; }

    public Task DraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        ClassifiedCountAtDraftTime = classifier?.ClassifiedTicketIds.Count ?? 0;
        DraftedTicketIds.Add(ticketId);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.CompletedTask;
    }

    public Task RedraftReplyAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        RedraftedTicketIds.Add(ticketId);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 7: Run the tests and verify they pass**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~DraftReplyServiceTests"`
Expected: all pass (existing + new).

- [ ] **Step 8: Build the whole solution**

Run: `dotnet build Helpdesk.slnx`
Expected: builds clean.

- [ ] **Step 9: Commit**

```bash
git add src/Helpdesk.Application/DraftReply/ tests/Helpdesk.Application.Tests/TestDoubles/FakeDraftReplyService.cs tests/Helpdesk.Application.Tests/DraftReply/DraftReplyServiceTests.cs
git commit -m "feat: add unconditional full-thread redraft for reopen flow"
```

---

## Task 3: Reopen orchestration in `EmailIngestionService`

**Files:**
- Modify: `src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs`
- Test: `tests/Helpdesk.Application.Tests/EmailIngestion/EmailIngestionServiceTests.cs`

**Interfaces:**
- Consumes: `IClassificationService.ReclassifyTicketAsync` (Task 1), `IDraftReplyService.RedraftReplyAsync` (Task 2), `IReviewFlagService.EvaluateAsync` (unchanged), `FakeClassificationService.ReclassifiedTicketIds`, `FakeDraftReplyService.RedraftedTicketIds` (both from Tasks 1-2).
- Produces: nothing consumed by later tasks (this is the last code task).

- [ ] **Step 1: Update the existing test that today asserts a `Replied` ticket's status is unchanged**

In `tests/Helpdesk.Application.Tests/EmailIngestion/EmailIngestionServiceTests.cs`, replace the test
`IngestNewEmailsAsync_ExistingConversation_AppendsMessageAndLeavesStatusUnchanged` (it will start
failing once Step 4 below ships, because a reply to a `Replied` ticket now reopens it) with:

```csharp
[Fact]
public async Task IngestNewEmailsAsync_ExistingConversationWasReplied_ReopensToInReview()
{
    var existingTicket = new Helpdesk.Core.Entities.Ticket
    {
        Id = Guid.NewGuid(),
        Subject = "Original subject",
        RequesterEmail = "requester@example.com",
        Status = TicketStatus.Replied,
        ConversationId = "conv-1",
        AssignedUserId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
    };
    var assignedUserId = existingTicket.AssignedUserId;

    var reply = new InboundEmailMessage(
        ExternalMessageId: "msg-2",
        ConversationId: "conv-1",
        FromAddress: "requester@example.com",
        Subject: "RE: Original subject",
        BodyHtml: "<p>Still broken</p>",
        ReceivedAt: DateTimeOffset.UtcNow);

    var mailClient = new FakeMailClient(reply);
    var ticketRepository = new FakeTicketRepository();
    ticketRepository.Tickets.Add(existingTicket);
    var messageRepository = new FakeMessageRepository();
    var scopeFactory = new FakeServiceScopeFactory(ticketRepository, messageRepository);
    var service = new EmailIngestionService(mailClient, scopeFactory, NullLogger<EmailIngestionService>.Instance);

    await service.IngestNewEmailsAsync();

    Assert.Single(ticketRepository.Tickets);
    Assert.Equal(TicketStatus.InReview, existingTicket.Status);
    Assert.Equal(assignedUserId, existingTicket.AssignedUserId);

    var message = Assert.Single(messageRepository.Messages);
    Assert.Equal(existingTicket.Id, message.TicketId);
    Assert.Equal("msg-2", message.ExternalMessageId);
}
```

- [ ] **Step 2: Add tests for the new reopen orchestration**

Add to the same file (inside the `EmailIngestionServiceTests` class):

```csharp
[Fact]
public async Task IngestNewEmailsAsync_ReplyToRepliedTicket_ReclassifiesRedraftsAndReviews()
{
    var existingTicket = new Ticket
    {
        Id = Guid.NewGuid(),
        Subject = "Original subject",
        RequesterEmail = "requester@example.com",
        Status = TicketStatus.Replied,
        ConversationId = "conv-1",
    };
    var ticketRepository = new FakeTicketRepository();
    ticketRepository.Tickets.Add(existingTicket);
    var classifier = new FakeClassificationService();
    var drafter = new FakeDraftReplyService();
    var reviewer = new FakeReviewFlagService();
    var scopeFactory = new FakeServiceScopeFactory(
        ticketRepository, new FakeMessageRepository(), classifier, drafter, reviewer);
    var service = new EmailIngestionService(
        new FakeMailClient(Email("msg-2", "conv-1")), scopeFactory, NullLogger<EmailIngestionService>.Instance);

    await service.IngestNewEmailsAsync();

    Assert.Equal(existingTicket.Id, Assert.Single(classifier.ReclassifiedTicketIds));
    Assert.Equal(existingTicket.Id, Assert.Single(drafter.RedraftedTicketIds));
    Assert.Equal(existingTicket.Id, Assert.Single(reviewer.ReviewedTicketIds));
    Assert.Empty(classifier.ClassifiedTicketIds);
    Assert.Empty(drafter.DraftedTicketIds);
}

[Fact]
public async Task IngestNewEmailsAsync_ReplyToInReviewTicket_StaysInReviewAndReopens()
{
    var existingTicket = new Ticket
    {
        Id = Guid.NewGuid(),
        Subject = "Original subject",
        RequesterEmail = "requester@example.com",
        Status = TicketStatus.InReview,
        ConversationId = "conv-1",
    };
    var ticketRepository = new FakeTicketRepository();
    ticketRepository.Tickets.Add(existingTicket);
    var classifier = new FakeClassificationService();
    var drafter = new FakeDraftReplyService();
    var scopeFactory = new FakeServiceScopeFactory(
        ticketRepository, new FakeMessageRepository(), classifier, drafter);
    var service = new EmailIngestionService(
        new FakeMailClient(Email("msg-2", "conv-1")), scopeFactory, NullLogger<EmailIngestionService>.Instance);

    await service.IngestNewEmailsAsync();

    Assert.Equal(TicketStatus.InReview, existingTicket.Status);
    Assert.Equal(existingTicket.Id, Assert.Single(classifier.ReclassifiedTicketIds));
    Assert.Equal(existingTicket.Id, Assert.Single(drafter.RedraftedTicketIds));
}

[Fact]
public async Task IngestNewEmailsAsync_ReplyToNewTicket_DoesNotReopen()
{
    var existingTicket = new Ticket
    {
        Id = Guid.NewGuid(),
        Subject = "Original subject",
        RequesterEmail = "requester@example.com",
        Status = TicketStatus.New,
        ConversationId = "conv-1",
    };
    var ticketRepository = new FakeTicketRepository();
    ticketRepository.Tickets.Add(existingTicket);
    var classifier = new FakeClassificationService();
    var drafter = new FakeDraftReplyService();
    var scopeFactory = new FakeServiceScopeFactory(
        ticketRepository, new FakeMessageRepository(), classifier, drafter);
    var service = new EmailIngestionService(
        new FakeMailClient(Email("msg-2", "conv-1")), scopeFactory, NullLogger<EmailIngestionService>.Instance);

    await service.IngestNewEmailsAsync();

    Assert.Equal(TicketStatus.New, existingTicket.Status);
    Assert.Empty(classifier.ReclassifiedTicketIds);
    Assert.Empty(drafter.RedraftedTicketIds);
}

[Fact]
public async Task IngestNewEmailsAsync_TwoRepliesInSameTick_SecondSeesReopenedThread()
{
    var existingTicket = new Ticket
    {
        Id = Guid.NewGuid(),
        Subject = "Original subject",
        RequesterEmail = "requester@example.com",
        Status = TicketStatus.Replied,
        ConversationId = "conv-1",
    };
    var ticketRepository = new FakeTicketRepository();
    ticketRepository.Tickets.Add(existingTicket);
    var classifier = new FakeClassificationService();
    var drafter = new FakeDraftReplyService();
    var mailClient = new FakeMailClient(Email("msg-2", "conv-1"), Email("msg-3", "conv-1"));
    var scopeFactory = new FakeServiceScopeFactory(
        ticketRepository, new FakeMessageRepository(), classifier, drafter);
    var service = new EmailIngestionService(mailClient, scopeFactory, NullLogger<EmailIngestionService>.Instance);

    await service.IngestNewEmailsAsync();

    Assert.Equal(TicketStatus.InReview, existingTicket.Status);
    Assert.Equal(2, classifier.ReclassifiedTicketIds.Count);
    Assert.Equal(2, drafter.RedraftedTicketIds.Count);
}

[Fact]
public async Task IngestNewEmailsAsync_NoClassificationOrDraftServiceRegistered_ReopenStillFlipsStatus()
{
    var existingTicket = new Ticket
    {
        Id = Guid.NewGuid(),
        Subject = "Original subject",
        RequesterEmail = "requester@example.com",
        Status = TicketStatus.Replied,
        ConversationId = "conv-1",
    };
    var ticketRepository = new FakeTicketRepository();
    ticketRepository.Tickets.Add(existingTicket);
    var scopeFactory = new FakeServiceScopeFactory(ticketRepository, new FakeMessageRepository());
    var service = new EmailIngestionService(
        new FakeMailClient(Email("msg-2", "conv-1")), scopeFactory, NullLogger<EmailIngestionService>.Instance);

    await service.IngestNewEmailsAsync();

    Assert.Equal(TicketStatus.InReview, existingTicket.Status);
}
```

- [ ] **Step 3: Run the new/updated tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~EmailIngestionServiceTests"`
Expected: the updated `ReopensToInReview` test and the new reopen tests fail (status still `Replied`/no reclassify-redraft calls); other existing tests still pass.

- [ ] **Step 4: Implement the reopen orchestration**

Replace the contents of `src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs` with:

```csharp
using Helpdesk.Application.Classification;
using Helpdesk.Application.DraftReply;
using Helpdesk.Application.Review;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.EmailIngestion;

public class EmailIngestionService(
    IMailClient mailClient,
    IServiceScopeFactory scopeFactory,
    ILogger<EmailIngestionService> logger)
{
    /// <summary>Result of processing one inbound email: which ticket it landed on, and which pipeline (if any) it needs.</summary>
    private readonly record struct EmailProcessingResult(Guid TicketId, bool IsNewTicket, bool NeedsReopen);

    public async Task IngestNewEmailsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<InboundEmailMessage> emails;
        try
        {
            emails = await mailClient.FetchNewMessagesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch new emails from the mail client.");
            return;
        }

        foreach (var email in emails)
        {
            try
            {
                // Each message gets its own DI scope (and therefore its own HelpdeskDbContext),
                // so a persistence failure for one message (e.g. a constraint violation) can't
                // leave a poisoned entity in a shared change tracker that then blocks every
                // subsequent message in the same tick.
                using var scope = scopeFactory.CreateScope();
                var ticketRepository = scope.ServiceProvider.GetRequiredService<ITicketRepository>();
                var messageRepository = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

                var result = await ProcessEmailAsync(email, ticketRepository, messageRepository);
                if (result is not { } processed)
                {
                    continue;
                }

                if (processed.IsNewTicket)
                {
                    // Only a brand-new ticket is classified, drafted and review-flagged this way (never a
                    // reply appended to an existing conversation). These services are optional: they are
                    // only registered when OpenRouter is configured, so ingestion keeps working without
                    // AI. Drafting runs after classification so the drafter can use the stored category.
                    // The drafter gets its own fresh scope (and DbContext) so a failed classification
                    // save, whose Added entity would stay tracked and be retried, cannot poison the
                    // drafter's change tracker: the two fail independently.
                    if (scope.ServiceProvider.GetService<IClassificationService>() is { } classifier)
                    {
                        await classifier.ClassifyTicketAsync(processed.TicketId, cancellationToken);
                    }

                    using var draftScope = scopeFactory.CreateScope();
                    if (draftScope.ServiceProvider.GetService<IDraftReplyService>() is { } drafter)
                    {
                        await drafter.DraftReplyAsync(processed.TicketId, cancellationToken);
                    }

                    // Review flags are derived from what classification and drafting actually stored, so they
                    // run last, in their own scope for the same isolation reason as the drafter.
                    using var reviewScope = scopeFactory.CreateScope();
                    if (reviewScope.ServiceProvider.GetService<IReviewFlagService>() is { } reviewer)
                    {
                        await reviewer.EvaluateAsync(processed.TicketId, cancellationToken);
                    }
                }
                else if (processed.NeedsReopen)
                {
                    // A customer reply reopened an InReview/Replied ticket (its status was already
                    // flipped Replied -> InReview inside ProcessEmailAsync, unconditionally, so the
                    // ticket reappears in the queue even if the AI calls below fail). Reclassify from
                    // the latest message, then redraft from the full thread, then re-evaluate review
                    // flags - same per-step scope isolation and AI-optional gating as the new-ticket path.
                    if (scope.ServiceProvider.GetService<IClassificationService>() is { } classifier)
                    {
                        await classifier.ReclassifyTicketAsync(processed.TicketId, cancellationToken);
                    }

                    using var draftScope = scopeFactory.CreateScope();
                    if (draftScope.ServiceProvider.GetService<IDraftReplyService>() is { } drafter)
                    {
                        await drafter.RedraftReplyAsync(processed.TicketId, cancellationToken);
                    }

                    using var reviewScope = scopeFactory.CreateScope();
                    if (reviewScope.ServiceProvider.GetService<IReviewFlagService>() is { } reviewer)
                    {
                        await reviewer.EvaluateAsync(processed.TicketId, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to process email {ExternalMessageId} (conversation {ConversationId}).",
                    email.ExternalMessageId,
                    email.ConversationId);
            }
        }
    }

    private async Task<EmailProcessingResult?> ProcessEmailAsync(
        InboundEmailMessage email,
        ITicketRepository ticketRepository,
        IMessageRepository messageRepository)
    {
        var existingMessage = await messageRepository.GetByExternalMessageIdAsync(email.ExternalMessageId);
        if (existingMessage is not null)
        {
            return null;
        }

        var ticket = await ticketRepository.GetByConversationIdAsync(email.ConversationId);

        bool isNewTicket;
        bool needsReopen;

        if (ticket is null)
        {
            ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                Subject = email.Subject,
                RequesterEmail = email.FromAddress,
                Status = TicketStatus.New,
                ConversationId = email.ConversationId,
                CreatedAt = email.ReceivedAt,
                UpdatedAt = email.ReceivedAt,
            };
            await ticketRepository.AddAsync(ticket);
            isNewTicket = true;
            needsReopen = false;
        }
        else
        {
            isNewTicket = false;
            // A reply is only "reopen-eligible" if the ticket had already gone through the
            // first-message pipeline (InReview or Replied). A reply arriving while the ticket is
            // still New (that pipeline mid-flight or previously failed) is left alone, matching
            // today's behavior: the message is stored, nothing else happens.
            needsReopen = ticket.Status is TicketStatus.InReview or TicketStatus.Replied;

            // Applied unconditionally, independent of whether reclassify/redraft below succeed, so
            // the ticket reliably reappears in the Queue/Mine views even on an AI failure.
            if (ticket.Status == TicketStatus.Replied)
            {
                ticket.Status = TicketStatus.InReview;
            }

            ticket.UpdatedAt = email.ReceivedAt;
            await ticketRepository.UpdateAsync(ticket);
        }

        var message = new Message
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            Sender = email.FromAddress,
            Body = email.BodyHtml,
            IsFromUser = true,
            ExternalMessageId = email.ExternalMessageId,
            ReceivedAt = email.ReceivedAt,
        };
        await messageRepository.AddAsync(message);

        await mailClient.MarkAsProcessedAsync(email.ExternalMessageId);
        return new EmailProcessingResult(ticket.Id, isNewTicket, needsReopen);
    }
}
```

- [ ] **Step 5: Run the full Application test project**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj`
Expected: all tests pass, including every pre-existing `EmailIngestionServiceTests` test (the
`DoesNotClassifyAgain`/`DoesNotDraftAgain`/`DoesNotReviewAgain` tests use tickets with the default
`Status = TicketStatus.New`, so `NeedsReopen` is `false` for them and they are unaffected).

- [ ] **Step 6: Run the whole solution's tests**

Run: `dotnet test Helpdesk.slnx`
Expected: all projects pass.

- [ ] **Step 7: Commit**

```bash
git add src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs tests/Helpdesk.Application.Tests/EmailIngestion/EmailIngestionServiceTests.cs
git commit -m "feat: reopen InReview/Replied tickets and re-draft on a customer reply"
```

---

## Task 4: E2E coverage

**Files:**
- Investigate: `e2e/` (Playwright suite; currently only `e2e/tests/auth/*` exists — no ticket-workflow E2E suite yet).

**Interfaces:**
- Consumes: whatever ticket-workflow E2E fixtures/helpers the `qa-engineer` subagent determines exist or need adding (test DB seeding, mocked/stubbed `IAiService`/`IMailClient` per its own conventions — see `.claude/agents/qa-engineer.md`).
- Produces: nothing consumed by other tasks (final task).

- [ ] **Step 1: Dispatch to the `qa-engineer` subagent**

Per `CLAUDE.md`'s E2E section, ticket-workflow Playwright infrastructure is the `qa-engineer`
subagent's responsibility, not ad hoc. Give it this scenario to add (creating whatever
mail-client/AI stubs and ticket-seeding helpers its existing conventions call for, since no
ticket-reply E2E suite exists yet in this repo to extend):

> A ticket that is `Replied` (with a known assignee) receives a new customer email on the same
> conversation thread. After the ingestion poll processes it: the ticket's status is `InReview`
> again; it reappears in that assignee's `Mine` queue view; `AssignedUserId` is unchanged from
> before the reply; and its stored `DraftReply` differs from whatever it was before the reply
> (proving a redraft happened, not just a stale value surviving).

- [ ] **Step 2: Record the outcome**

If the subagent adds a passing E2E test, note its file path here for the branch review. If it
determines the existing E2E harness cannot yet exercise ingestion end-to-end (e.g. no test hook for
triggering `EmailIngestionService` against a fake mailbox) without disproportionate new
infrastructure, record that explicitly as a known gap for a follow-up phase — this must not block
the rest of Phase 7b, since it's already covered by Task 3's unit tests at the service level.

- [ ] **Step 3: Commit whatever the subagent produced (if anything)**

```bash
git add e2e/
git commit -m "test: add E2E coverage for reopening a replied ticket on a customer reply"
```

(Skip this step, with a one-line note instead, if Step 2 concluded there was nothing safe to add yet.)
