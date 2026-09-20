---
name: add-endpoint
description: Use whenever the user asks to add a new API endpoint, controller action, or route to Helpdesk.Api — e.g. "add a POST endpoint for X", "add an endpoint to update/delete Y", "expose Z over the API". Drives the full pattern this repo uses: controller action, request DTO + FluentValidation validator, DI registration, and an xUnit unit test for the validator's happy path and a failure case. Do NOT use for non-HTTP application logic (services in Helpdesk.Application) or for frontend API calls.
---

# Add API Endpoint

This repo has one established shape for a new endpoint, set by `UsersController`
and `CreateUserRequestValidator`. Follow it exactly — don't introduce Minimal
API, a mediator pattern, or manual validation checks in the controller body.

## Steps

### 1. Add the controller action

- Put it in the existing controller for that resource under
  `src/Helpdesk.Api/Controllers/` (e.g. `UsersController.cs`), or create a new
  `<Resource>Controller.cs` there if no controller for that resource exists
  yet.
- New controllers follow `UsersController`'s shape:
  ```csharp
  [ApiController]
  [Route("api/<resource>")]
  [Authorize(Policy = "AdminOnly")] // or "AgentOnly" — pick per who should call it
  public class <Resource>Controller(I<Resource>Repository repository) : ControllerBase
  ```
  — primary-constructor DI of `Helpdesk.Core` repository interfaces (never
  `Helpdesk.Infrastructure` types directly), route pattern `api/<lowercase
  plural resource>`.
- Action methods are `async Task<IActionResult>`, named after the HTTP verb's
  intent (`GetAll`, `Create`, `Update`, `Delete`), decorated with
  `[HttpGet]`/`[HttpPost]`/etc.
- Do **not** hand-roll null/empty/format checks in the action body (e.g. no
  `if (string.IsNullOrWhiteSpace(...)) return BadRequest(...)`) — that's the
  validator's job (step 2) and the auto-validation pipeline's job (step 4).
  The action should assume `request` already satisfies its validator by the
  time the action body runs.
- Return the same result shapes already used in this controller family:
  `Ok(...)` for reads, `CreatedAtAction(nameof(GetAll), new { }, entity)` for
  creates, `Conflict(...)` for a uniqueness clash, `NotFound()` for a missing
  resource.

### 2. Add the request DTO + validator

- Define the request as a `record` at the bottom of the controller file,
  exactly like `CreateUserRequest` at the bottom of `UsersController.cs`:
  ```csharp
  public record <Action><Resource>Request(...);
  ```
- Add a validator class in `src/Helpdesk.Api/Validators/`, named
  `<Action><Resource>RequestValidator.cs`, mirroring
  `CreateUserRequestValidator.cs`:
  ```csharp
  using FluentValidation;
  using Helpdesk.Api.Controllers;

  namespace Helpdesk.Api.Validators;

  public class <Action><Resource>RequestValidator : AbstractValidator<<Action><Resource>Request>
  {
      public <Action><Resource>RequestValidator()
      {
          RuleFor(x => x.SomeField)
              .NotEmpty()
              .MaximumLength(...);
      }
  }
  ```
- One validator class per request DTO. Match the existing rule style: chain
  `NotEmpty()`, `MaximumLength(...)`, `EmailAddress()`, etc. — no custom
  `Must(...)` predicates unless the business rule genuinely can't be
  expressed with built-in validators.

### 3. Register the validator

This is almost always a no-op — check `src/Helpdesk.Api/Program.cs` first.
`AddValidatorsFromAssemblyContaining<Program>()` scans the whole `Helpdesk.Api`
assembly, so a validator placed under `Validators/` per step 2 is picked up
automatically. Only touch `Program.cs` if:
- The validator lives in a different assembly (it shouldn't — keep API
  validators in `Helpdesk.Api`), or
- The `AddValidatorsFromAssemblyContaining<Program>()` line is missing
  entirely (see step 4).

### 4. Ensure the validation pipeline is wired up

Check `src/Helpdesk.Api/Program.cs` for these two lines, in the services
section before `builder.Build()`:
```csharp
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddFluentValidationAutoValidation();
```
If either is missing:
1. Confirm the `FluentValidation.AspNetCore` package is referenced in
   `src/Helpdesk.Api/Helpdesk.Api.csproj` (`dotnet add package
   FluentValidation.AspNetCore` from that directory if not — check installed
   version against what's already in the `.csproj` first).
2. Add the `using FluentValidation;` and `using FluentValidation.AspNetCore;`
   imports.
3. Add both lines directly after `builder.Services.AddControllers()...`.

With this in place, a failing validator short-circuits the request with `400`
and a `ValidationProblemDetails` body before the controller action runs — the
action never needs to check `ModelState` manually.

### 5. Write the xUnit test

Validators are tested directly (no HTTP host needed). Test project:
`tests/Helpdesk.Api.Tests`.

- If `tests/Helpdesk.Api.Tests` doesn't exist yet, create it matching the
  exact shape of `tests/Helpdesk.Core.Tests/Helpdesk.Core.Tests.csproj` (same
  SDK, `TargetFramework`, `xunit`/`xunit.runner.visualstudio`/
  `coverlet.collector` versions, `<Using Include="Xunit" />`), but with a
  `ProjectReference` to `..\..\src\Helpdesk.Api\Helpdesk.Api.csproj` instead.
  Register it in `Helpdesk.slnx` under the `/tests/` folder alongside the
  other two test projects.
- Delete the placeholder `UnitTest1.cs` if this is the first real test added
  to a freshly-created test project.
- Name the test file `<Action><Resource>RequestValidatorTests.cs`, one test
  class per validator, e.g.:
  ```csharp
  using Helpdesk.Api.Controllers;
  using Helpdesk.Api.Validators;

  namespace Helpdesk.Api.Tests.Validators;

  public class <Action><Resource>RequestValidatorTests
  {
      private readonly <Action><Resource>RequestValidator _validator = new();

      [Fact]
      public void Validate_WithValidRequest_Succeeds()
      {
          var request = new <Action><Resource>Request(/* valid values */);

          var result = _validator.Validate(request);

          Assert.True(result.IsValid);
      }

      [Fact]
      public void Validate_With<InvalidField>_Fails()
      {
          var request = new <Action><Resource>Request(/* one invalid field */);

          var result = _validator.Validate(request);

          Assert.False(result.IsValid);
          Assert.Contains(result.Errors, e => e.PropertyName == nameof(<Action><Resource>Request.<Field>));
      }
  }
  ```
- Cover: one happy-path case (`IsValid` true) and at least one failure case
  per meaningfully distinct rule (empty required field, over-length field,
  bad format if `EmailAddress()`/similar is used). Don't add
  `FluentValidation.TestHelper` as a dependency for this — plain
  `Validate(request)` + asserting on `result.IsValid` /
  `result.Errors` is enough and keeps the test project's package list as
  small as the other two.
- Run it with:
  ```
  dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj
  ```

## Reference

- Controller pattern: `src/Helpdesk.Api/Controllers/UsersController.cs`
- Validator pattern: `src/Helpdesk.Api/Validators/CreateUserRequestValidator.cs`
- DI wiring: `src/Helpdesk.Api/Program.cs`
- Architecture rules (layering, request validation conventions):
  `CLAUDE.md` — "Backend: layered, dependency direction matters" and
  "Request validation" sections
