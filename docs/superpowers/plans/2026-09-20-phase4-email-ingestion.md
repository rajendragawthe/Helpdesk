# Phase 4 Email Ingestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Poll a shared mailbox via Microsoft Graph, turn new inbound emails into `Ticket`/`Message` records, thread replies onto existing tickets by `conversationId`, and never double-process the same email.

**Architecture:** `Helpdesk.Core` gets a Graph-agnostic `IMailClient` abstraction and an `InboundEmailMessage` DTO. `Helpdesk.Infrastructure/GraphApi` implements `IMailClient` against the real Microsoft Graph SDK (app-only auth via `ClientSecretCredential`). `Helpdesk.Application/EmailIngestion` holds the mapping/threading business logic (`EmailIngestionService`) and the polling loop (`EmailIngestionBackgroundService : BackgroundService`) — it lives in `Application`, not `Infrastructure`, because it needs both `IMailClient` and the ticket/message repositories together, and `Infrastructure` cannot depend on `Application`.

**Tech Stack:** Microsoft.Graph (Kiota-generated SDK v5), Azure.Identity (`ClientSecretCredential`), `Microsoft.Extensions.Hosting.Abstractions` (`BackgroundService`), xUnit with hand-written test doubles (no mocking library is used elsewhere in this repo).

**Spec:** `docs/superpowers/specs/2026-09-20-phase4-email-ingestion-design.md`

## Global Constraints

- Dependency direction is fixed: `Helpdesk.Api → Helpdesk.Application → Helpdesk.Core`, `Helpdesk.Infrastructure → Helpdesk.Core` only. `Helpdesk.Application` may take on lightweight `Microsoft.Extensions.*.Abstractions` packages (Hosting, DependencyInjection, Configuration, Logging abstractions) — these are generic BCL extension points, not the EF Core/Npgsql/Graph SDK infra the layering rule is protecting `Core` from — but it must never reference `Helpdesk.Infrastructure`.
- Graph auth is app-only (`ClientSecretCredential`), a separate Entra app registration from the API's own `AzureAd` config. Config keys under a new `GraphApi` section: `TenantId`, `ClientId`, `MailboxAddress`, `PollingIntervalSeconds` (default `60`) in `appsettings.Development.json`; `ClientSecret` in `dotnet user-secrets` for `Helpdesk.Api` — never committed.
- `Message.Body` stores the raw HTML Graph returns for the message body — no plain-text conversion.
- Appending a reply to an already-`Replied` ticket does **not** change its `Status`.
- A message is uniquely identified by its Graph message id, stored as `Message.ExternalMessageId` (already an indexed column — no migration needed).
- Threading key is `Ticket.ConversationId` (already exists, already indexed — no migration needed).
- Every repository call in this codebase does its own `SaveChangesAsync()` per `AddAsync`/`UpdateAsync` call (see `TicketRepository`/`MessageRepository`) — no ambient transaction. Follow that existing pattern; do not introduce `IDbContextTransaction` scaffolding not already used elsewhere in the repo.
- Match this repo's existing test-project shape exactly for any new test project: same `TargetFramework`, same `xunit`/`xunit.runner.visualstudio`/`coverlet.collector`/`Microsoft.NET.Test.Sdk` versions as `tests/Helpdesk.Core.Tests/Helpdesk.Core.Tests.csproj`, `<Using Include="Xunit" />`, registered in `Helpdesk.slnx` under the `/tests/` folder.
- No mocking library exists in this repo yet (no Moq/NSubstitute reference anywhere) — write plain hand-rolled test doubles implementing the Core interfaces directly, not a mocking framework.

---

### Task 1: Core mail-ingestion abstractions

**Files:**
- Create: `src/Helpdesk.Core/Models/InboundEmailMessage.cs`
- Create: `src/Helpdesk.Core/Interfaces/IMailClient.cs`
- Modify: `src/Helpdesk.Core/Interfaces/IMessageRepository.cs`
- Modify: `src/Helpdesk.Infrastructure/Repositories/MessageRepository.cs`

