# Phase 8 — Low-Confidence Escalation (Review Flags) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** New tickets that the AI is unsure about or could not fully process get a `ReviewReasons` flag set on the ticket (low confidence, classification failed, category Other, draft failed), without changing the normal draft/InReview flow.

**Architecture:** A `[Flags]` enum `ReviewReasons` on `Ticket` (int column, migration). A pure `ReviewPolicy` derives the flags from the persisted classification and draft; `ReviewFlagService` loads the ticket, applies the policy and persists only on change. `EmailIngestionService` runs it last (classify -> draft -> review) for NEW tickets, each in its own DI scope. Threshold is `Review:ConfidenceThreshold` (default 0.7), registered under the existing OpenRouter gate.

**Tech Stack:** .NET 10, EF Core 10 (Npgsql), xunit, hand-written fakes.

**Spec:** `docs/superpowers/specs/2026-09-26-phase8-low-confidence-escalation-design.md`

## Global Constraints

- `Helpdesk.Core` has no EF Core / Npgsql / Graph SDK dependency; `Helpdesk.Application` depends only on `Helpdesk.Core`.
- `ReviewReasons` values are a stored, cross-phase contract: `None=0, LowConfidence=1, ClassificationFailed=2, CategoryOther=4, DraftFailed=8`. "Needs review" is `ReviewReasons != None`; there is NO separate bool column.
- Flag rules (`ReviewPolicy`): no classification -> `ClassificationFailed` (and no confidence/category checks); otherwise confidence `<` threshold -> `LowConfidence` (equal does NOT flag) and category equal to `TicketCategories.Other` (case-insensitive) -> `CategoryOther`; blank/null draft -> `DraftFailed`. Reasons combine with OR.
- Threshold: `Review:ConfidenceThreshold`, invariant-culture double, default `0.7`, must be within `[0, 1]`; anything else throws `InvalidOperationException` at registration, but ONLY when the OpenRouter gate is on.
- `IReviewFlagService` and `ReviewOptions` are registered only under the existing `IsOpenRouterConfigured` gate in `Helpdesk.Application/DependencyInjection.cs`; the host must still start with OpenRouter absent.
- Reviewing never fails ingestion: exceptions are logged and swallowed; only cancellation (`OperationCanceledException` while the token is cancelled) propagates. It never modifies `Status`, `DraftReply` or `Classification`.
- Only NEW tickets are reviewed; the reviewer runs after the drafter, in its OWN `scopeFactory.CreateScope()` (optional `GetService`), like the drafter.
- Flags are set once at ingestion; existing tickets are not backfilled (column default 0).
- No HTTP/API surface and no frontend in this plan (Phase 7 owns DTOs/queue; the badge is task 48, a follow-up after Phase 7 merges).
- Follow existing test style: xunit, hand-written fakes in `tests/Helpdesk.Application.Tests/TestDoubles`, no mocking library.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`. Work stays on branch `phase8-escalation` in worktree `D:/Rajendra/Claude/Learning/HELPDESK-phase8`; do not merge or push. The migration is generated but NOT applied to the dev DB by implementers.
- Never put secrets in the chat, repo files or memory. `dotnet ef` commands run with `OpenRouter__Enabled=false` in the environment so they do not need the API key.

## Review Focus

Failure modes the spec implies but no obvious test would catch; each has a test in the owning task:
1. Threshold boundary and bad config: confidence exactly equal to the threshold is not flagged; `abc`, `-0.1`, `1.5`, `NaN`, `Infinity`, `0,7` all fail fast (a comma decimal must not silently become 7) (Task 2).
2. Category `Other` in different casings, and combinations (low confidence + Other + draft failed) (Task 2).
3. Re-evaluating an unchanged ticket writes nothing; stale flags are cleared when the state no longer warrants them; the reviewer never touches Status/DraftReply/Classification (Task 3).
4. AI disabled: nothing registered, and an invalid threshold is ignored when the OpenRouter gate is off (Task 3).
5. A reviewer exception or cancellation during ingestion must not stop the batch, un-mark the message, or share the drafter's scope (Task 4).
6. The migration default: existing rows must become `0`, i.e. the column is NOT NULL with default 0 (Task 1).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Helpdesk.Core/Enums/ReviewReasons.cs` (new) | flags enum (stored contract) |
| `src/Helpdesk.Core/Entities/Ticket.cs` (mod) | `ReviewReasons ReviewReasons` property |
| `src/Helpdesk.Infrastructure/Data/Configurations/TicketConfiguration.cs` (mod) | column default 0 |
| `src/Helpdesk.Infrastructure/Data/Migrations/*_AddTicketReviewReasons*` (new, generated) | schema change |
| `src/Helpdesk.Application/Review/ReviewOptions.cs` (new) | threshold + `Parse` |
| `src/Helpdesk.Application/Review/ReviewPolicy.cs` (new) | pure flag derivation |
| `src/Helpdesk.Application/Review/IReviewFlagService.cs`, `ReviewFlagService.cs` (new) | load -> policy -> persist on change |
| `src/Helpdesk.Application/DependencyInjection.cs` (mod) | register under the OpenRouter gate |
| `src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs` (mod) | call reviewer after drafter |
| `tests/...` | one test file per new unit + fake updates |
| `CLAUDE.md`, `implementation-plan.md` (mod) | docs |

---

### Task 1: `ReviewReasons`, `Ticket.ReviewReasons`, EF column and migration

**Files:**
- Create: `src/Helpdesk.Core/Enums/ReviewReasons.cs`
- Modify: `src/Helpdesk.Core/Entities/Ticket.cs`
- Modify: `src/Helpdesk.Infrastructure/Data/Configurations/TicketConfiguration.cs`
- Create (generated): `src/Helpdesk.Infrastructure/Data/Migrations/<timestamp>_AddTicketReviewReasons.cs` (+ `.Designer.cs`, updated `HelpdeskDbContextModelSnapshot.cs`)
- Test: `tests/Helpdesk.Core.Tests/ReviewReasonsTests.cs`, `tests/Helpdesk.Infrastructure.Tests/Data/TicketModelTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `[Flags] public enum ReviewReasons { None = 0, LowConfidence = 1, ClassificationFailed = 2, CategoryOther = 4, DraftFailed = 8 }` in `Helpdesk.Core.Enums`; `public ReviewReasons ReviewReasons { get; set; } = ReviewReasons.None;` on `Ticket`.

- [ ] **Step 1: Write the failing tests**

`tests/Helpdesk.Core.Tests/ReviewReasonsTests.cs`:
```csharp
using Helpdesk.Core.Enums;

