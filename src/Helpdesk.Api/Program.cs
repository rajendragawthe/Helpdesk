using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.AspNetCore;
using Helpdesk.Api.Auth;
using Helpdesk.Api.Cors;
using Helpdesk.Application;
using Helpdesk.Core.Enums;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Data;
using Helpdesk.Infrastructure.GraphApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddFluentValidationAutoValidation();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddGraphApi(builder.Configuration);
builder.Services.AddOpenRouter(builder.Configuration);
builder.Services.AddKnowledgeBase();
builder.Services.AddApplication(builder.Configuration);

var corsConfigured = builder.Services.TryAddCors(builder.Configuration);

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

if (corsConfigured)
{
    app.UseCors(CorsExtensions.DefaultPolicyName);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