**Interfaces:**
- Produces: `InboundEmailMessage(string ExternalMessageId, string ConversationId, string FromAddress, string Subject, string BodyHtml, DateTimeOffset ReceivedAt)` — record, in `Helpdesk.Core.Models`.
- Produces: `IMailClient.FetchNewMessagesAsync(CancellationToken cancellationToken = default) : Task<IReadOnlyList<InboundEmailMessage>>` and `IMailClient.MarkAsProcessedAsync(string externalMessageId, CancellationToken cancellationToken = default) : Task`, in `Helpdesk.Core.Interfaces`.
- Produces: `IMessageRepository.GetByExternalMessageIdAsync(string externalMessageId) : Task<Message?>`.

This task is pure interface/DTO/repository-method scaffolding with no branching logic of its own (the one new repository method is a single `FirstOrDefaultAsync` query, matching every other method already in `MessageRepository`/`TicketRepository`) — consistent with this repo's existing convention that repository implementations don't get dedicated unit tests (`Helpdesk.Infrastructure.Tests` is currently a placeholder). Verification is a successful build; the interesting logic this enables (`EmailIngestionService`) gets full TDD coverage in Task 2.

- [ ] **Step 1: Create the `InboundEmailMessage` DTO**

```csharp
namespace Helpdesk.Core.Models;

public record InboundEmailMessage(
    string ExternalMessageId,
    string ConversationId,
    string FromAddress,
    string Subject,
    string BodyHtml,
    DateTimeOffset ReceivedAt);
```

- [ ] **Step 2: Create the `IMailClient` interface**

```csharp
using Helpdesk.Core.Models;

namespace Helpdesk.Core.Interfaces;

public interface IMailClient
{
    Task<IReadOnlyList<InboundEmailMessage>> FetchNewMessagesAsync(CancellationToken cancellationToken = default);

    Task MarkAsProcessedAsync(string externalMessageId, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Add `GetByExternalMessageIdAsync` to `IMessageRepository`**

Modify `src/Helpdesk.Core/Interfaces/IMessageRepository.cs` to:

```csharp
using Helpdesk.Core.Entities;

namespace Helpdesk.Core.Interfaces;

public interface IMessageRepository
{
    Task<IReadOnlyList<Message>> GetByTicketIdAsync(Guid ticketId);
    Task<Message?> GetByExternalMessageIdAsync(string externalMessageId);
    Task AddAsync(Message message);
}
```

- [ ] **Step 4: Implement it in `MessageRepository`**

Modify `src/Helpdesk.Infrastructure/Repositories/MessageRepository.cs` to add:

```csharp
    public async Task<Message?> GetByExternalMessageIdAsync(string externalMessageId)
    {
        return await dbContext.Messages
            .FirstOrDefaultAsync(m => m.ExternalMessageId == externalMessageId);
    }
```

(placed between `GetByTicketIdAsync` and `AddAsync`, matching the interface's member order)

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build Helpdesk.slnx`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 6: Commit**

```bash
git add src/Helpdesk.Core/Models/InboundEmailMessage.cs src/Helpdesk.Core/Interfaces/IMailClient.cs src/Helpdesk.Core/Interfaces/IMessageRepository.cs src/Helpdesk.Infrastructure/Repositories/MessageRepository.cs
git commit -m "Add IMailClient abstraction and message dedupe lookup"
```

---

### Task 2: `EmailIngestionService` (mapping/threading logic, TDD)

**Files:**
- Create: `src/Helpdesk.Application/Helpdesk.Application.csproj` (modify — add package references)
- Create: `src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs`
- Create: `tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj`
- Create: `tests/Helpdesk.Application.Tests/TestDoubles/FakeMailClient.cs`
- Create: `tests/Helpdesk.Application.Tests/TestDoubles/FakeTicketRepository.cs`
- Create: `tests/Helpdesk.Application.Tests/TestDoubles/FakeMessageRepository.cs`
- Create: `tests/Helpdesk.Application.Tests/EmailIngestion/EmailIngestionServiceTests.cs`
- Modify: `Helpdesk.slnx` (register the new test project)