namespace Helpdesk.Core.Tests;

public class ReviewReasonsTests
{
    // These numeric values are stored in the database and exposed by Phase 7 DTOs: never renumber.
    [Theory]
    [InlineData(ReviewReasons.None, 0)]
    [InlineData(ReviewReasons.LowConfidence, 1)]
    [InlineData(ReviewReasons.ClassificationFailed, 2)]
    [InlineData(ReviewReasons.CategoryOther, 4)]
    [InlineData(ReviewReasons.DraftFailed, 8)]
    public void Values_AreTheStoredContract(ReviewReasons reason, int expected)
    {
        Assert.Equal(expected, (int)reason);
    }

    [Fact]
    public void Reasons_CombineAsFlags()
    {
        var combined = ReviewReasons.LowConfidence | ReviewReasons.DraftFailed;

        Assert.True(combined.HasFlag(ReviewReasons.LowConfidence));
        Assert.True(combined.HasFlag(ReviewReasons.DraftFailed));
        Assert.False(combined.HasFlag(ReviewReasons.CategoryOther));
        Assert.Equal(9, (int)combined);
    }
}
```
`tests/Helpdesk.Infrastructure.Tests/Data/TicketModelTests.cs`:
```csharp
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Helpdesk.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Helpdesk.Infrastructure.Tests.Data;

public class TicketModelTests
{
    // Only the model is inspected; nothing connects to the database.
    private static IProperty ReviewReasonsProperty()
    {
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var context = new HelpdeskDbContext(options);
        return context.Model.FindEntityType(typeof(Ticket))!.FindProperty(nameof(Ticket.ReviewReasons))!;
    }

