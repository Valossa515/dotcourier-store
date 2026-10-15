using Store.Domain.Catalog;
using Store.Domain.Orders;

namespace Store.Application.Abstractions;

// Driven ports: the application says what it needs; Store.Infrastructure provides the adapters.

public interface IProductRepository
{
    Task<Product?> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Product>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<IReadOnlyList<Product>> ListAsync(string? search, CancellationToken ct);

    Task<bool> SkuExistsAsync(string sku, CancellationToken ct);

    void Add(Product product);
}

public interface IOrderRepository
{
    Task<Order?> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Order>> ListByStatusAsync(OrderStatus status, CancellationToken ct);

    void Add(Order order);
}

/// <summary>Saves the changes and then publishes the domain events raised by the saved aggregates.</summary>
public interface IUnitOfWork
{
    Task CommitAsync(CancellationToken ct);
}

public sealed record PaymentReceipt(string PaymentId);

public interface IPaymentGateway
{
    /// <summary>Charges an order. Idempotent per order. Throws when the gateway is unavailable.</summary>
    Task<PaymentReceipt> ChargeAsync(Guid orderId, decimal amount, string currency, CancellationToken ct);
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}