**Interfaces:**
- Consumes: `IMailClient` (Task 1), `InboundEmailMessage` (Task 1), `IMessageRepository.GetByExternalMessageIdAsync`/`AddAsync` (Task 1), `ITicketRepository.GetByConversationIdAsync`/`AddAsync`/`UpdateAsync` (existing), `Ticket`/`Message` entities (existing), `TicketStatus` enum (existing).
- Produces: `EmailIngestionService(IMailClient mailClient, ITicketRepository ticketRepository, IMessageRepository messageRepository, ILogger<EmailIngestionService> logger)` with `Task IngestNewEmailsAsync(CancellationToken cancellationToken = default)`, in `Helpdesk.Application.EmailIngestion` — consumed by Task 4's background service.

- [ ] **Step 1: Add required package references to `Helpdesk.Application.csproj`**

Replace `src/Helpdesk.Application/Helpdesk.Application.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\Helpdesk.Core\Helpdesk.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

If `10.0.0` isn't resolvable for any of these packages, run `dotnet add src/Helpdesk.Application package <PackageId>` for each instead (from the repo root) and let NuGet pick the latest stable version compatible with `net10.0`, then re-check the `.csproj` reflects it.

- [ ] **Step 2: Create the new test project**

Create `tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj`:

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
    <ProjectReference Include="..\..\src\Helpdesk.Application\Helpdesk.Application.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Register the test project in `Helpdesk.slnx`**

Modify `Helpdesk.slnx`, adding a line under the `/tests/` folder:

```xml
  <Folder Name="/tests/">
    <Project Path="tests/Helpdesk.Core.Tests/Helpdesk.Core.Tests.csproj" />
    <Project Path="tests/Helpdesk.Infrastructure.Tests/Helpdesk.Infrastructure.Tests.csproj" />
    <Project Path="tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj" />
  </Folder>