    [Fact]
    public void ReviewReasons_IsARequiredColumnThatDefaultsToNone()
    {
        var property = ReviewReasonsProperty();

        Assert.Equal(typeof(ReviewReasons), property.ClrType);
        Assert.False(property.IsNullable);
        Assert.Equal(0, Convert.ToInt32(property.GetDefaultValue()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Core.Tests/Helpdesk.Core.Tests.csproj --filter "FullyQualifiedName~ReviewReasonsTests"`
Expected: build FAIL — `ReviewReasons` does not exist.

(Note for Step 4: if `property.GetDefaultValue()` returns null on `context.Model` because the runtime model drops relational annotations, read the property from the design-time model instead: `context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model` (`using Microsoft.EntityFrameworkCore.Infrastructure;`). Keep the assertions the same.)

- [ ] **Step 3: Implement the enum, property and EF configuration**

`src/Helpdesk.Core/Enums/ReviewReasons.cs`:
```csharp
namespace Helpdesk.Core.Enums;

/// <summary>
/// Why a ticket was tagged for manual review. Stored as an int and exposed by the API: the numeric
/// values are a contract, so never renumber them. A ticket "needs review" when this is not None.
/// </summary>
[Flags]
public enum ReviewReasons
{
    None = 0,
    LowConfidence = 1,
    ClassificationFailed = 2,
    CategoryOther = 4,
    DraftFailed = 8,
}
```
In `src/Helpdesk.Core/Entities/Ticket.cs` add, after the `Status` property:
```csharp
    public ReviewReasons ReviewReasons { get; set; } = ReviewReasons.None;
```
(`using Helpdesk.Core.Enums;` is already imported there.)

In `src/Helpdesk.Infrastructure/Data/Configurations/TicketConfiguration.cs` add `using Helpdesk.Core.Enums;` and, after the `ConversationId` index line, add:
```csharp
        builder.Property(t => t.ReviewReasons).HasDefaultValue(ReviewReasons.None);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Helpdesk.Core.Tests/Helpdesk.Core.Tests.csproj --filter "FullyQualifiedName~ReviewReasonsTests"` then `dotnet test tests/Helpdesk.Infrastructure.Tests/Helpdesk.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TicketModelTests"`
Expected: PASS (6 + 1 tests).

- [ ] **Step 5: Generate the migration (do NOT apply it)**

Run from the worktree root (bash):
```
OpenRouter__Enabled=false dotnet ef migrations add AddTicketReviewReasons --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Api --output-dir Data/Migrations
```
Review the generated `Up()`: it must contain exactly one `AddColumn<int>(name: "ReviewReasons", table: "Tickets", type: "integer", nullable: false, defaultValue: 0)` and `Down()` the matching `DropColumn`. If it contains anything else (other tables/columns), stop and report BLOCKED. Then verify the snapshot is in sync:
```
OpenRouter__Enabled=false dotnet ef migrations has-pending-model-changes --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Api
```
Expected: exit code 0 / "No changes have been made to the model since the last migration."

- [ ] **Step 6: Full test run and commit**

Run: `dotnet build Helpdesk.slnx` then `dotnet test Helpdesk.slnx`
Expected: 0 errors; all tests pass.
```bash
git add src/Helpdesk.Core/Enums/ReviewReasons.cs src/Helpdesk.Core/Entities/Ticket.cs src/Helpdesk.Infrastructure/Data tests/Helpdesk.Core.Tests/ReviewReasonsTests.cs tests/Helpdesk.Infrastructure.Tests/Data/TicketModelTests.cs
git commit -m "feat: add Ticket.ReviewReasons flags column and migration

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: `ReviewOptions` and `ReviewPolicy`

**Files:**
- Create: `src/Helpdesk.Application/Review/ReviewOptions.cs`
- Create: `src/Helpdesk.Application/Review/ReviewPolicy.cs`
- Test: `tests/Helpdesk.Application.Tests/Review/ReviewOptionsTests.cs`, `tests/Helpdesk.Application.Tests/Review/ReviewPolicyTests.cs`

**Interfaces:**
- Consumes: `ReviewReasons` (Task 1), existing `TicketCategories.Other`, `Helpdesk.Core.Entities.Classification`.
- Produces: `public sealed record ReviewOptions(double ConfidenceThreshold)` with `public const string ConfigKey = "Review:ConfidenceThreshold"`, `public const double DefaultConfidenceThreshold = 0.7`, `public static ReviewOptions Parse(string? value)` (null/blank -> default; else invariant double in `[0, 1]` or throws `InvalidOperationException` mentioning the key); `public static class ReviewPolicy` with `public static ReviewReasons Evaluate(Helpdesk.Core.Entities.Classification? classification, string? draftReply, double threshold)`. Both in namespace `Helpdesk.Application.Review`.

- [ ] **Step 1: Write the failing tests**

`tests/Helpdesk.Application.Tests/Review/ReviewOptionsTests.cs`:
```csharp
using Helpdesk.Application.Review;

namespace Helpdesk.Application.Tests.Review;

public class ReviewOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_MissingValue_UsesDefaultOfPointSeven(string? value)
    {
        Assert.Equal(0.7, ReviewOptions.Parse(value).ConfidenceThreshold);
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("0", 0.0)]
    [InlineData("1", 1.0)]
    [InlineData(" 0.85 ", 0.85)]
    public void Parse_ValidValue_IsUsed(string value, double expected)
    {
        Assert.Equal(expected, ReviewOptions.Parse(value).ConfidenceThreshold);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0,7")]
    public void Parse_InvalidValue_ThrowsMentioningTheKey(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ReviewOptions.Parse(value));

        Assert.Contains("Review:ConfidenceThreshold", ex.Message);
    }
}
```
`tests/Helpdesk.Application.Tests/Review/ReviewPolicyTests.cs`:
```csharp
using Helpdesk.Application.Review;
using Helpdesk.Core.Enums;
using ClassificationEntity = Helpdesk.Core.Entities.Classification;

namespace Helpdesk.Application.Tests.Review;

public class ReviewPolicyTests
{
    private const double Threshold = 0.7;
    private const string Draft = "Hello, we are looking into it.";

    private static ClassificationEntity Cls(string category, double confidence) => new()
    {
        Id = Guid.NewGuid(),
        TicketId = Guid.NewGuid(),
        Category = category,
        Summary = "s",
        Confidence = confidence,
    };

    [Fact]
    public void Evaluate_ConfidentKnownCategoryWithDraft_IsNone()
    {
        Assert.Equal(ReviewReasons.None, ReviewPolicy.Evaluate(Cls("Billing", 0.95), Draft, Threshold));
    }

    [Fact]
    public void Evaluate_NoClassificationButDraft_IsClassificationFailedOnly()
    {
        Assert.Equal(ReviewReasons.ClassificationFailed, ReviewPolicy.Evaluate(null, Draft, Threshold));
    }

    [Fact]
    public void Evaluate_NoClassificationAndNoDraft_IsClassificationFailedAndDraftFailed()
    {
        Assert.Equal(
            ReviewReasons.ClassificationFailed | ReviewReasons.DraftFailed,
            ReviewPolicy.Evaluate(null, null, Threshold));
    }

    [Fact]
    public void Evaluate_ConfidenceBelowThreshold_IsLowConfidence()
    {
        Assert.Equal(ReviewReasons.LowConfidence, ReviewPolicy.Evaluate(Cls("Billing", 0.69), Draft, Threshold));
    }

    [Fact]
    public void Evaluate_ConfidenceEqualToThreshold_IsNotFlagged()
    {
        Assert.Equal(ReviewReasons.None, ReviewPolicy.Evaluate(Cls("Billing", 0.7), Draft, 0.7));
    }

    [Fact]
    public void Evaluate_ConfidenceAboveThreshold_IsNotFlagged()
    {
        Assert.Equal(ReviewReasons.None, ReviewPolicy.Evaluate(Cls("Billing", 0.71), Draft, Threshold));
    }

    [Fact]
    public void Evaluate_ThresholdZero_NeverFlagsLowConfidence()
    {
        Assert.Equal(ReviewReasons.None, ReviewPolicy.Evaluate(Cls("Billing", 0.0), Draft, 0.0));
    }

    [Fact]
    public void Evaluate_ThresholdOne_FlagsAnythingBelowCertainty()
    {
        Assert.Equal(ReviewReasons.LowConfidence, ReviewPolicy.Evaluate(Cls("Billing", 0.99), Draft, 1.0));
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("other")]
    [InlineData("OTHER")]
    public void Evaluate_CategoryOther_IsCategoryOtherRegardlessOfCase(string category)
    {
        Assert.Equal(ReviewReasons.CategoryOther, ReviewPolicy.Evaluate(Cls(category, 0.95), Draft, Threshold));
    }

    [Fact]
    public void Evaluate_LowConfidenceOtherAndNoDraft_CombinesAllThree()
    {
        Assert.Equal(
            ReviewReasons.LowConfidence | ReviewReasons.CategoryOther | ReviewReasons.DraftFailed,
            ReviewPolicy.Evaluate(Cls("Other", 0.2), null, Threshold));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Evaluate_BlankDraft_IsDraftFailed(string? draft)
    {
        Assert.Equal(ReviewReasons.DraftFailed, ReviewPolicy.Evaluate(Cls("Billing", 0.95), draft, Threshold));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~Review"`
Expected: build FAIL — `ReviewOptions` / `ReviewPolicy` do not exist.

- [ ] **Step 3: Implement**

`src/Helpdesk.Application/Review/ReviewOptions.cs`:
```csharp
using System.Globalization;

namespace Helpdesk.Application.Review;

/// <summary>Settings for review flagging. The threshold is compared with the classification confidence.</summary>
public sealed record ReviewOptions(double ConfidenceThreshold)
{
    public const string ConfigKey = "Review:ConfidenceThreshold";
    public const double DefaultConfidenceThreshold = 0.7;

    /// <summary>
    /// Parses the raw configuration value: missing/blank gives the default; otherwise it must be an
    /// invariant-culture number in [0, 1] (so "0,7" is rejected rather than read as 7). A bad value throws
    /// so a typo fails at startup instead of silently changing which tickets are flagged.
    /// </summary>
    public static ReviewOptions Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ReviewOptions(DefaultConfidenceThreshold);
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            || double.IsNaN(threshold)
            || threshold < 0
            || threshold > 1)
        {
            throw new InvalidOperationException($"{ConfigKey} must be a number between 0 and 1 (was '{value}').");
        }

        return new ReviewOptions(threshold);
    }
}
```
`src/Helpdesk.Application/Review/ReviewPolicy.cs`:
```csharp
using Helpdesk.Core.Enums;
using Helpdesk.Core.Models;
using ClassificationEntity = Helpdesk.Core.Entities.Classification;

namespace Helpdesk.Application.Review;

/// <summary>Derives why a ticket needs manual review from what was actually persisted for it.</summary>
public static class ReviewPolicy
{
    public static ReviewReasons Evaluate(ClassificationEntity? classification, string? draftReply, double threshold)
    {
        var reasons = ReviewReasons.None;

        if (classification is null)
        {
            reasons |= ReviewReasons.ClassificationFailed;
        }
        else
        {
            if (classification.Confidence < threshold)
            {
                reasons |= ReviewReasons.LowConfidence;
            }

            if (string.Equals(classification.Category, TicketCategories.Other, StringComparison.OrdinalIgnoreCase))
            {
                reasons |= ReviewReasons.CategoryOther;
            }
        }

        if (string.IsNullOrWhiteSpace(draftReply))
        {
            reasons |= ReviewReasons.DraftFailed;
        }

        return reasons;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj`
Expected: PASS (all, including the existing tests).

- [ ] **Step 5: Commit**

```bash
git add src/Helpdesk.Application/Review tests/Helpdesk.Application.Tests/Review
git commit -m "feat: add ReviewOptions and ReviewPolicy for review flags

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: `ReviewFlagService` and DI registration

**Files:**
- Create: `src/Helpdesk.Application/Review/IReviewFlagService.cs`
- Create: `src/Helpdesk.Application/Review/ReviewFlagService.cs`
- Modify: `src/Helpdesk.Application/DependencyInjection.cs`
- Modify: `tests/Helpdesk.Application.Tests/TestDoubles/FakeTicketRepository.cs` (count updates)
- Modify: `tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj` (add configuration package)
- Test: `tests/Helpdesk.Application.Tests/Review/ReviewFlagServiceTests.cs`, `tests/Helpdesk.Application.Tests/Review/ReviewDependencyInjectionTests.cs`

**Interfaces:**
- Consumes: `ReviewPolicy.Evaluate`, `ReviewOptions` / `ReviewOptions.Parse` / `ReviewOptions.ConfigKey` (Task 2), `ReviewReasons` and `Ticket.ReviewReasons` (Task 1), `ITicketRepository.GetByIdAsync/UpdateAsync`.
- Produces: `public interface IReviewFlagService { Task EvaluateAsync(Guid ticketId, CancellationToken cancellationToken = default); }` and `public class ReviewFlagService(ITicketRepository ticketRepository, ReviewOptions options, ILogger<ReviewFlagService> logger) : IReviewFlagService` in `Helpdesk.Application.Review`; `FakeTicketRepository.UpdateCount` (int); `AddApplication` registers `ReviewOptions` (singleton instance) and `IReviewFlagService` (scoped) only under the OpenRouter gate.

- [ ] **Step 1: Extend the fake and add the package**

In `tests/Helpdesk.Application.Tests/TestDoubles/FakeTicketRepository.cs` add a property and count updates: add
```csharp
    public int UpdateCount { get; private set; }
```
and change `UpdateAsync` so it counts before the optional failure (keep the rest of the class):
```csharp
    public Task UpdateAsync(Ticket ticket)
    {
        UpdateCount++;
        if (UpdateException is not null)
        {
            throw UpdateException;
        }

        return Task.CompletedTask;
    }
```
In `tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj` add to the package `ItemGroup` (next to `xunit`):
```xml
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.0" />
```

- [ ] **Step 2: Write the failing tests**

`tests/Helpdesk.Application.Tests/Review/ReviewFlagServiceTests.cs`:
```csharp
using Helpdesk.Application.Review;
using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using ClassificationEntity = Helpdesk.Core.Entities.Classification;

namespace Helpdesk.Application.Tests.Review;

public class ReviewFlagServiceTests
{
    private readonly FakeTicketRepository _tickets = new();

    private ReviewFlagService CreateService(double threshold = 0.7) =>
        new(_tickets, new ReviewOptions(threshold), NullLogger<ReviewFlagService>.Instance);

    private Ticket AddTicket(double? confidence, string category = "Billing", string? draft = "A draft.")
    {
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Charged twice",
            RequesterEmail = "a@example.com",
            Status = TicketStatus.InReview,
            DraftReply = draft,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        if (confidence is { } c)
        {
            ticket.Classification = new ClassificationEntity
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                Category = category,
                Summary = "s",
                Confidence = c,
            };
        }

        _tickets.Tickets.Add(ticket);
        return ticket;
    }

    [Fact]
    public async Task EvaluateAsync_LowConfidence_SetsFlagAndUpdatesOnlyTheFlag()
    {
        var ticket = AddTicket(0.4);
        var classification = ticket.Classification;

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(ReviewReasons.LowConfidence, ticket.ReviewReasons);
        Assert.Equal(1, _tickets.UpdateCount);
        Assert.True(ticket.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(TicketStatus.InReview, ticket.Status);
        Assert.Equal("A draft.", ticket.DraftReply);
        Assert.Same(classification, ticket.Classification);
    }

    [Fact]
    public async Task EvaluateAsync_UsesTheConfiguredThreshold()
    {
        var ticket = AddTicket(0.8);

        await CreateService(threshold: 0.9).EvaluateAsync(ticket.Id);

        Assert.Equal(ReviewReasons.LowConfidence, ticket.ReviewReasons);
    }

    [Fact]
    public async Task EvaluateAsync_CleanTicket_StaysNoneAndIsNotUpdated()
    {
        var ticket = AddTicket(0.95);

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(ReviewReasons.None, ticket.ReviewReasons);
        Assert.Equal(0, _tickets.UpdateCount);
    }

    [Fact]
    public async Task EvaluateAsync_FlagsAlreadyUpToDate_DoesNotUpdateAgain()
    {
        var ticket = AddTicket(0.4);
        ticket.ReviewReasons = ReviewReasons.LowConfidence;

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(0, _tickets.UpdateCount);
    }

    [Fact]
    public async Task EvaluateAsync_StaleFlagsAreClearedWhenNoLongerWarranted()
    {
        var ticket = AddTicket(0.95);
        ticket.ReviewReasons = ReviewReasons.DraftFailed;

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(ReviewReasons.None, ticket.ReviewReasons);
        Assert.Equal(1, _tickets.UpdateCount);
    }

    [Fact]
    public async Task EvaluateAsync_NoClassificationAndNoDraft_SetsBothFailureFlags()
    {
        var ticket = AddTicket(null, draft: null);

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(ReviewReasons.ClassificationFailed | ReviewReasons.DraftFailed, ticket.ReviewReasons);
    }

    [Fact]
    public async Task EvaluateAsync_TicketNotFound_DoesNothing()
    {
        await CreateService().EvaluateAsync(Guid.NewGuid());

        Assert.Equal(0, _tickets.UpdateCount);
    }

    [Fact]
    public async Task EvaluateAsync_PersistenceThrows_Swallows()
    {
        var ticket = AddTicket(0.4);
        _tickets.UpdateException = new InvalidOperationException("db down");

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(1, _tickets.UpdateCount);
    }

    [Fact]
    public async Task EvaluateAsync_CancelledWhileTokenCancelled_Propagates()
    {
        var ticket = AddTicket(0.4);
        _tickets.UpdateException = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateService().EvaluateAsync(ticket.Id, cts.Token));
    }

    [Fact]
    public async Task EvaluateAsync_TimeoutStyleCancellationWithLiveToken_IsSwallowed()
    {
        var ticket = AddTicket(0.4);
        _tickets.UpdateException = new OperationCanceledException();

        await CreateService().EvaluateAsync(ticket.Id);

        Assert.Equal(1, _tickets.UpdateCount);
    }
}
```
`tests/Helpdesk.Application.Tests/Review/ReviewDependencyInjectionTests.cs`:
```csharp
using Helpdesk.Application.Review;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Application.Tests.Review;

public class ReviewDependencyInjectionTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static ServiceCollection Register(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddApplication(configuration);
        return services;
    }

    [Fact]
    public void OpenRouterConfigured_RegistersScopedReviewFlagServiceAndDefaultOptions()
    {
        var services = Register(Config(("OpenRouter:Model", "m")));

        var service = Assert.Single(services, d => d.ServiceType == typeof(IReviewFlagService));
        Assert.Equal(typeof(ReviewFlagService), service.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, service.Lifetime);

        var options = Assert.Single(services, d => d.ServiceType == typeof(ReviewOptions));
        Assert.Equal(0.7, ((ReviewOptions)options.ImplementationInstance!).ConfidenceThreshold);
    }

    [Fact]
    public void ConfiguredThreshold_IsUsed()
    {
        var services = Register(Config(("OpenRouter:Model", "m"), ("Review:ConfidenceThreshold", "0.55")));

        var options = Assert.Single(services, d => d.ServiceType == typeof(ReviewOptions));
        Assert.Equal(0.55, ((ReviewOptions)options.ImplementationInstance!).ConfidenceThreshold);
    }

    [Fact]
    public void InvalidThreshold_WithOpenRouterConfigured_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Register(Config(("OpenRouter:Model", "m"), ("Review:ConfidenceThreshold", "abc"))));

        Assert.Contains("Review:ConfidenceThreshold", ex.Message);
    }

    [Fact]
    public void OpenRouterAbsent_RegistersNothing_EvenWithAnInvalidThreshold()
    {
        var services = Register(Config(("Review:ConfidenceThreshold", "abc")));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IReviewFlagService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ReviewOptions));
    }

