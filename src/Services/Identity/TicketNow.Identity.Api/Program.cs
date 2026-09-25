using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketNow.BuildingBlocks.Auth;
using TicketNow.Identity.Api.Domain;
using TicketNow.Identity.Api.Persistence;
using TicketNow.Identity.Api.Security;
using TicketNow.BuildingBlocks.Messaging;
using TicketNow.Contracts.Identity;

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

var auth = app.MapGroup("/api/auth");

auth.MapPost("/register", async (RegisterRequest req, AuthDbContext db, IPasswordHasher<User> hasher) =>
{
    var email = req.Email.Trim().ToLowerInvariant();
    if (!email.Contains('@') || req.Password.Length < 8)
        return Results.BadRequest("Email không hợp lệ hoặc mật khẩu dưới 8 ký tự");

    if (await db.Users.AnyAsync(u => u.Email == email))
        return Results.Conflict("Email đã được sử dụng");

    var user = new User { Email = email, FullName = req.FullName, Role = Roles.User };
    user.PasswordHash = hasher.HashPassword(user, req.Password);

    db.Users.Add(user);
    db.OutboxMessages.Add(OutboxMessage.From(new UserRegistered(user.Id, user.Email, user.FullName, user.Role)));
    await db.SaveChangesAsync();
    return Results.Created("/api/auth/me", new { user.Id, user.Email });
});

auth.MapPost("/login", async (LoginRequest req, AuthDbContext db, IPasswordHasher<User> hasher,
    TokenService tokens, IOptions<JwtOptions> jwt) =>
{
    var email = req.Email.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    if (user is null) return Results.Unauthorized();

    var check = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
    if (check == PasswordVerificationResult.Failed) return Results.Unauthorized();
    if (check == PasswordVerificationResult.SuccessRehashNeeded)
        user.PasswordHash = hasher.HashPassword(user, req.Password);

    var response = IssueTokens(user, db, tokens, jwt.Value);
    await db.SaveChangesAsync();
    return Results.Ok(response);
});

auth.MapPost("/refresh", async (RefreshRequest req, AuthDbContext db, TokenService tokens, IOptions<JwtOptions> jwt) =>
{
    var hash = TokenService.Hash(req.RefreshToken);
    var stored = await db.RefreshTokens.Include(r => r.User)
        .FirstOrDefaultAsync(r => r.TokenHash == hash);

    if (stored is null || stored.ExpiresAt <= DateTimeOffset.UtcNow)
        return Results.Unauthorized();

    // Token đã bị dùng rồi mà vẫn có người gửi lại => nghi bị đánh cắp.
    // Thu hồi TOÀN BỘ phiên của user này, buộc đăng nhập lại.
    if (stored.RevokedAt is not null)
    {
        await db.RefreshTokens
            .Where(r => r.UserId == stored.UserId && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow));
        return Results.Unauthorized();
    }

    stored.RevokedAt = DateTimeOffset.UtcNow;   // rotation: token cũ chỉ dùng được 1 lần
    var response = IssueTokens(stored.User, db, tokens, jwt.Value);
    await db.SaveChangesAsync();
    return Results.Ok(response);
});

auth.MapPost("/logout", async (RefreshRequest req, AuthDbContext db) =>
{
    var hash = TokenService.Hash(req.RefreshToken);
    await db.RefreshTokens
        .Where(r => r.TokenHash == hash && r.RevokedAt == null)
        .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow));
    return Results.NoContent();
});

auth.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new
{
    Id = principal.FindFirstValue("sub"),
    Email = principal.FindFirstValue("email"),
    Name = principal.FindFirstValue("name"),
    Role = principal.FindFirstValue("role")
})).RequireAuthorization("authenticated");

app.Run();

static AuthResponse IssueTokens(User user, AuthDbContext db, TokenService tokens, JwtOptions jwt)
{
    var (access, expiresAt) = tokens.CreateAccessToken(user);
    var refresh = TokenService.CreateRefreshToken();

    db.RefreshTokens.Add(new RefreshToken
    {
        UserId = user.Id,
        TokenHash = TokenService.Hash(refresh),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(jwt.RefreshTokenDays)
    });

    return new AuthResponse(access, refresh, expiresAt);
}

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

record RegisterRequest(string Email, string Password, string FullName);
record LoginRequest(string Email, string Password);
record RefreshRequest(string RefreshToken);
record AuthResponse(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt);