using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Domain.Orders;

public enum OrderStatus
{
    Pending,
    Paid,
    Cancelled,
}

public sealed class Order : AggregateRoot
{
    private readonly List<OrderLine> _lines = [];

    private Order()
    {
    }

    public string CustomerEmail { get; private set; } = string.Empty;

    public IReadOnlyList<OrderLine> Lines => _lines;

    public OrderStatus Status { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }

    public string? PaymentId { get; private set; }

    public Money Total => _lines.Aggregate(Money.Zero(), (sum, line) => sum + line.Total);

    /// <summary>Places an order, taking the items out of stock.</summary>
    public static Order Place(string customerEmail, IEnumerable<(Product Product, int Quantity)> items)
    {
        var order = new Order
        {
            CustomerEmail = customerEmail.Trim().ToLowerInvariant(),
            Status = OrderStatus.Pending,
            PlacedAt = DateTimeOffset.UtcNow,
        };

        foreach (var (product, quantity) in items)
        {
            product.RemoveStock(quantity);
            order._lines.Add(new OrderLine(product.Id, product.Name, product.Price, quantity));
        }

        if (order._lines.Count == 0)
        {
            throw new DomainException("ORDER_EMPTY", "An order needs at least one item.");
        }

        order.Raise(new OrderPlaced(order.Id, order.CustomerEmail, order.Total.Amount, DateTimeOffset.UtcNow));
        return order;
    }

    /// <summary>Checked before charging, so a paid or cancelled order is never charged.</summary>
    public void EnsurePayable()
    {
        if (Status != OrderStatus.Pending)
        {
            throw new DomainException("ORDER_NOT_PAYABLE", $"The order is {Status} and cannot be paid.", DomainErrorKind.Conflict);
        }
    }

    public void MarkAsPaid(string paymentId)
    {
        EnsurePayable();
        Status = OrderStatus.Paid;
        PaymentId = paymentId;
        Raise(new OrderPaid(Id, paymentId, Total.Amount, DateTimeOffset.UtcNow));
    }

    /// <summary>Cancels the order and returns the lines whose stock must be given back.</summary>
    public IReadOnlyList<OrderLine> Cancel()
    {
        if (Status == OrderStatus.Paid)
        {
            throw new DomainException("ORDER_ALREADY_PAID", "A paid order cannot be cancelled.", DomainErrorKind.Conflict);
        }

        if (Status == OrderStatus.Cancelled)
        {
            throw new DomainException("ORDER_ALREADY_CANCELLED", "The order is already cancelled.", DomainErrorKind.Conflict);
        }

        Status = OrderStatus.Cancelled;
        Raise(new OrderCancelled(Id, DateTimeOffset.UtcNow));
        return _lines;
    }
}

public sealed class OrderLine
{
    private OrderLine()
    {
    }

    internal OrderLine(Guid productId, string productName, Money unitPrice, int quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException("ORDER_INVALID_QUANTITY", "Quantity must be positive.");
        }

        ProductId = productId;
        ProductName = productName;
        UnitPrice = unitPrice.Amount;
        Currency = unitPrice.Currency;
        Quantity = quantity;
    }

    public Guid ProductId { get; private set; }

    public string ProductName { get; private set; } = string.Empty;

    public decimal UnitPrice { get; private set; }

    public string Currency { get; private set; } = "BRL";

    public int Quantity { get; private set; }

    public Money Total => new Money(UnitPrice, Currency).Times(Quantity);
}

public sealed record OrderPlaced(Guid OrderId, string CustomerEmail, decimal Total, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderPaid(Guid OrderId, string PaymentId, decimal Total, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderCancelled(Guid OrderId, DateTimeOffset OccurredAt) : IDomainEvent;