    [Fact]
    public void OpenRouterDisabled_RegistersNothing()
    {
        var services = Register(Config(("OpenRouter:Enabled", "false"), ("OpenRouter:Model", "m")));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IReviewFlagService));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~Review"`
Expected: build FAIL — `IReviewFlagService` / `ReviewFlagService` do not exist.

- [ ] **Step 4: Implement the service**

`src/Helpdesk.Application/Review/IReviewFlagService.cs`:
```csharp
namespace Helpdesk.Application.Review;

public interface IReviewFlagService
{
    /// <summary>
    /// Recomputes the ticket's ReviewReasons from its stored classification and draft and persists them
    /// if they changed. Never modifies status, draft or classification. Never throws for lookup or
    /// persistence failures (logs and leaves the ticket as is); only cancellation propagates.
    /// </summary>
    Task EvaluateAsync(Guid ticketId, CancellationToken cancellationToken = default);
}
```
`src/Helpdesk.Application/Review/ReviewFlagService.cs`:
```csharp
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Review;

public class ReviewFlagService(
    ITicketRepository ticketRepository,
    ReviewOptions options,
    ILogger<ReviewFlagService> logger) : IReviewFlagService
{
    public async Task EvaluateAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        try
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId);
            if (ticket is null)
            {
                logger.LogWarning("Cannot evaluate review flags for ticket {TicketId}: not found.", ticketId);
                return;
            }

            var reasons = ReviewPolicy.Evaluate(ticket.Classification, ticket.DraftReply, options.ConfidenceThreshold);
            if (reasons == ticket.ReviewReasons)
            {
                return;
            }

            ticket.ReviewReasons = reasons;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;
            await ticketRepository.UpdateAsync(ticket);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to evaluate review flags for ticket {TicketId}; leaving it unflagged.", ticketId);
        }
    }
}
```

