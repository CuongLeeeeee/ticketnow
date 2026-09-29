using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TicketNow.BuildingBlocks.Auth;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.BuildingBlocks.Observability;
using TicketNow.Contracts.Identity;
using TicketNow.Identity.Api.Domain;
using TicketNow.Identity.Api.Endpoints;
using TicketNow.Identity.Api.Persistence;
using TicketNow.Identity.Api.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AuthDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
     .UseSnakeCaseNamingConvention());

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddTicketNowJwt(builder.Configuration);
builder.Services.AddTicketNowMessaging(builder.Configuration, "identity");
builder.Services.AddOutboxPublisher<AuthDbContext>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddTicketNowTelemetry(builder.Configuration, "identity", withRedis: true);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await db.Database.MigrateAsync();
    await SeedAdminAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(), app.Configuration);

    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");

app.MapAuthEndpoints();

app.Run();

static async Task SeedAdminAsync(AuthDbContext db, IPasswordHasher<User> hasher, IConfiguration config)
{
    var email = config["SeedAdmin:Email"];
    var password = config["SeedAdmin:Password"];
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;
    if (await db.Users.AnyAsync(u => u.Email == email)) return;

    var admin = new User { Email = email, FullName = "Administrator", Role = Roles.Admin };
    admin.PasswordHash = hasher.HashPassword(admin, password);
    db.Users.Add(admin);
    db.OutboxMessages.Add(OutboxMessage.From(new UserRegistered(admin.Id, admin.Email, admin.FullName, admin.Role)));
    await db.SaveChangesAsync();
}
