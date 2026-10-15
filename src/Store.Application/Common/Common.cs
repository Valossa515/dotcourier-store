using DotCourier;
using Store.Domain.Catalog;
using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Application.Common;

public sealed record ProductDto(Guid Id, string Name, string Sku, decimal Price, string Currency, int Stock);

public sealed record OrderLineDto(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, decimal Total);

public sealed record OrderDto(
    Guid Id,
    string CustomerEmail,
    string Status,
    decimal Total,
    string Currency,
    string? PaymentId,
    DateTimeOffset PlacedAt,
    IReadOnlyList<OrderLineDto> Lines);

internal static class Mapping
{
    public static ProductDto ToDto(this Product p) =>
        new(p.Id, p.Name, p.Sku, p.Price.Amount, p.Price.Currency, p.Stock);

    public static OrderDto ToDto(this Order o) =>
        new(o.Id, o.CustomerEmail, o.Status.ToString(), o.Total.Amount, o.Total.Currency, o.PaymentId, o.PlacedAt,
            o.Lines.Select(l => new OrderLineDto(l.ProductId, l.ProductName, l.UnitPrice, l.Quantity, l.Total.Amount)).ToList());
}

/// <summary>
/// Carries a domain event through DotCourier as a notification, so the domain does not depend on
/// DotCourier. Handlers subscribe to e.g. <c>INotificationHandler&lt;DomainEventNotification&lt;OrderPlaced&gt;&gt;</c>.
/// </summary>
public sealed record DomainEventNotification<TEvent>(TEvent Event) : INotification
    where TEvent : IDomainEvent;

/// <summary>
/// Turns a <see cref="DomainException"/> into a <see cref="CourierException"/>, so business-rule
/// failures reach the client with their message, code and a 409/422 status (instead of a masked 500).
/// Registered innermost, so retry never repeats a request that broke a business rule.
/// </summary>
public sealed class DomainExceptionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(cancellationToken);
        }
        catch (DomainException ex)
        {
            var status = ex.Kind == DomainErrorKind.Conflict ? 409 : 422;
            throw new CourierException(ex.Message, ex, status) { Code = ex.Code };
        }
    }
}
