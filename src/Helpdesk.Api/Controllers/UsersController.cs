using Helpdesk.Core.Entities;
using Helpdesk.Core.Enums;
using Helpdesk.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Policy = "AdminOnly")]
public class UsersController(IUserRepository userRepository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var users = await userRepository.GetAllAsync();
        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return BadRequest("Email and DisplayName are required.");
        }

        var existing = await userRepository.GetByEmailAsync(request.Email);
        if (existing is not null)
        {
            return Conflict($"A user with email '{request.Email}' already exists.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = request.Email,
            DisplayName = request.DisplayName,
            Role = Role.Agent,
            ExternalObjectId = null,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await userRepository.AddAsync(user);

        return CreatedAtAction(nameof(GetAll), new { }, user);
    }
}

public record CreateUserRequest(string Email, string DisplayName);
