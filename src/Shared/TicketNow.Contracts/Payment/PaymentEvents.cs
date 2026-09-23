namespace TicketNow.Contracts.Payment;

public record PaymentSucceeded(Guid PaymentId, Guid OrderId, Guid UserId, decimal Amount, string Currency, string ProviderRef);

public record PaymentFailed(Guid PaymentId, Guid OrderId, Guid UserId, string Reason);

public record PaymentRefunded(Guid PaymentId, Guid OrderId, Guid UserId, decimal Amount, string Currency);