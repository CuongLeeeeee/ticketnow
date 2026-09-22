namespace TicketNow.Identity.Api.Domain;

public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public string Role { get; set; } = Roles.User;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}