- [ ] **Step 5: Register under the OpenRouter gate**

In `src/Helpdesk.Application/DependencyInjection.cs` add `using Helpdesk.Application.Review;` and, inside the existing `if (IsOpenRouterConfigured(configuration))` block after the `IDraftReplyService` line, add:
```csharp
            // Review flags are derived from the classification and draft, so they only make sense when AI is on:
            // with AI off nothing is classified or drafted and every ticket would look "failed". The threshold is
            // parsed here (fail fast on a typo) and only when the gate is on, so a stray bad value cannot stop a
            // host that has AI disabled from starting.
            services.AddSingleton(ReviewOptions.Parse(configuration[ReviewOptions.ConfigKey]));
            services.AddScoped<IReviewFlagService, ReviewFlagService>();
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj`
Expected: PASS (all, including existing tests).

- [ ] **Step 7: Commit**

```bash
git add src/Helpdesk.Application tests/Helpdesk.Application.Tests
git commit -m "feat: add ReviewFlagService and register it under the OpenRouter gate

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: Ingestion wiring and docs

**Files:**
- Modify: `src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs`
- Create: `tests/Helpdesk.Application.Tests/TestDoubles/FakeReviewFlagService.cs`
- Modify: `tests/Helpdesk.Application.Tests/TestDoubles/FakeServiceScopeFactory.cs`
- Modify: `tests/Helpdesk.Application.Tests/EmailIngestion/EmailIngestionServiceTests.cs`
- Modify: `CLAUDE.md`, `implementation-plan.md`

**Interfaces:**
- Consumes: `IReviewFlagService.EvaluateAsync(Guid, CancellationToken)` (Task 3), existing `FakeDraftReplyService`, `FakeClassificationService`.
- Produces: `FakeReviewFlagService(FakeDraftReplyService? drafter = null)` with `List<Guid> ReviewedTicketIds`, `Exception? ExceptionToThrow`, `int DraftedCountAtReviewTime`; `FakeServiceScopeFactory` ctor gains a 5th optional `IReviewFlagService? reviewFlagService = null` and records its resolutions in `AiServiceResolutions`.

- [ ] **Step 1: Add the fake and extend the scope factory**

`tests/Helpdesk.Application.Tests/TestDoubles/FakeReviewFlagService.cs`:
```csharp
using Helpdesk.Application.Review;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeReviewFlagService(FakeDraftReplyService? drafter = null) : IReviewFlagService
{
    public List<Guid> ReviewedTicketIds { get; } = [];
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>How many tickets the paired drafter had already drafted when reviewing was called.</summary>
    public int DraftedCountAtReviewTime { get; private set; }

    public Task EvaluateAsync(Guid ticketId, CancellationToken cancellationToken = default)
    {
        DraftedCountAtReviewTime = drafter?.DraftedTicketIds.Count ?? 0;
        ReviewedTicketIds.Add(ticketId);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.CompletedTask;
    }
}
```
Replace `tests/Helpdesk.Application.Tests/TestDoubles/FakeServiceScopeFactory.cs` with:
```csharp
using Helpdesk.Application.Classification;
using Helpdesk.Application.DraftReply;
using Helpdesk.Application.Review;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Application.Tests.TestDoubles;

