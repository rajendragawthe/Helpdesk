# Phase 9 — Hardening & Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add structured backend logging (Serilog), basic tracing/metrics (OpenTelemetry), a CORS allow-list, a frontend crash-reporting path, and a validation/error-handling review, closing out `implementation-plan.md` Phase 9 (items 49–53).

**Architecture:** Four independent, additive hardening tracks layered on the existing MVP without changing its behavior: (1) a CORS extension gated on config, (2) a new anonymous `POST /api/client-errors` endpoint that the frontend's `ErrorBoundary`/global handlers report to, (3) Serilog replacing the default console logger, (4) an OpenTelemetry extension (console exporter by default, OTLP when configured) plus `ILogger.BeginScope` correlation added to the two hottest call paths (email ingestion, ticket workflow). Each is independently testable via `ServiceCollection`/`BuildServiceProvider` unit tests, matching the existing `AddGraphApi`/`AddOpenRouter` opt-in test pattern — no new WebApplicationFactory/full-host test infrastructure is introduced.

**Tech Stack:** ASP.NET Core (.NET 10, controller-based), Serilog.AspNetCore, Serilog.Formatting.Compact, OpenTelemetry .NET SDK (+ ASP.NET Core/HttpClient/Npgsql instrumentation), FluentValidation, xUnit, React 19 + TypeScript (Vite).

**Spec:** `docs/superpowers/specs/2026-09-27-phase9-hardening-polish-design.md`

## Global Constraints

