using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace TicketNow.BuildingBlocks.Auth;

public static class JwtAuthExtensions
{
    public static IServiceCollection AddTicketNowJwt(this IServiceCollection services, IConfiguration config, Action<JwtBearerOptions>? configure = null)
    {
        var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>()
                  ?? throw new InvalidOperationException("Thiếu cấu hình Jwt");

        if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
            throw new InvalidOperationException("Jwt:SigningKey phải dài tối thiểu 32 byte");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "role"
                };
                configure?.Invoke(o);
            });

        services.AddAuthorizationBuilder()
            .AddPolicy("admin", p => p.RequireRole("Admin"))
            .AddPolicy("authenticated", p => p.RequireAuthenticatedUser());

        return services;
    }
}