```

- [ ] **Step 4: Write the test doubles**

Create `tests/Helpdesk.Application.Tests/TestDoubles/FakeMailClient.cs`:

```csharp
using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeMailClient : IMailClient
{
    private readonly List<InboundEmailMessage> _messages;

    public List<string> MarkedAsProcessed { get; } = [];

    public FakeMailClient(params InboundEmailMessage[] messages)
    {
        _messages = [.. messages];
    }

    public Task<IReadOnlyList<InboundEmailMessage>> FetchNewMessagesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<InboundEmailMessage>>(_messages);
    }

    public Task MarkAsProcessedAsync(string externalMessageId, CancellationToken cancellationToken = default)
    {
        MarkedAsProcessed.Add(externalMessageId);
        return Task.CompletedTask;
    }
}
```

Create `tests/Helpdesk.Application.Tests/TestDoubles/FakeTicketRepository.cs`:

```csharp
using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeTicketRepository : ITicketRepository
{
    public List<Ticket> Tickets { get; } = [];

    public Task<Ticket?> GetByIdAsync(Guid id)
    {
        return Task.FromResult(Tickets.FirstOrDefault(t => t.Id == id));
    }

    public Task<Ticket?> GetByConversationIdAsync(string conversationId)
    {
        return Task.FromResult(Tickets.FirstOrDefault(t => t.ConversationId == conversationId));
    }

    public Task<IReadOnlyList<Ticket>> GetAllAsync()
    {
        return Task.FromResult<IReadOnlyList<Ticket>>(Tickets);
    }

    public Task AddAsync(Ticket ticket)
    {
        Tickets.Add(ticket);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Ticket ticket)
    {
        return Task.CompletedTask;
    }
}
```

Create `tests/Helpdesk.Application.Tests/TestDoubles/FakeMessageRepository.cs`:

```csharp
using Helpdesk.Core.Entities;
using Helpdesk.Core.Interfaces;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeMessageRepository : IMessageRepository
{
    public List<Message> Messages { get; } = [];

    public string? ThrowOnAddForExternalMessageId { get; set; }

    public Task<IReadOnlyList<Message>> GetByTicketIdAsync(Guid ticketId)
    {
        return Task.FromResult<IReadOnlyList<Message>>(Messages.Where(m => m.TicketId == ticketId).ToList());
    }

    public Task<Message?> GetByExternalMessageIdAsync(string externalMessageId)
    {
        return Task.FromResult(Messages.FirstOrDefault(m => m.ExternalMessageId == externalMessageId));
    }

    public Task AddAsync(Message message)
    {
        if (message.ExternalMessageId == ThrowOnAddForExternalMessageId)
        {
            throw new InvalidOperationException("Simulated persistence failure.");
        }

        Messages.Add(message);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 5: Write the failing tests**

Create `tests/Helpdesk.Application.Tests/EmailIngestion/EmailIngestionServiceTests.cs`:

```csharp
using Helpdesk.Application.EmailIngestion;
using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Application.Tests.EmailIngestion;

public class EmailIngestionServiceTests
{
    [Fact]
    public async Task IngestNewEmailsAsync_NewConversation_CreatesTicketAndMessage()
    {
        var email = new InboundEmailMessage(
            ExternalMessageId: "msg-1",
            ConversationId: "conv-1",
            FromAddress: "requester@example.com",
            Subject: "Help please",
            BodyHtml: "<p>I need help</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var mailClient = new FakeMailClient(email);
        var ticketRepository = new FakeTicketRepository();
        var messageRepository = new FakeMessageRepository();
        var service = new EmailIngestionService(mailClient, ticketRepository, messageRepository, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        var ticket = Assert.Single(ticketRepository.Tickets);
        Assert.Equal("conv-1", ticket.ConversationId);
        Assert.Equal("requester@example.com", ticket.RequesterEmail);
        Assert.Equal(TicketStatus.New, ticket.Status);

        var message = Assert.Single(messageRepository.Messages);
        Assert.Equal(ticket.Id, message.TicketId);
        Assert.Equal("<p>I need help</p>", message.Body);
        Assert.True(message.IsFromUser);
        Assert.Equal("msg-1", message.ExternalMessageId);

        Assert.Equal(["msg-1"], mailClient.MarkedAsProcessed);
    }

    [Fact]
    public async Task IngestNewEmailsAsync_ExistingConversation_AppendsMessageAndLeavesStatusUnchanged()
    {
        var existingTicket = new Helpdesk.Core.Entities.Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Original subject",
            RequesterEmail = "requester@example.com",
            Status = TicketStatus.Replied,
            ConversationId = "conv-1",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

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
        var service = new EmailIngestionService(mailClient, ticketRepository, messageRepository, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        Assert.Single(ticketRepository.Tickets);
        Assert.Equal(TicketStatus.Replied, existingTicket.Status);

        var message = Assert.Single(messageRepository.Messages);
        Assert.Equal(existingTicket.Id, message.TicketId);
        Assert.Equal("msg-2", message.ExternalMessageId);
    }

    [Fact]
    public async Task IngestNewEmailsAsync_DuplicateExternalMessageId_IsSkipped()
    {
        var email = new InboundEmailMessage(
            ExternalMessageId: "msg-1",
            ConversationId: "conv-1",
            FromAddress: "requester@example.com",
            Subject: "Help please",
            BodyHtml: "<p>I need help</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var mailClient = new FakeMailClient(email);
        var ticketRepository = new FakeTicketRepository();
        var messageRepository = new FakeMessageRepository();
        messageRepository.Messages.Add(new Helpdesk.Core.Entities.Message
        {
            Id = Guid.NewGuid(),
            TicketId = Guid.NewGuid(),
            Sender = "requester@example.com",
            Body = "<p>I need help</p>",
            IsFromUser = true,
            ExternalMessageId = "msg-1",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        var service = new EmailIngestionService(mailClient, ticketRepository, messageRepository, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        Assert.Empty(ticketRepository.Tickets);
        Assert.Single(messageRepository.Messages);
        Assert.Empty(mailClient.MarkedAsProcessed);
    }

    [Fact]
    public async Task IngestNewEmailsAsync_OneMessageFailsToPersist_OtherMessagesStillProcessed()
    {
        var failingEmail = new InboundEmailMessage(
            ExternalMessageId: "msg-fail",
            ConversationId: "conv-fail",
            FromAddress: "a@example.com",
            Subject: "Will fail",
            BodyHtml: "<p>boom</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var okEmail = new InboundEmailMessage(
            ExternalMessageId: "msg-ok",
            ConversationId: "conv-ok",
            FromAddress: "b@example.com",
            Subject: "Will succeed",
            BodyHtml: "<p>fine</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var mailClient = new FakeMailClient(failingEmail, okEmail);
        var ticketRepository = new FakeTicketRepository();
        var messageRepository = new FakeMessageRepository { ThrowOnAddForExternalMessageId = "msg-fail" };
        var service = new EmailIngestionService(mailClient, ticketRepository, messageRepository, NullLogger<EmailIngestionService>.Instance);

        await service.IngestNewEmailsAsync();

        Assert.Single(ticketRepository.Tickets);
        Assert.Equal("conv-ok", ticketRepository.Tickets[0].ConversationId);
        Assert.Single(messageRepository.Messages);
        Assert.Equal(["msg-ok"], mailClient.MarkedAsProcessed);
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj`
Expected: build error — `EmailIngestionService` does not exist yet.

- [ ] **Step 7: Implement `EmailIngestionService`**

Create `src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs`:

```csharp
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.EmailIngestion;

public class EmailIngestionService(
    IMailClient mailClient,
    ITicketRepository ticketRepository,
    IMessageRepository messageRepository,
    ILogger<EmailIngestionService> logger)
{
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
                await ProcessEmailAsync(email);
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

    private async Task ProcessEmailAsync(InboundEmailMessage email)
    {
        var existingMessage = await messageRepository.GetByExternalMessageIdAsync(email.ExternalMessageId);
        if (existingMessage is not null)
        {
            return;
        }

        var ticket = await ticketRepository.GetByConversationIdAsync(email.ConversationId);

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
        }
        else
        {
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
    }
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj`
Expected: all 4 tests pass.

- [ ] **Step 9: Commit**

```bash
git add src/Helpdesk.Application Helpdesk.slnx tests/Helpdesk.Application.Tests
git commit -m "Add EmailIngestionService with threading and dedupe logic"
```

---

### Task 3: `GraphMailClient` (real Microsoft Graph implementation)

**Files:**
- Modify: `src/Helpdesk.Infrastructure/Helpdesk.Infrastructure.csproj` (add package references)
- Create: `src/Helpdesk.Infrastructure/GraphApi/GraphApiOptions.cs`
- Create: `src/Helpdesk.Infrastructure/GraphApi/GraphMailClient.cs`
- Create: `src/Helpdesk.Infrastructure/GraphApi/DependencyInjection.cs`

**Interfaces:**
- Consumes: `IMailClient`, `InboundEmailMessage` (Task 1).
- Produces: `GraphApiOptions(string TenantId, string ClientId, string ClientSecret, string MailboxAddress, int PollingIntervalSeconds)`; `AddGraphApi(this IServiceCollection services, IConfiguration configuration) : IServiceCollection`, in `Helpdesk.Infrastructure.GraphApi` — consumed by Task 4's `Program.cs` wiring.

No unit tests here — this is a thin adapter over the Graph SDK with no branching logic of its own to unit-test in isolation, and hitting real Graph requires the Entra app registration from Task 5. Verification is `dotnet build`; behavior is exercised via Task 4's dummy-credential smoke test and Task 5's real E2E test.

- [ ] **Step 1: Add Graph SDK packages**

From the repo root:
```bash
dotnet add src/Helpdesk.Infrastructure package Microsoft.Graph
dotnet add src/Helpdesk.Infrastructure package Azure.Identity
```

- [ ] **Step 2: Create `GraphApiOptions`**

```csharp
namespace Helpdesk.Infrastructure.GraphApi;

public record GraphApiOptions(
    string TenantId,
    string ClientId,
    string ClientSecret,
    string MailboxAddress,
    int PollingIntervalSeconds);
```

- [ ] **Step 3: Create `GraphMailClient`**

```csharp
using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Helpdesk.Infrastructure.GraphApi;

public class GraphMailClient(GraphServiceClient graphClient, GraphApiOptions options) : IMailClient
{
    public async Task<IReadOnlyList<InboundEmailMessage>> FetchNewMessagesAsync(CancellationToken cancellationToken = default)
    {
        var response = await graphClient.Users[options.MailboxAddress]
            .MailFolders["Inbox"]
            .Messages
            .GetAsync(requestConfig =>
            {
                requestConfig.QueryParameters.Filter = "isRead eq false";
                requestConfig.QueryParameters.Orderby = ["receivedDateTime asc"];
                requestConfig.QueryParameters.Select =
                    ["id", "conversationId", "from", "subject", "body", "receivedDateTime"];
            }, cancellationToken);

        var messages = response?.Value ?? [];

        return messages
            .Where(m => m.Id is not null && m.ConversationId is not null)
            .Select(m => new InboundEmailMessage(
                ExternalMessageId: m.Id!,
                ConversationId: m.ConversationId!,
                FromAddress: m.From?.EmailAddress?.Address ?? "unknown@unknown",
                Subject: m.Subject ?? "(no subject)",
                BodyHtml: m.Body?.Content ?? string.Empty,
                ReceivedAt: m.ReceivedDateTime ?? DateTimeOffset.UtcNow))
            .ToList();
    }

    public async Task MarkAsProcessedAsync(string externalMessageId, CancellationToken cancellationToken = default)
    {
        await graphClient.Users[options.MailboxAddress]
            .Messages[externalMessageId]
            .PatchAsync(new Message { IsRead = true }, cancellationToken: cancellationToken);
    }
}
```

- [ ] **Step 4: Create the `AddGraphApi` DI extension**

```csharp
using Azure.Identity;
using Helpdesk.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;

namespace Helpdesk.Infrastructure.GraphApi;

public static class DependencyInjection
{
    public static IServiceCollection AddGraphApi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new GraphApiOptions(
            TenantId: configuration["GraphApi:TenantId"]
                ?? throw new InvalidOperationException("GraphApi:TenantId is not configured."),
            ClientId: configuration["GraphApi:ClientId"]
                ?? throw new InvalidOperationException("GraphApi:ClientId is not configured."),
            ClientSecret: configuration["GraphApi:ClientSecret"]
                ?? throw new InvalidOperationException("GraphApi:ClientSecret is not configured."),
            MailboxAddress: configuration["GraphApi:MailboxAddress"]
                ?? throw new InvalidOperationException("GraphApi:MailboxAddress is not configured."),
            PollingIntervalSeconds: int.TryParse(configuration["GraphApi:PollingIntervalSeconds"], out var seconds)
                ? seconds
                : 60);

        services.AddSingleton(options);

        services.AddSingleton(_ =>
        {
            var credential = new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
            return new GraphServiceClient(credential);
        });

        services.AddScoped<IMailClient, GraphMailClient>();

        return services;
    }
}
```

- [ ] **Step 5: Build and reconcile against the installed SDK version**

Run: `dotnet build Helpdesk.slnx`

The Kiota-generated Graph SDK's exact request-builder method signatures can shift slightly between minor versions. If the compiler reports a mismatch (e.g. `PatchAsync`'s parameter list, or `QueryParameters` property names), adjust `GraphMailClient.cs` to match what the installed `Microsoft.Graph` version actually generated — check the compiler error's suggested members — then rebuild until it's clean.

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 6: Commit**

```bash
git add src/Helpdesk.Infrastructure/Helpdesk.Infrastructure.csproj src/Helpdesk.Infrastructure/GraphApi
git commit -m "Add GraphMailClient (app-only Microsoft Graph mail access)"
```

---

### Task 4: Background poller + DI wiring in `Program.cs`

**Files:**
- Create: `src/Helpdesk.Application/EmailIngestion/EmailIngestionBackgroundService.cs`
- Create: `src/Helpdesk.Application/DependencyInjection.cs`
- Modify: `src/Helpdesk.Api/Program.cs`
- Modify: `src/Helpdesk.Api/appsettings.Development.json`

**Interfaces:**
- Consumes: `EmailIngestionService` (Task 2), `AddGraphApi` (Task 3).
- Produces: `AddApplication(this IServiceCollection services) : IServiceCollection`, in `Helpdesk.Application` — called from `Program.cs`.

- [ ] **Step 1: Create `EmailIngestionBackgroundService`**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.EmailIngestion;

public class EmailIngestionBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<EmailIngestionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = int.TryParse(configuration["GraphApi:PollingIntervalSeconds"], out var seconds)
            ? seconds
            : 60;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        do
        {
            using var scope = scopeFactory.CreateScope();
            var ingestionService = scope.ServiceProvider.GetRequiredService<EmailIngestionService>();

            try
            {
                await ingestionService.IngestNewEmailsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email ingestion tick failed unexpectedly.");
            }
        }
        while (!stoppingToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

- [ ] **Step 2: Create the `Helpdesk.Application` DI extension**

```csharp
using Helpdesk.Application.EmailIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Helpdesk.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<EmailIngestionService>();
        services.AddHostedService<EmailIngestionBackgroundService>();

        return services;
    }
}
```

- [ ] **Step 3: Wire both extensions into `Program.cs`**

Modify `src/Helpdesk.Api/Program.cs`:

```csharp
using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.AspNetCore;
using Helpdesk.Api.Auth;
using Helpdesk.Application;
using Helpdesk.Core.Enums;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Data;
using Helpdesk.Infrastructure.GraphApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddFluentValidationAutoValidation();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddGraphApi(builder.Configuration);
builder.Services.AddApplication();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddScoped<IClaimsTransformation, HelpdeskUserClaimsTransformation>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(nameof(Role.Admin)));
    options.AddPolicy("AgentOnly", policy => policy.RequireRole(nameof(Role.Admin), nameof(Role.Agent)));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
    await DbSeeder.SeedAsync(dbContext, app.Environment.IsDevelopment());
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
```

(only the `using Helpdesk.Application;`/`using Helpdesk.Infrastructure.GraphApi;` imports and the `builder.Services.AddGraphApi(...)`/`builder.Services.AddApplication();` lines are new — everything else is unchanged from the current file)

- [ ] **Step 4: Add non-secret Graph config to `appsettings.Development.json`**

Modify `src/Helpdesk.Api/appsettings.Development.json` to add a `GraphApi` section (leave `TenantId`/`ClientId`/`MailboxAddress` as placeholders you'll fill in once Task 5's Entra app registration exists):

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=helpdesk;Username=helpdesk;Password=helpdesk"
  },
  "GraphApi": {
    "TenantId": "REPLACE_WITH_TENANT_ID",
    "ClientId": "REPLACE_WITH_CLIENT_ID",
    "MailboxAddress": "REPLACE_WITH_SHARED_MAILBOX_ADDRESS",
    "PollingIntervalSeconds": 60
  }
}
```

- [ ] **Step 5: Set a dummy client secret via user-secrets and smoke-test the wiring**

From `src/Helpdesk.Api`:
```bash
dotnet user-secrets init
dotnet user-secrets set "GraphApi:ClientSecret" "placeholder-secret-for-wiring-smoke-test"
dotnet run
```

Watch the console output. Expected within ~`PollingIntervalSeconds` of startup: an `error`-level log line from `EmailIngestionService` reading something like `Failed to fetch new emails from the mail client.` with an underlying Graph authentication exception. This confirms the whole DI graph resolves and the background service is actually ticking — it fails only because the `TenantId`/`ClientId`/`MailboxAddress` placeholders and dummy secret aren't real credentials yet. The host must **not** crash or exit; if it does, the exception isn't being caught where the plan expects — re-check `EmailIngestionService.IngestNewEmailsAsync`'s try/catch around the fetch call.

Stop the app with Ctrl+C once confirmed.

- [ ] **Step 6: Commit**

```bash
git add src/Helpdesk.Application src/Helpdesk.Api/Program.cs src/Helpdesk.Api/appsettings.Development.json
git commit -m "Wire up email ingestion background service in Program.cs"
```

---

### Task 5: Entra app registration + real end-to-end verification

This task has no code changes — it's the manual prerequisite plus the manual test called out in `implementation-plan.md` task 30 and the design spec's "Manual E2E test" section. Walk through it with the user rather than executing it unattended, since it requires access to their Entra tenant and a real mailbox.

- [ ] **Step 1: Register the Graph app-only Entra application**

In the Entra admin center (`entra.microsoft.com` → App registrations):
1. New registration, any name (e.g. `Helpdesk Graph Mail Access`), single tenant.
2. API permissions → Add a permission → Microsoft Graph → **Application permissions** → add `Mail.Read` and `Mail.Send`.
3. Click **Grant admin consent** for the tenant (requires admin rights on the tenant).
4. Certificates & secrets → New client secret → copy the **value** immediately (it's shown once).
5. Note the **Application (client) ID** and **Directory (tenant) ID** from the Overview page.

- [ ] **Step 2: Confirm the shared mailbox exists**

Confirm the target shared mailbox's email address is a real mail-enabled object in the tenant (Exchange admin center or ask whoever administers the tenant). App-only Graph mail access does not require the mailbox to be "assigned" to a user the way delegated access does — it just needs to exist.

- [ ] **Step 3: Fill in real config**

In `src/Helpdesk.Api/appsettings.Development.json`, replace the `GraphApi:TenantId`/`ClientId`/`MailboxAddress` placeholders from Task 4 with the real values from Step 1–2.

From `src/Helpdesk.Api`:
```bash
dotnet user-secrets set "GraphApi:ClientSecret" "<the real client secret value>"
```

- [ ] **Step 4: Run and send a test email**

```bash
cd src/Helpdesk.Api
dotnet run
```

Send a plain email to the shared mailbox's address. Within `PollingIntervalSeconds`, confirm:
- No error-level log from `EmailIngestionService` about the fetch failing.
- A new row exists in the `Tickets` table (check via `psql -d helpdesk -c "select id, subject, requester_email, conversation_id, status from \"Tickets\";"` — adjust column name casing per the actual `TicketConfiguration`/EF naming convention already in use) matching the test email's subject/sender.
- A matching row in `Messages` with `body` containing the test email's HTML content.
- The test email is now marked read in the mailbox (ingestion's `MarkAsProcessedAsync` succeeded).

- [ ] **Step 5: Send a reply and confirm threading**

Reply to the same email thread (same `conversationId` on the Graph side — a normal mail-client reply satisfies this). Within `PollingIntervalSeconds`, confirm:
- No new row in `Tickets` — the reply threaded onto the existing ticket.
- A second row in `Messages` with the same `ticket_id` as the first, and the reply's `ExternalMessageId`.

- [ ] **Step 6: Record completion**

Once both checks pass, Phase 4 (`implementation-plan.md` tasks 24–30) is complete. Update `implementation-plan.md`'s Phase 4 heading to `— done` to match the convention used for Phases 1–3, and commit:

```bash
git add implementation-plan.md
git commit -m "Mark Phase 4 (email ingestion) done"
```
