using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketNow.BuildingBlocks.Auth;
using TicketNow.Contracts.Identity;
using TicketNow.Identity.Api.Domain;
using TicketNow.Identity.Api.Persistence;
using TicketNow.Identity.Api.Security;

namespace TicketNow.Identity.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/register", Register);
        auth.MapPost("/login", Login);
        auth.MapPost("/refresh", Refresh);
        auth.MapPost("/logout", Logout);
        auth.MapGet("/me", Me).RequireAuthorization("authenticated");

        return app;
    }

    private static async Task<IResult> Register(RegisterRequest req, AuthDbContext db, IPasswordHasher<User> hasher)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        if (!email.Contains('@') || req.Password.Length < 8)
            return Results.BadRequest("Invalid email or password shorter than 8 characters");

        if (await db.Users.AnyAsync(u => u.Email == email))
            return Results.Conflict("Email is already in use");

        var user = new User { Email = email, FullName = req.FullName, Role = Roles.User };
        user.PasswordHash = hasher.HashPassword(user, req.Password);

        db.Users.Add(user);
        db.OutboxMessages.Add(OutboxMessage.From(new UserRegistered(user.Id, user.Email, user.FullName, user.Role)));
        await db.SaveChangesAsync();
        return Results.Created("/api/auth/me", new { user.Id, user.Email });
    }

    private static async Task<IResult> Login(LoginRequest req, AuthDbContext db, IPasswordHasher<User> hasher,
        TokenService tokens, IOptions<JwtOptions> jwt)
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
    }

    private static async Task<IResult> Refresh(RefreshRequest req, AuthDbContext db, TokenService tokens, IOptions<JwtOptions> jwt)
    {
        var hash = TokenService.Hash(req.RefreshToken);
        var stored = await db.RefreshTokens.Include(r => r.User)
            .FirstOrDefaultAsync(r => r.TokenHash == hash);

        if (stored is null || stored.ExpiresAt <= DateTimeOffset.UtcNow)
            return Results.Unauthorized();

        // A token that was already used is being replayed => it may have been stolen.
        // Revoke ALL of this user's sessions and force a new login.
        if (stored.RevokedAt is not null)
        {
            await db.RefreshTokens
                .Where(r => r.UserId == stored.UserId && r.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow));
            return Results.Unauthorized();
        }

        stored.RevokedAt = DateTimeOffset.UtcNow;   // rotation: each refresh token is single-use
        var response = IssueTokens(stored.User, db, tokens, jwt.Value);
        await db.SaveChangesAsync();
        return Results.Ok(response);
    }

    private static async Task<IResult> Logout(RefreshRequest req, AuthDbContext db)
    {
        var hash = TokenService.Hash(req.RefreshToken);
        await db.RefreshTokens
            .Where(r => r.TokenHash == hash && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow));
        return Results.NoContent();
    }

    private static IResult Me(ClaimsPrincipal principal) => Results.Ok(new
    {
        Id = principal.FindFirstValue("sub"),
        Email = principal.FindFirstValue("email"),
        Name = principal.FindFirstValue("name"),
        Role = principal.FindFirstValue("role")
    });

    private static AuthResponse IssueTokens(User user, AuthDbContext db, TokenService tokens, JwtOptions jwt)
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
}

public record RegisterRequest(string Email, string Password, string FullName);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt);
