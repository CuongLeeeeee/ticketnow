namespace TicketNow.Contracts.Identity;

public record UserRegistered(Guid UserId, string Email, string FullName, string Role);