- `Helpdesk.Core` has no dependency on EF Core, Npgsql, or the Graph SDK. This plan touches `Helpdesk.Api`, `Helpdesk.Application`, `Helpdesk.Infrastructure`, and the frontend — never `Helpdesk.Core`.
- `Helpdesk.Application` depends only on `Helpdesk.Core` (plus the `Microsoft.Extensions.*.Abstractions` packages it already references). Correlation logging in `EmailIngestionService`/`TicketWorkflowService` must use plain `Microsoft.Extensions.Logging.ILogger.BeginScope` — **not** `Serilog.Context.LogContext`, which would add a Serilog package reference to `Application` and break this rule. (This is a deliberate, spec-compatible substitution: Serilog's `Microsoft.Extensions.Logging` bridge converts `ILogger.BeginScope(IEnumerable<KeyValuePair<string,object>>)` state into structured log properties automatically, with no `Serilog.Context` dependency needed in the calling code.)
- New request DTOs get a FluentValidation validator in `src/Helpdesk.Api/Validators/`, picked up automatically by the existing `AddValidatorsFromAssemblyContaining<Program>()` — no per-endpoint registration.
- New optional config sections (`Otel`, `CorsOrigins`) follow the existing `GraphApi`/`OpenRouter` opt-in rule: absent or explicitly disabled → the app still starts with no external dependency required.
- Controllers stay in `Helpdesk.Api`; they must not reach into `Helpdesk.Infrastructure` types directly (only via DI registered in `Program.cs`/`AddInfrastructure`).
- No new persistence: `POST /api/client-errors` logs and returns; it never writes a table.
- No new frontend test framework is introduced (none exists today — `client/helpdesk-web` has no test runner configured); frontend changes are verified by `npm run build` and `npm run lint`, not unit tests.

## Review Focus

- **Client-error endpoint reachable while signed out or with an expired token.** A crash can happen before login or after a token expires; the endpoint must accept the report without a bearer token (`[AllowAnonymous]`, no global auth filter blocking it) and must never itself throw back into the reporting code path. Covered in Task 2's controller test (`Report_ValidRequest_ReturnsAccepted`, called with no `Authorize` context at all) and Task 6's frontend `reportClientError` swallowing its own fetch failure.
- **Oversized or missing fields on the client-error payload.** A malformed or huge stack trace (e.g. a minified bundle's multi-KB stack, or a `null`/empty `Message`) must not 500 the endpoint or blow up log storage. Covered by Task 2's validator tests (blank `Message`/`Url`/`UserAgent`, over-length `Message`/`Stack`/`Url`/`UserAgent`).
- **CORS origin not in the allow-list.** A request from an origin that isn't configured must still be rejected by the browser (no `Access-Control-Allow-Origin` echoed back for it) rather than silently allowed — covered by Task 1's test asserting the registered policy's `Origins` contains only the configured list, never a wildcard.
- **A reopened/redrafted ticket's correlation scope must not leak between two different emails processed in the same ingestion tick.** Each email in `EmailIngestionService`'s loop gets its own `BeginScope`, and the scope must not still report the previous email's `ExternalMessageId` for the next one — covered by Task 5's test processing two emails in one `IngestNewEmailsAsync` call and asserting each captured scope has the matching id.
- **OpenTelemetry/CORS config absent vs. present must both leave the host startable.** No `Otel`/`CorsOrigins` section (today's state) and a section with a real value must both resolve their respective providers/policies without throwing — covered by Task 1's and Task 4's paired "absent" and "present" tests, mirroring the existing `AddOpenRouter_NoSection_RegistersNothing` / `AddOpenRouter_Configured_ResolvesAiService` pattern.

---

## Task 1: CORS allow-list

**Files:**
- Create: `src/Helpdesk.Api/Cors/CorsExtensions.cs`
- Create: `tests/Helpdesk.Api.Tests/Cors/CorsExtensionsTests.cs`
- Modify: `src/Helpdesk.Api/Program.cs`
- Modify: `src/Helpdesk.Api/appsettings.Development.json`

**Interfaces:**
- Produces: `Helpdesk.Api.Cors.CorsExtensions.TryAddCors(this IServiceCollection services, IConfiguration configuration) : bool` — returns `true` and registers a named CORS policy (`CorsExtensions.DefaultPolicyName`) when the `CorsOrigins` config section has at least one entry; returns `false` and registers nothing otherwise. `Helpdesk.Api.Cors.CorsExtensions.DefaultPolicyName : string`.
- Consumes (Program.cs): nothing from other tasks.

- [ ] **Step 1: Write the failing tests**

Create `tests/Helpdesk.Api.Tests/Cors/CorsExtensionsTests.cs`:

```csharp
using Helpdesk.Api.Cors;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tests.Cors;

public class CorsExtensionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void TryAddCors_NoSection_ReturnsFalseAndRegistersNoPolicy()
    {
        var services = new ServiceCollection();

        var added = services.TryAddCors(Config());

        Assert.False(added);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ICorsService));
    }

    [Fact]
    public void TryAddCors_EmptyArray_ReturnsFalse()
    {
        var services = new ServiceCollection();

        // An explicitly-present but empty CorsOrigins section behaves the same as an absent one.
        var added = services.TryAddCors(Config(("CorsOrigins", null)));

        Assert.False(added);
    }

    [Fact]
    public void TryAddCors_WithOrigins_RegistersPolicyWithExactlyTheConfiguredOrigins()
    {
        var services = new ServiceCollection();
        var config = Config(("CorsOrigins:0", "http://localhost:5173"));

        var added = services.TryAddCors(config);

        Assert.True(added);
        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(CorsExtensions.DefaultPolicyName);
        Assert.NotNull(policy);
        Assert.Equal(["http://localhost:5173"], policy!.Origins);
    }

    [Fact]
    public void TryAddCors_WithMultipleOrigins_RegistersAllOfThem()
    {
        var services = new ServiceCollection();
        var config = Config(
            ("CorsOrigins:0", "http://localhost:5173"),
            ("CorsOrigins:1", "https://helpdesk.example.com"));

        services.TryAddCors(config);

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(CorsExtensions.DefaultPolicyName);
        Assert.Equal(["http://localhost:5173", "https://helpdesk.example.com"], policy!.Origins);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj --filter "FullyQualifiedName~CorsExtensionsTests"`
Expected: build error (`CorsExtensions` doesn't exist yet) or failure.

- [ ] **Step 3: Implement `CorsExtensions`**

Create `src/Helpdesk.Api/Cors/CorsExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Api.Cors;

/// <summary>
/// Registers a CORS policy from the "CorsOrigins" config section (a string array), only when at
/// least one origin is configured. Mirrors the GraphApi/OpenRouter opt-in rule: an absent or
/// empty section leaves today's behavior (no CORS middleware at all) unchanged, so a fresh clone
/// or the E2E process - both proxied same-origin in dev - need no config to keep working.
/// </summary>
public static class CorsExtensions
{
    public const string DefaultPolicyName = "Frontend";

    public static bool TryAddCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("CorsOrigins").Get<string[]>() ?? [];
        if (origins.Length == 0)
        {
            return false;
        }

        services.AddCors(options => options.AddPolicy(
            DefaultPolicyName,
            policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj --filter "FullyQualifiedName~CorsExtensionsTests"`
Expected: PASS (4 tests)

- [ ] **Step 5: Wire it into `Program.cs`**

Modify `src/Helpdesk.Api/Program.cs`. Add `using Helpdesk.Api.Cors;` near the top with the other `using` statements. After the line `builder.Services.AddApplication(builder.Configuration);`, add:

```csharp
var corsConfigured = builder.Services.TryAddCors(builder.Configuration);
```

Then, in the request pipeline section, change:

```csharp
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
```

to:

```csharp
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

if (corsConfigured)
{
    app.UseCors(CorsExtensions.DefaultPolicyName);
}

app.UseAuthentication();
app.UseAuthorization();
```

(CORS must run before authentication/authorization per ASP.NET Core's documented middleware ordering.)

- [ ] **Step 6: Add the dev origin to config**

Modify `src/Helpdesk.Api/appsettings.Development.json`. Add a top-level `"CorsOrigins": ["http://localhost:5173"]` entry (after `"Logging"`, before `"ConnectionStrings"`, matching the file's existing key order):

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "CorsOrigins": ["http://localhost:5173"],
  "ConnectionStrings": {
```

Leave the rest of the file unchanged.

- [ ] **Step 7: Build the whole solution**

Run: `dotnet build Helpdesk.slnx`
Expected: builds with no new errors/warnings.

- [ ] **Step 8: Commit**

```bash
git add src/Helpdesk.Api/Cors/CorsExtensions.cs tests/Helpdesk.Api.Tests/Cors/CorsExtensionsTests.cs src/Helpdesk.Api/Program.cs src/Helpdesk.Api/appsettings.Development.json
git commit -m "feat: add configurable CORS allow-list

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 2: Client-error reporting endpoint

**Files:**
- Create: `src/Helpdesk.Api/Controllers/ClientErrorsController.cs`
- Create: `src/Helpdesk.Api/Validators/ClientErrorRequestValidator.cs`
- Create: `tests/Helpdesk.Api.Tests/Controllers/ClientErrorsControllerTests.cs`
- Create: `tests/Helpdesk.Api.Tests/Validators/ClientErrorRequestValidatorTests.cs`

**Interfaces:**
- Produces: `Helpdesk.Api.Controllers.ClientErrorRequest(string Message, string? Stack, string Url, string UserAgent)` (record), `Helpdesk.Api.Controllers.ClientErrorsController` (route `api/client-errors`, `POST`, anonymous). Task 6 (frontend) posts a JSON body shaped `{ message, stack, url, userAgent }` to this route (ASP.NET Core's default camelCase JSON binding maps it onto the record's PascalCase properties).
- Consumes: nothing from other tasks.

- [ ] **Step 1: Write the failing validator tests**

Create `tests/Helpdesk.Api.Tests/Validators/ClientErrorRequestValidatorTests.cs`:

```csharp
using Helpdesk.Api.Controllers;
using Helpdesk.Api.Validators;

namespace Helpdesk.Api.Tests.Validators;

public class ClientErrorRequestValidatorTests
{
    private readonly ClientErrorRequestValidator _validator = new();

    private static ClientErrorRequest Valid(
        string message = "Boom",
        string? stack = "at foo (app.js:1:1)",
        string url = "https://app.example.com/tickets/1",
        string userAgent = "Mozilla/5.0") =>
        new(message, stack, url, userAgent);

    [Fact]
    public void ValidRequest_IsValid()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void ValidRequest_WithNullStack_IsValid()
    {
        Assert.True(_validator.Validate(Valid(stack: null)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankMessage_IsInvalid(string message)
    {
        Assert.False(_validator.Validate(Valid(message: message)).IsValid);
    }

    [Fact]
    public void MessageOverLimit_IsInvalid()
    {
        Assert.False(_validator.Validate(Valid(message: new string('x', 2001))).IsValid);
    }

    [Fact]
    public void MessageAtLimit_IsValid()
    {
        Assert.True(_validator.Validate(Valid(message: new string('x', 2000))).IsValid);
    }

    [Fact]
    public void StackOverLimit_IsInvalid()
    {
        Assert.False(_validator.Validate(Valid(stack: new string('x', 8001))).IsValid);
    }

    [Fact]
    public void StackAtLimit_IsValid()
    {
        Assert.True(_validator.Validate(Valid(stack: new string('x', 8000))).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BlankUrl_IsInvalid(string? url)
    {
        Assert.False(_validator.Validate(Valid(url: url!)).IsValid);
    }

    [Fact]
    public void UrlOverLimit_IsInvalid()
    {
        Assert.False(_validator.Validate(Valid(url: new string('x', 501))).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BlankUserAgent_IsInvalid(string? userAgent)
    {
        Assert.False(_validator.Validate(Valid(userAgent: userAgent!)).IsValid);
    }

    [Fact]
    public void UserAgentOverLimit_IsInvalid()
    {
        Assert.False(_validator.Validate(Valid(userAgent: new string('x', 501))).IsValid);
    }
}
```

- [ ] **Step 2: Write the failing controller tests**

Create `tests/Helpdesk.Api.Tests/Controllers/ClientErrorsControllerTests.cs`:

```csharp
using System.Reflection;
using Helpdesk.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Api.Tests.Controllers;

public class ClientErrorsControllerTests
{
    [Fact]
    public void Controller_AllowsAnonymousAccess()
    {
        var attribute = typeof(ClientErrorsController).GetCustomAttribute<AllowAnonymousAttribute>();

        Assert.NotNull(attribute);
    }

    [Fact]
    public void Report_ValidRequest_ReturnsAccepted()
    {
        var controller = new ClientErrorsController(NullLogger<ClientErrorsController>.Instance);

        var result = controller.Report(new ClientErrorRequest(
            "Boom", "at foo (app.js:1:1)", "https://app.example.com/tickets/1", "Mozilla/5.0"));

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public void Report_RequestWithNullStack_StillReturnsAccepted()
    {
        var controller = new ClientErrorsController(NullLogger<ClientErrorsController>.Instance);

        var result = controller.Report(new ClientErrorRequest(
            "Boom", null, "https://app.example.com/tickets/1", "Mozilla/5.0"));

        Assert.IsType<AcceptedResult>(result);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj --filter "FullyQualifiedName~ClientError"`
Expected: build error (types don't exist yet).

- [ ] **Step 4: Implement the controller and DTO**

Create `src/Helpdesk.Api/Controllers/ClientErrorsController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.Api.Controllers;

/// <summary>
/// Receives frontend crash reports from the React app's ErrorBoundary and global
/// window.onerror/unhandledrejection handlers. Anonymous on purpose: a crash can happen before
/// sign-in or after a token has expired, and it must still be captured. Logs only - there is no
/// persistence here, this is a log stream, not a feature.
/// </summary>
[ApiController]
[Route("api/client-errors")]
[AllowAnonymous]
public class ClientErrorsController(ILogger<ClientErrorsController> logger) : ControllerBase
{
    [HttpPost]
    public IActionResult Report([FromBody] ClientErrorRequest request)
    {
        logger.LogWarning(
            "Frontend error reported: {Message} at {Url} ({UserAgent}). Stack: {Stack}",
            request.Message,
            request.Url,
            request.UserAgent,
            request.Stack);

        return Accepted();
    }
}

public record ClientErrorRequest(string Message, string? Stack, string Url, string UserAgent);
```

Note: this file needs `using Microsoft.Extensions.Logging;` implicitly satisfied by `ImplicitUsings` (the project already has `<ImplicitUsings>enable</ImplicitUsings>`), matching the other controllers in this project which don't explicitly import it either — if the build complains, add `using Microsoft.Extensions.Logging;` explicitly.

- [ ] **Step 5: Implement the validator**

Create `src/Helpdesk.Api/Validators/ClientErrorRequestValidator.cs`:

```csharp
using FluentValidation;
using Helpdesk.Api.Controllers;

namespace Helpdesk.Api.Validators;

public class ClientErrorRequestValidator : AbstractValidator<ClientErrorRequest>
{
    public ClientErrorRequestValidator()
    {
        RuleFor(x => x.Message).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Stack).MaximumLength(8000);
        RuleFor(x => x.Url).NotEmpty().MaximumLength(500);
        RuleFor(x => x.UserAgent).NotEmpty().MaximumLength(500);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj --filter "FullyQualifiedName~ClientError"`
Expected: PASS (all validator and controller tests)

- [ ] **Step 7: Commit**

```bash
git add src/Helpdesk.Api/Controllers/ClientErrorsController.cs src/Helpdesk.Api/Validators/ClientErrorRequestValidator.cs tests/Helpdesk.Api.Tests/Controllers/ClientErrorsControllerTests.cs tests/Helpdesk.Api.Tests/Validators/ClientErrorRequestValidatorTests.cs
git commit -m "feat: add anonymous client-error reporting endpoint

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 3: Structured logging with Serilog

**Files:**
- Modify: `src/Helpdesk.Api/Helpdesk.Api.csproj`
- Modify: `src/Helpdesk.Api/Program.cs`

**Interfaces:**
- Produces: nothing new callable — this task changes *how* existing `ILogger` calls are rendered (structured JSON to console), not any new API surface. Tasks 4 and 5 build on top of the same `ILogger`/`ILogger<T>` abstraction already used everywhere, unaffected by this task's change of logging provider.
- Consumes: nothing from other tasks. Independent of Tasks 1/2/4/5/6 — can be done in any order relative to them, but is listed here because Task 4 (OpenTelemetry) touches the same `Program.cs` region and it's simplest to layer them in sequence.

- [ ] **Step 1: Add the Serilog packages**

Run from the repo root:

```bash
dotnet add src/Helpdesk.Api/Helpdesk.Api.csproj package Serilog.AspNetCore
dotnet add src/Helpdesk.Api/Helpdesk.Api.csproj package Serilog.Formatting.Compact
```

(Letting `dotnet add package` resolve the latest version compatible with `net10.0` avoids pinning a version number that may not exist by the time this runs; it edits the `.csproj` for you.)

- [ ] **Step 2: Wire Serilog into the host**

Modify `src/Helpdesk.Api/Program.cs`. Add these two `using` statements near the top, alongside the existing ones:

```csharp
using Serilog;
using Serilog.Formatting.Compact;
```

Immediately after the line `var builder = WebApplication.CreateBuilder(args);`, add:

```csharp
builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));
```

This replaces the default console logger with Serilog. `ReadFrom.Configuration` understands the standard ASP.NET Core `Logging:LogLevel` shape already in `appsettings.json`/`appsettings.Development.json`, so no changes are needed there. `Enrich.FromLogContext()` is included for forward-compatibility with any future direct `Serilog.Context.LogContext` use, even though this plan's own correlation logging (Task 5) uses the provider-agnostic `ILogger.BeginScope` instead (see Global Constraints) — Serilog's `Microsoft.Extensions.Logging` bridge picks up `BeginScope` state on its own, without needing this enricher, but it's harmless to have both.

- [ ] **Step 3: Build the solution**

Run: `dotnet build Helpdesk.slnx`
Expected: builds with no new errors/warnings.

- [ ] **Step 4: Run the existing test suite**

Run: `dotnet test Helpdesk.slnx`
Expected: PASS (same count as before this task — this task changes no runtime behavior other tests could observe).

- [ ] **Step 5: Commit**

```bash
git add src/Helpdesk.Api/Helpdesk.Api.csproj src/Helpdesk.Api/Program.cs
git commit -m "feat: switch to structured Serilog console logging

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 4: OpenTelemetry tracing and metrics

**Files:**
- Create: `src/Helpdesk.Api/Observability/ObservabilityExtensions.cs`
- Create: `tests/Helpdesk.Api.Tests/Observability/ObservabilityExtensionsTests.cs`
- Modify: `src/Helpdesk.Api/Helpdesk.Api.csproj`
- Modify: `src/Helpdesk.Infrastructure/Helpdesk.Infrastructure.csproj`
- Modify: `src/Helpdesk.Api/Program.cs`

**Interfaces:**
- Produces: `Helpdesk.Api.Observability.ObservabilityExtensions.AddObservability(this IServiceCollection services, IConfiguration configuration) : IServiceCollection`.
- Consumes: nothing from other tasks.

- [ ] **Step 1: Add the OpenTelemetry packages**

Run from the repo root:

```bash
dotnet add src/Helpdesk.Api/Helpdesk.Api.csproj package OpenTelemetry.Extensions.Hosting
dotnet add src/Helpdesk.Api/Helpdesk.Api.csproj package OpenTelemetry.Instrumentation.AspNetCore
dotnet add src/Helpdesk.Api/Helpdesk.Api.csproj package OpenTelemetry.Instrumentation.Http
dotnet add src/Helpdesk.Api/Helpdesk.Api.csproj package OpenTelemetry.Exporter.Console
dotnet add src/Helpdesk.Api/Helpdesk.Api.csproj package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add src/Helpdesk.Infrastructure/Helpdesk.Infrastructure.csproj package Npgsql.OpenTelemetry
```

If `Npgsql.OpenTelemetry` fails to resolve against the `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3` already in `Helpdesk.Infrastructure.csproj` (a version mismatch between the OTel package and the core Npgsql driver it instruments), pin it to the latest `Npgsql.OpenTelemetry` version whose own `Npgsql` dependency matches the major version actually restored (check with `dotnet list src/Helpdesk.Infrastructure/Helpdesk.Infrastructure.csproj package` after `dotnet restore`). If no compatible version exists at all, skip the Npgsql-specific span (drop the `AddNpgsql()` call in Step 3 below and note it as a follow-up in the task's completion report) rather than blocking the rest of this task on it — ASP.NET Core + HttpClient instrumentation still deliver the bulk of the value.

- [ ] **Step 2: Write the failing tests**

Create `tests/Helpdesk.Api.Tests/Observability/ObservabilityExtensionsTests.cs`:

```csharp
using Helpdesk.Api.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Helpdesk.Api.Tests.Observability;

public class ObservabilityExtensionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void AddObservability_NoOtelSection_StartsWithConsoleExporterAndResolves()
    {
        var services = new ServiceCollection();

        services.AddObservability(Config());

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<TracerProvider>());
        Assert.NotNull(provider.GetService<MeterProvider>());
    }

    [Fact]
    public void AddObservability_WithOtlpEndpoint_StillResolvesWithoutThrowing()
    {
        var services = new ServiceCollection();

        services.AddObservability(Config(("Otel:OtlpEndpoint", "http://localhost:4317")));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<TracerProvider>());
        Assert.NotNull(provider.GetService<MeterProvider>());
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj --filter "FullyQualifiedName~ObservabilityExtensionsTests"`
Expected: build error (`ObservabilityExtensions` doesn't exist yet).

- [ ] **Step 4: Implement `ObservabilityExtensions`**

Create `src/Helpdesk.Api/Observability/ObservabilityExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Helpdesk.Api.Observability;

/// <summary>
/// Instruments ASP.NET Core/HttpClient/Npgsql for tracing and metrics. Unlike GraphApi/OpenRouter,
/// there is no failure mode from instrumenting with nowhere to send data, so this is always
/// registered: it exports to the console when no "Otel:OtlpEndpoint" is configured, and adds an
/// OTLP exporter on top when one is. A fresh clone therefore gets visible traces/metrics with zero
/// config, and can be pointed at a real collector later with only a config change.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpEndpoint = configuration["Otel:OtlpEndpoint"];

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Helpdesk.Api"));

        otel.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation();
            tracing.AddHttpClientInstrumentation();
            tracing.AddNpgsql();

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            }
            else
            {
                tracing.AddConsoleExporter();
            }
        });

        otel.WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation();
            metrics.AddHttpClientInstrumentation();

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            }
            else
            {
                metrics.AddConsoleExporter();
            }
        });

        return services;
    }
}
```

If Step 1's `Npgsql.OpenTelemetry` install had to be skipped, remove the `tracing.AddNpgsql();` line above and add a one-line comment explaining why (referencing this step).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj --filter "FullyQualifiedName~ObservabilityExtensionsTests"`
Expected: PASS (2 tests)

- [ ] **Step 6: Wire it into `Program.cs`**

Modify `src/Helpdesk.Api/Program.cs`. Add `using Helpdesk.Api.Observability;` near the top. After the line `builder.Services.AddApplication(builder.Configuration);` (and before or after the CORS line added in Task 1 — order between them doesn't matter), add:

```csharp
builder.Services.AddObservability(builder.Configuration);
```

- [ ] **Step 7: Build and test the whole solution**

Run: `dotnet build Helpdesk.slnx && dotnet test Helpdesk.slnx`
Expected: builds and all tests PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Helpdesk.Api/Observability/ObservabilityExtensions.cs tests/Helpdesk.Api.Tests/Observability/ObservabilityExtensionsTests.cs src/Helpdesk.Api/Helpdesk.Api.csproj src/Helpdesk.Infrastructure/Helpdesk.Infrastructure.csproj src/Helpdesk.Api/Program.cs
git commit -m "feat: add OpenTelemetry tracing and metrics instrumentation

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 5: Correlation scopes in email ingestion and ticket workflow

**Files:**
- Create: `tests/Helpdesk.Application.Tests/TestDoubles/RecordingLogger.cs`
- Create: `tests/Helpdesk.Application.Tests/EmailIngestion/EmailIngestionServiceCorrelationTests.cs`
- Create: `tests/Helpdesk.Application.Tests/Tickets/TicketWorkflowServiceCorrelationTests.cs`
- Modify: `src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs`
- Modify: `src/Helpdesk.Application/Tickets/TicketWorkflowService.cs`

**Interfaces:**
- Produces: no new public API — adds `ILogger.BeginScope` calls around existing method bodies. `RecordingLogger<T> : ILogger<T>` (test double) exposes `Scopes : List<IReadOnlyDictionary<string, object?>>`, one entry per `BeginScope` call whose state was an `IEnumerable<KeyValuePair<string, object>>`.
- Consumes: `EmailIngestionService`'s and `TicketWorkflowService`'s existing constructors (unchanged signatures — this task only changes method bodies, not constructors).

- [ ] **Step 1: Write the `RecordingLogger<T>` test double**

Create `tests/Helpdesk.Application.Tests/TestDoubles/RecordingLogger.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Tests.TestDoubles;

/// <summary>
/// Captures every BeginScope call's state so tests can assert on correlation properties (e.g.
/// TicketId, ExternalMessageId) pushed via ILogger.BeginScope, without depending on Serilog - the
/// production code only ever calls the provider-agnostic ILogger.BeginScope (see Application's
/// dependency rule: it must not reference Serilog directly).
/// </summary>
public class RecordingLogger<T> : ILogger<T>
{
    public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        if (state is IEnumerable<KeyValuePair<string, object>> pairs)
        {
            Scopes.Add(pairs.ToDictionary(p => p.Key, p => (object?)p.Value));
        }

        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
```

- [ ] **Step 2: Write the failing email-ingestion correlation test**

Create `tests/Helpdesk.Application.Tests/EmailIngestion/EmailIngestionServiceCorrelationTests.cs`:

```csharp
using Helpdesk.Application.EmailIngestion;
using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Core.Models;

namespace Helpdesk.Application.Tests.EmailIngestion;

public class EmailIngestionServiceCorrelationTests
{
    [Fact]
    public async Task IngestNewEmailsAsync_PushesExternalMessageIdAndConversationIdScope_PerEmail()
    {
        var emailOne = new InboundEmailMessage(
            ExternalMessageId: "msg-1",
            ConversationId: "conv-1",
            FromAddress: "one@example.com",
            Subject: "First",
            BodyHtml: "<p>First</p>",
            ReceivedAt: DateTimeOffset.UtcNow);
        var emailTwo = new InboundEmailMessage(
            ExternalMessageId: "msg-2",
            ConversationId: "conv-2",
            FromAddress: "two@example.com",
            Subject: "Second",
            BodyHtml: "<p>Second</p>",
            ReceivedAt: DateTimeOffset.UtcNow);

        var mailClient = new FakeMailClient(emailOne, emailTwo);
        var ticketRepository = new FakeTicketRepository();
        var messageRepository = new FakeMessageRepository();
        var scopeFactory = new FakeServiceScopeFactory(ticketRepository, messageRepository);
        var logger = new RecordingLogger<EmailIngestionService>();
        var service = new EmailIngestionService(mailClient, scopeFactory, logger);

        await service.IngestNewEmailsAsync();

        Assert.Equal(2, logger.Scopes.Count);
        Assert.Equal("msg-1", logger.Scopes[0]["ExternalMessageId"]);
        Assert.Equal("conv-1", logger.Scopes[0]["ConversationId"]);
        Assert.Equal("msg-2", logger.Scopes[1]["ExternalMessageId"]);
        Assert.Equal("conv-2", logger.Scopes[1]["ConversationId"]);
    }
}
```

`FakeMailClient`'s constructor is `FakeMailClient(params InboundEmailMessage[] messages)`, so `new FakeMailClient(emailOne, emailTwo)` above already compiles as-is - no change needed to that test double.

- [ ] **Step 3: Write the failing ticket-workflow correlation test**

Create `tests/Helpdesk.Application.Tests/Tickets/TicketWorkflowServiceCorrelationTests.cs`:

```csharp
using Helpdesk.Application.Tests.TestDoubles;
using Helpdesk.Application.Tickets;
using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;

namespace Helpdesk.Application.Tests.Tickets;

public class TicketWorkflowServiceCorrelationTests
{
    [Fact]
    public async Task ClaimAsync_PushesTicketIdScope()
    {
        var tickets = new FakeTicketRepository();
        var alice = new User { Id = Guid.NewGuid(), Email = "alice@example.com", DisplayName = "Alice", Role = Role.Agent };
        tickets.Users.Add(alice);
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Subject = "Charged twice",
            RequesterEmail = "customer@example.com",
            Status = TicketStatus.InReview,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        tickets.Tickets.Add(ticket);
        var logger = new RecordingLogger<TicketWorkflowService>();
        var service = new TicketWorkflowService(tickets, logger, mailClient: null);

        await service.ClaimAsync(new TicketCaller(alice.Id, alice.Email, IsAdmin: false), ticket.Id);

        Assert.Contains(logger.Scopes, s => Equals(s["TicketId"], ticket.Id));
    }
}
```

Check `tests/Helpdesk.Application.Tests/TestDoubles/FakeTicketRepository.cs` for the exact `Tickets`/`Users` collection member names and `Ticket`/`User` entity property names before writing this file, and adjust the object initializers above to match exactly (this plan's earlier read of `TicketWorkflowServiceTests.cs` confirms `Tickets` and `Users` are public mutable collections and `Ticket`/`User` have the properties used above, but confirm field names like `RequesterEmail` line up before running).

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~Correlation"`
Expected: FAIL (`logger.Scopes` is empty - no `BeginScope` calls exist in production code yet).

- [ ] **Step 5: Add the scope to `EmailIngestionService`**

Modify `src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs`. In the `foreach (var email in emails)` loop inside `IngestNewEmailsAsync`, wrap the existing `try { ... } catch (...) { ... }` block in a `using` scope. Change:

```csharp
        foreach (var email in emails)
        {
            try
            {
```

to:

```csharp
        foreach (var email in emails)
        {
            using var _ = logger.BeginScope(new Dictionary<string, object>
            {
                ["ExternalMessageId"] = email.ExternalMessageId,
                ["ConversationId"] = email.ConversationId,
            });

            try
            {
```

Leave everything else in the loop body (the existing `try`/`catch`, the calls into `ProcessEmailAsync`/`classifier`/`drafter`/`reviewer`) exactly as it is - the scope is ambient via `BeginScope`'s `AsyncLocal`-backed state, so every log call made inside this iteration's `await` chain (including inside the classifier/drafter/reviewer, which each get their own DI scope but share the same async execution context) picks it up automatically, with no signature changes needed anywhere else.

- [ ] **Step 6: Add the scope to `TicketWorkflowService`**

Modify `src/Helpdesk.Application/Tickets/TicketWorkflowService.cs`. Wrap the body of `ClaimAsync`, `ReleaseAsync`, and `SendReplyAsync` each in a `using var _ = logger.BeginScope(...)` pushing `TicketId`. For example, change:

```csharp
    public async Task<TicketResult<TicketDetail>> ClaimAsync(TicketCaller caller, Guid ticketId)
    {
        var existing = await ticketRepository.GetDetailAsync(ticketId);
```

to:

```csharp
    public async Task<TicketResult<TicketDetail>> ClaimAsync(TicketCaller caller, Guid ticketId)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["TicketId"] = ticketId });

        var existing = await ticketRepository.GetDetailAsync(ticketId);
```

and apply the identical one-line addition (`using var _ = logger.BeginScope(new Dictionary<string, object> { ["TicketId"] = ticketId });`) as the first line inside `ReleaseAsync` and `SendReplyAsync`. Do not add it to `ListAsync` or `GetAsync` - `ListAsync` has no single ticket to correlate, and `GetAsync` is a read called both directly (no correlation needed) and from inside `ClaimAsync`/`ReleaseAsync` (which already push the scope themselves, so `GetAsync` inherits it there).

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj --filter "FullyQualifiedName~Correlation"`
Expected: PASS (both new tests)

- [ ] **Step 8: Run the full Application test suite to confirm no regression**

Run: `dotnet test tests/Helpdesk.Application.Tests/Helpdesk.Application.Tests.csproj`
Expected: PASS (same count as before this task, plus the 2 new correlation tests - `BeginScope` around existing logic changes no return values or side effects the other tests assert on).

- [ ] **Step 9: Commit**

```bash
git add tests/Helpdesk.Application.Tests/TestDoubles/RecordingLogger.cs tests/Helpdesk.Application.Tests/EmailIngestion/EmailIngestionServiceCorrelationTests.cs tests/Helpdesk.Application.Tests/Tickets/TicketWorkflowServiceCorrelationTests.cs src/Helpdesk.Application/EmailIngestion/EmailIngestionService.cs src/Helpdesk.Application/Tickets/TicketWorkflowService.cs
git commit -m "feat: correlate ingestion and ticket-workflow logs by ticket/message id

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 6: Frontend crash capture

**Files:**
- Create: `client/helpdesk-web/src/lib/clientErrorReporter.ts`
- Create: `client/helpdesk-web/src/components/ErrorBoundary.tsx`
- Modify: `client/helpdesk-web/src/main.tsx`

**Interfaces:**
- Produces: `reportClientError(error: { message: string; stack?: string }) : void` (fire-and-forget, exported from `clientErrorReporter.ts`); `ErrorBoundary` (default export from `ErrorBoundary.tsx`), a React component taking `children: ReactNode`.
- Consumes: `POST /api/client-errors` from Task 2 (`{ message, stack, url, userAgent }` JSON body - relies on ASP.NET Core's default camelCase model binding onto `ClientErrorRequest`, no frontend-side header/casing work needed).

- [ ] **Step 1: Implement the reporter**

Create `client/helpdesk-web/src/lib/clientErrorReporter.ts`:

```typescript
/**
 * Reports a frontend crash to the backend's anonymous client-error endpoint. Deliberately does
 * not use apiFetch (which requires a signed-in MSAL account and throws otherwise) - a crash can
 * happen before sign-in, and reporting it must never itself throw or block the caller.
 */
export function reportClientError(error: { message: string; stack?: string }): void {
  const body = {
    message: error.message,
    stack: error.stack,
    url: window.location.href,
    userAgent: navigator.userAgent,
  }

  fetch('/api/client-errors', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).catch(() => {
    // Reporting failures must never surface as a second crash.
  })
}
```

- [ ] **Step 2: Implement the error boundary**

Create `client/helpdesk-web/src/components/ErrorBoundary.tsx`:

```tsx
import { Component, type ErrorInfo, type ReactNode } from 'react'
import { reportClientError } from '@/lib/clientErrorReporter'

type Props = { children: ReactNode }
type State = { hasError: boolean }

/**
 * Catches render-time crashes anywhere below it in the tree, reports them, and shows a minimal
 * fallback instead of a blank white screen. Does not catch errors in event handlers or async
 * code (React error boundaries never do) - those are covered by the window-level handlers
 * registered in main.tsx.
 */
export class ErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false }

  static getDerivedStateFromError(): State {
    return { hasError: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    reportClientError({ message: error.message, stack: error.stack ?? info.componentStack ?? undefined })
  }

  render() {
    if (this.state.hasError) {
      return (
        <div style={{ padding: '2rem', textAlign: 'center' }}>
          <p>Something went wrong. Please reload the page.</p>
        </div>
      )
    }

    return this.props.children
  }
}
```

- [ ] **Step 3: Register global handlers and wrap `App`**

Modify `client/helpdesk-web/src/main.tsx`. Add these two imports alongside the existing ones:

```typescript
import { ErrorBoundary } from './components/ErrorBoundary.tsx'
import { reportClientError } from './lib/clientErrorReporter.ts'
```

After the `await msalInstance.initialize()` line and before `createRoot(...)`, add:

```typescript
window.addEventListener('error', (event) => {
  reportClientError({ message: event.message, stack: event.error?.stack })
})

window.addEventListener('unhandledrejection', (event) => {
  const reason = event.reason
  reportClientError({
    message: reason instanceof Error ? reason.message : String(reason),
    stack: reason instanceof Error ? reason.stack : undefined,
  })
})
```

Then change the render call from:

```tsx
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeProvider>
      <TooltipProvider>
        <MsalProvider instance={msalInstance}>
          <BrowserRouter>
            <App />
          </BrowserRouter>
        </MsalProvider>
      </TooltipProvider>
    </ThemeProvider>
  </StrictMode>,
)
```

to:

```tsx
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <ThemeProvider>
        <TooltipProvider>
          <MsalProvider instance={msalInstance}>
            <BrowserRouter>
              <App />
            </BrowserRouter>
          </MsalProvider>
        </TooltipProvider>
      </ThemeProvider>
    </ErrorBoundary>
  </StrictMode>,
)
```

- [ ] **Step 4: Build and lint the frontend**

Run from `client/helpdesk-web`:

```bash
npm run build
npm run lint
```

Expected: both succeed with no new errors. (There is no frontend test runner configured in this project, so this build+lint pass is this task's verification - matching the spec's stated scope.)

- [ ] **Step 5: Commit**

```bash
git add client/helpdesk-web/src/lib/clientErrorReporter.ts client/helpdesk-web/src/components/ErrorBoundary.tsx client/helpdesk-web/src/main.tsx
git commit -m "feat: capture frontend crashes and report them to the backend

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 7: Documentation and manual smoke test

**Files:**
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: everything from Tasks 1–6 (this task only documents and hands off a manual verification step to the user - it writes no product code).

- [ ] **Step 1: Add a Phase 9 section to `CLAUDE.md`**

Modify `CLAUDE.md`. After the existing "### Reopen on customer reply (Phase 7b done)" section and before "### Data flow (MVP core loop)", add a new section:

```markdown
### Hardening & polish (Phase 9 done)

Structured backend logging via Serilog (`Program.cs`, `UseSerilog`, console sink with `CompactJsonFormatter` - every log line is now a JSON object; log levels still come from the standard `Logging:LogLevel` section, unchanged). `EmailIngestionService` pushes an `ExternalMessageId`/`ConversationId` correlation scope around each email in its ingestion loop, and `TicketWorkflowService` pushes a `TicketId` scope around `ClaimAsync`/`ReleaseAsync`/`SendReplyAsync`, both via plain `ILogger.BeginScope` (not `Serilog.Context`, to keep `Helpdesk.Application` free of a Serilog dependency per its "depends only on Core" rule) - Serilog's `Microsoft.Extensions.Logging` bridge folds `BeginScope` dictionary state into each log event's structured properties automatically. OpenTelemetry (`Helpdesk.Api/Observability/ObservabilityExtensions.cs`, `AddObservability`) instruments ASP.NET Core/HttpClient/Npgsql for traces and metrics; unlike `GraphApi`/`OpenRouter` this is always registered (there's no failure mode from instrumenting with nowhere to send data) and exports to the console by default, switching to an OTLP exporter when `Otel:OtlpEndpoint` is configured.

CORS is opt-in via a `CorsOrigins` string-array config section (`Helpdesk.Api/Cors/CorsExtensions.cs`, `TryAddCors`); `appsettings.Development.json` sets it to `["http://localhost:5173"]` for the Vite dev server, though the dev proxy makes this unnecessary in practice (documented for when Phase 10 points a real frontend origin at the API). An absent/empty section registers no CORS middleware at all, matching today's behavior.

Frontend crashes are captured without a third-party service: a `POST /api/client-errors` endpoint (`ClientErrorsController`, anonymous - a crash can happen before sign-in) logs the report via the same Serilog pipeline at `Warning`; the frontend's `ErrorBoundary` (render-time crashes) plus `window.onerror`/`unhandledrejection` handlers registered in `main.tsx` (async/event-handler crashes) both call `reportClientError` (`src/lib/clientErrorReporter.ts`), which swallows its own fetch failures so a reporting failure never becomes a second crash. No error data is persisted - it's a log stream, not a feature; a self-hosted dashboard (e.g. GlitchTip) was considered and deferred until Phase 10 gives this a deployment story worth building one for.

Spec: `docs/superpowers/specs/2026-09-27-phase9-hardening-polish-design.md`; plan: `docs/superpowers/plans/2026-09-27-phase9-hardening-polish.md`.

**Manual smoke test (implementation-plan.md item 53):** not yet performed live - needs the user's mailbox and secrets. To repeat, following the same pattern as Phases 5-8's manual verification:
1. From `src/Helpdesk.Api`, ensure `OpenRouter:ApiKey` and `GraphApi:ClientSecret` are set via `dotnet user-secrets`, then `dotnet run`.
2. Send 5-10 varied emails to the monitored mailbox over one or two poll intervals: (a) a clear, unambiguous billing question; (b) a one-line vague message like "hi"; (c) an HTML email with a link, an image, and inline formatting; (d) a reply on an existing thread after that ticket was already replied to; (e) one email sent while the API process is stopped, then the API restarted - confirms the poller catches up on its next tick with nothing lost.
3. Verify via `psql -U helpdesk -h localhost -d helpdesk -c 'select "Subject", "Status", "ReviewReasons" from "Tickets" order by "CreatedAt" desc limit 10;'` that subjects/statuses/review reasons match expectations for each case above.
4. Confirm the running API's console output is structured JSON (one object per log line, with `ExternalMessageId`/`ConversationId`/`TicketId` properties present on the relevant lines) rather than the old plain-text format.
```

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: document Phase 9 hardening and pending manual smoke test

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Final verification (after all tasks)

- [ ] Run `dotnet build Helpdesk.slnx` - succeeds with no new warnings.
- [ ] Run `dotnet test Helpdesk.slnx` - all tests pass.
- [ ] Run `cd client/helpdesk-web && npm run build && npm run lint` - both succeed.
- [ ] Hand off Task 7's manual smoke test to the user - it needs their mailbox and secrets and cannot be run by an agent.