/// <summary>
/// Minimal hand-written fake of IServiceScopeFactory/IServiceScope/IServiceProvider that always
/// resolves the SAME FakeTicketRepository/FakeMessageRepository instances regardless of how many
/// scopes are created. This mirrors production DI shape (a new scope per message) while still
/// letting tests inspect state across all the "scopes" EmailIngestionService creates in one tick.
/// IClassificationService, IDraftReplyService and IReviewFlagService are optional (null = "AI not
/// configured"), matching production where they are only registered when OpenRouter is configured.
/// No DI container or mocking library involved.
/// </summary>
public class FakeServiceScopeFactory(
    ITicketRepository ticketRepository,
    IMessageRepository messageRepository,
    IClassificationService? classificationService = null,
    IDraftReplyService? draftReplyService = null,
    IReviewFlagService? reviewFlagService = null)
    : IServiceScopeFactory
{
    public int ScopesCreated { get; private set; }

    /// <summary>Records (service type, 1-based index of the scope it was resolved from) for AI services.</summary>
    public List<(Type ServiceType, int ScopeIndex)> AiServiceResolutions { get; } = [];

    public IServiceScope CreateScope()
    {
        ScopesCreated++;
        var scopeIndex = ScopesCreated;
        return new FakeServiceScope(new FakeServiceProvider(
            ticketRepository, messageRepository, classificationService, draftReplyService, reviewFlagService,
            serviceType => AiServiceResolutions.Add((serviceType, scopeIndex))));
    }

    private sealed class FakeServiceScope(IServiceProvider serviceProvider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public void Dispose()
        {
        }
    }

    private sealed class FakeServiceProvider(
        ITicketRepository ticketRepository,
        IMessageRepository messageRepository,
        IClassificationService? classificationService,
        IDraftReplyService? draftReplyService,
        IReviewFlagService? reviewFlagService,
        Action<Type> onAiServiceResolved)
        : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ITicketRepository))
            {
                return ticketRepository;
            }

            if (serviceType == typeof(IMessageRepository))
            {
                return messageRepository;
            }

            if (serviceType == typeof(IClassificationService))
            {
                onAiServiceResolved(serviceType);
                return classificationService;
            }

            if (serviceType == typeof(IDraftReplyService))
            {
                onAiServiceResolved(serviceType);
                return draftReplyService;
            }

            if (serviceType == typeof(IReviewFlagService))
            {
                onAiServiceResolved(serviceType);
                return reviewFlagService;
            }

            return null;
        }
    }
}
```

- [ ] **Step 2: Write the failing ingestion tests and fix the two scope-count expectations**

In `tests/Helpdesk.Application.Tests/EmailIngestion/EmailIngestionServiceTests.cs` add `using Helpdesk.Application.Review;` at the top if missing, and:

a) Adjust the two existing scope-count assertions, because every new ticket now creates one more scope (the reviewer's). In the test whose assertion is `Assert.Equal(3, scopeFactory.ScopesCreated);` (the "first message poisoned, second still processed in its own scope" test) change it to `Assert.Equal(4, scopeFactory.ScopesCreated);` and update its adjacent comment to say: the poisoned first message throws before any AI work (1 scope); the second message uses a message scope, a draft scope and a review scope (3). In `IngestNewEmailsAsync_NewConversation_DraftsInDifferentScopeThanClassification` change `Assert.Equal(2, scopeFactory.ScopesCreated);` to `Assert.Equal(3, scopeFactory.ScopesCreated);`.

b) Append these tests inside the class (before its closing brace):
```csharp
    [Fact]
    public async Task IngestNewEmailsAsync_NewConversation_ReviewsNewTicketAfterDraftingInItsOwnScope()
    {
        var ticketRepository = new FakeTicketRepository();
        var classifier = new FakeClassificationService();
        var drafter = new FakeDraftReplyService(classifier);
        var reviewer = new FakeReviewFlagService(drafter);
        var scopeFactory = new FakeServiceScopeFactory(
            ticketRepository, new FakeMessageRepository(), classifier, drafter, reviewer);
        var service = new EmailIngestionService(
            new FakeMailClient(Email("msg-1", "conv-1")), scopeFactory, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        var ticket = Assert.Single(ticketRepository.Tickets);
        Assert.Equal(ticket.Id, Assert.Single(reviewer.ReviewedTicketIds));
        Assert.Equal(1, reviewer.DraftedCountAtReviewTime);

        int ScopeOf(Type type) => Assert.Single(scopeFactory.AiServiceResolutions, r => r.ServiceType == type).ScopeIndex;
        var scopes = new[] { ScopeOf(typeof(IClassificationService)), ScopeOf(typeof(IDraftReplyService)), ScopeOf(typeof(IReviewFlagService)) };
        Assert.Equal(3, scopes.Distinct().Count());
    }

    [Fact]
    public async Task IngestNewEmailsAsync_ReplyToExistingConversation_DoesNotReviewAgain()
    {
        var ticketRepository = new FakeTicketRepository();
        ticketRepository.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Help please",
            RequesterEmail = "requester@example.com",
            ConversationId = "conv-1",
        });
        var reviewer = new FakeReviewFlagService();
        var scopeFactory = new FakeServiceScopeFactory(
            ticketRepository, new FakeMessageRepository(), new FakeClassificationService(), new FakeDraftReplyService(), reviewer);
        var service = new EmailIngestionService(
            new FakeMailClient(Email("msg-2", "conv-1")), scopeFactory, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        Assert.Empty(reviewer.ReviewedTicketIds);
    }

    [Fact]
    public async Task IngestNewEmailsAsync_NoReviewServiceRegistered_StillClassifiesAndDrafts()
    {
        var ticketRepository = new FakeTicketRepository();
        var classifier = new FakeClassificationService();
        var drafter = new FakeDraftReplyService(classifier);
        var scopeFactory = new FakeServiceScopeFactory(ticketRepository, new FakeMessageRepository(), classifier, drafter);
        var service = new EmailIngestionService(
            new FakeMailClient(Email("msg-1", "conv-1")), scopeFactory, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        Assert.Single(ticketRepository.Tickets);
        Assert.Single(classifier.ClassifiedTicketIds);
        Assert.Single(drafter.DraftedTicketIds);
    }

    [Fact]
    public async Task IngestNewEmailsAsync_ReviewThrows_MessageStillMarkedProcessedAndOthersContinue()
    {
        var mailClient = new FakeMailClient(Email("msg-1", "conv-1"), Email("msg-2", "conv-2"));
        var ticketRepository = new FakeTicketRepository();
        var reviewer = new FakeReviewFlagService { ExceptionToThrow = new InvalidOperationException("boom") };
        var scopeFactory = new FakeServiceScopeFactory(
            ticketRepository, new FakeMessageRepository(), new FakeClassificationService(), new FakeDraftReplyService(), reviewer);
        var service = new EmailIngestionService(mailClient, scopeFactory, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        Assert.Equal(2, ticketRepository.Tickets.Count);
        Assert.Equal(2, reviewer.ReviewedTicketIds.Count);
        Assert.Equal(["msg-1", "msg-2"], mailClient.MarkedAsProcessed);
    }

    [Fact]
    public async Task IngestNewEmailsAsync_ReviewCancelled_PropagatesAndStopsBatch()
    {
        var mailClient = new FakeMailClient(Email("msg-1", "conv-1"), Email("msg-2", "conv-2"));
        var ticketRepository = new FakeTicketRepository();
        var reviewer = new FakeReviewFlagService { ExceptionToThrow = new OperationCanceledException() };
        var scopeFactory = new FakeServiceScopeFactory(
            ticketRepository, new FakeMessageRepository(), new FakeClassificationService(), new FakeDraftReplyService(), reviewer);
        var service = new EmailIngestionService(mailClient, scopeFactory, NullLogger<EmailIngestionService>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.IngestNewEmailsAsync(cts.Token));

        Assert.Single(reviewer.ReviewedTicketIds);
        Assert.Equal(["msg-1"], mailClient.MarkedAsProcessed);
    }
```

- [ ] **Step 3: Run tests to verify the new ones fail**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~EmailIngestionServiceTests"`
Expected: builds; the review tests FAIL (reviewer never called) and the two adjusted scope-count tests FAIL (still 3 / 2 scopes); the rest pass.

- [ ] **Step 4: Wire the reviewer into ingestion**

In `src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs` add `using Helpdesk.Application.Review;` and, inside `if (newTicketId is { } ticketId)`, after the drafter block add:
```csharp

                    // Review flags are derived from what classification and drafting actually stored, so they
                    // run last, in their own scope for the same isolation reason as the drafter.
                    using var reviewScope = scopeFactory.CreateScope();
                    if (reviewScope.ServiceProvider.GetService<IReviewFlagService>() is { } reviewer)
                    {
                        await reviewer.EvaluateAsync(ticketId, cancellationToken);
                    }
```
Also extend the comment above the block: change "Only a brand-new ticket is classified and drafted" to "Only a brand-new ticket is classified, drafted and review-flagged", and "Both services are optional" to "These services are optional".

- [ ] **Step 5: Run the whole suite**

Run: `dotnet build Helpdesk.slnx` then `dotnet test Helpdesk.slnx`
Expected: 0 errors, 0 warnings; all tests pass (record the count per test project).

- [ ] **Step 6: Update the docs**

In `CLAUDE.md`, insert this section directly after the `### AI draft reply (Phase 6 done)` section (before `### Data flow (MVP core loop)`):
```markdown
### Review flags / escalation (Phase 8 backend done; queue badge pending Phase 7)
`Ticket.ReviewReasons` (`Helpdesk.Core/Enums/ReviewReasons`, a `[Flags]` enum stored as an int column, migration `AddTicketReviewReasons`, default 0) records why a ticket needs manual review: `LowConfidence=1`, `ClassificationFailed=2`, `CategoryOther=4`, `DraftFailed=8` (stored values are a contract, never renumber). "Needs review" is `ReviewReasons != None`; there is no separate bool. `ReviewPolicy` (`Helpdesk.Application/Review`) derives the flags from the persisted classification and draft (no classification -> ClassificationFailed; confidence below the threshold, equal does not flag -> LowConfidence; category Other -> CategoryOther; blank draft -> DraftFailed). `ReviewFlagService` runs last in `EmailIngestionService` (classify -> draft -> review, each in its own DI scope) for new tickets only and writes only when the flags change; it never touches status, draft or classification, and flagged tickets are still drafted and moved to `InReview`. The threshold is `Review:ConfidenceThreshold` (default 0.7, invariant-culture number in [0,1], a bad value fails startup, but only when OpenRouter is configured); the services are registered under the same OpenRouter gate as classification/drafting, so with AI off nothing is flagged. Existing tickets are not backfilled. No API/UI yet: Phase 7's queue/detail DTOs expose `reviewReasons` and `needsReview`, and the badge (task 48) follows once Phase 7 is merged. Spec: `docs/superpowers/specs/2026-09-26-phase8-low-confidence-escalation-design.md`; plan: `docs/superpowers/plans/2026-09-26-phase8-low-confidence-escalation.md`. Manual check (needs the user's secrets and real emails): apply the migration with `dotnet ef database update --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Api`, send a deliberately vague email and a clear billing email, then `psql -U helpdesk -h localhost -d helpdesk -c 'select "Subject", "ReviewReasons" from "Tickets" order by "CreatedAt" desc limit 3;'` should show a non-zero value for the vague one and `0` for the clear one.
```
In `implementation-plan.md` replace the Phase 8 heading and tasks 46-48. The file contains non-UTF8 bytes on other lines, so edit it BYTE-LEVEL only (a small Python script doing exact `bytes.replace` on those four lines, preserving CRLF and every other byte), then confirm with `git diff --stat` that only those four lines changed. New text:
```
## Phase 8 — Escalation Path (Low Confidence) — backend done (manual test and queue badge pending)
46. Define confidence threshold — done, as a `Review:ConfidenceThreshold` setting (default 0.7) evaluated by `ReviewPolicy` rather than on the `IAiService.ClassifyAsync` response (`IAiService` is unchanged)
47. If below threshold, classification fails, category is Other or the draft failed, tag the ticket for manual review — done (`Ticket.ReviewReasons` flags + `ReviewFlagService`, migration `AddTicketReviewReasons`); manual test pending (needs the user's secrets and real emails; see CLAUDE.md "Review flags")
48. Frontend: visual indicator on queue for escalated/low-confidence tickets — pending (needs Phase 7's queue page and DTO)
```

- [ ] **Step 7: Commit**

```bash
git add src/Helpdesk.Application tests/Helpdesk.Application.Tests CLAUDE.md implementation-plan.md
git commit -m "feat: flag tickets for manual review during ingestion

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Post-merge manual test (user's hands, not an implementer task)

1. Stop any running API; from the worktree/main root apply the migration to the dev DB: `dotnet ef database update --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Api` (needs the OpenRouter user-secret or `OpenRouter__Enabled=false`).
2. Clear the shared mailbox, start the API with `OpenRouter__Model=nvidia/nemotron-3-super-120b-a12b:free`, send one deliberately vague email (e.g. "hi, quick question, thanks") and one clear billing email.
3. `psql` (see CLAUDE.md "Review flags") should show a non-zero `ReviewReasons` for the vague email and `0` for the billing one. Record the result in `CLAUDE.md` / `implementation-plan.md` afterwards.

## Self-Review Notes

- **Spec coverage:** four triggers + policy boundaries (Task 2); flags enum, entity, column default, migration (Task 1); service idempotence, no side effects, failure/cancellation semantics, threshold usage (Task 3); options parsing/gating/fail-fast (Tasks 2-3); ingestion order/own scope/new-only/optional (Task 4); docs incl. the deviation that the threshold lives in config rather than on `IAiService` (Task 4). Out-of-scope items (API, badge, clearing flags, backfill) untouched.
- **Cross-task types:** `ReviewReasons` (T1) used by T2-T4; `ReviewOptions(double)` + `Parse` (T2) used by T3 DI and the service; `IReviewFlagService.EvaluateAsync` (T3) used by T4 fake and ingestion; `FakeTicketRepository.UpdateCount` (T3) used by T3 tests only.
- **Known follow-ups:** the badge (task 48) after Phase 7; the DTO fields `reviewReasons`/`needsReview` belong to Phase 7.
