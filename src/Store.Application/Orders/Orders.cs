using DotCourier;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Store.Application.Abstractions;
using Store.Application.Catalog;
using Store.Application.Common;
using Store.Domain.Orders;

namespace Store.Application.Orders;

// ---------------------------------------------------------------------------------------------
// Place an order: idempotent by the client's Idempotency-Key header
// ---------------------------------------------------------------------------------------------

public sealed record PlaceOrderItem(Guid ProductId, int Quantity);

/// <summary>
/// Idempotent for 24 h by <see cref="IdempotencyKey"/>: a client retrying after a timeout gets the
/// same order instead of a duplicate. Takes stock, so it evicts the product caches.
/// </summary>
[Idempotent(86_400)]
[InvalidatesCache(typeof(GetProduct), typeof(ListProducts))]
public sealed record PlaceOrder(string IdempotencyKey, string CustomerEmail, IReadOnlyList<PlaceOrderItem> Items)
    : ICommand<Response<OrderDto>>, IIdempotencyKey;

public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrder>
{
    public PlaceOrderValidator()
    {
        RuleFor(x => x.IdempotencyKey).NotEmpty().WithMessage("The Idempotency-Key header is required.").WithErrorCode("ORD-KEY");
        RuleFor(x => x.CustomerEmail).NotEmpty().EmailAddress().WithErrorCode("ORD-EMAIL");
        RuleFor(x => x.Items).NotEmpty().WithErrorCode("ORD-ITEMS");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty().WithErrorCode("ORD-PRODUCT");
            item.RuleFor(i => i.Quantity).InclusiveBetween(1, 100).WithErrorCode("ORD-QUANTITY");
        });
    }
}

public sealed class PlaceOrderHandler(IProductRepository products, IOrderRepository orders, IUnitOfWork unitOfWork)
    : ICommandHandler<PlaceOrder, Response<OrderDto>>
{
    public async Task<Response<OrderDto>> Handle(PlaceOrder command, CancellationToken ct)
    {
        var ids = command.Items.Select(i => i.ProductId).Distinct().ToList();
        var found = (await products.GetManyAsync(ids, ct)).ToDictionary(p => p.Id);

        var missing = ids.FirstOrDefault(id => !found.ContainsKey(id));
        if (missing != Guid.Empty)
        {
            return Response.NotFound($"Product {missing} not found", code: "PRD-404");
        }

        // Domain rules (stock, empty order) throw DomainException -> 409/422 via DomainExceptionBehavior.
        var order = Order.Place(command.CustomerEmail, command.Items.Select(i => (found[i.ProductId], i.Quantity)));
        orders.Add(order);
        await unitOfWork.CommitAsync(ct);
        return Response.Created(order.ToDto());
    }
}

// ---------------------------------------------------------------------------------------------
// Pay an order: calls an unreliable payment gateway, protected by retry + circuit breaker
// ---------------------------------------------------------------------------------------------

public sealed record PayOrder(Guid OrderId) : ICommand<Response<OrderDto>>;

public sealed class PayOrderHandler(IOrderRepository orders, IPaymentGateway gateway, IUnitOfWork unitOfWork, ILogger<PayOrderHandler> logger)
    : ICommandHandler<PayOrder, Response<OrderDto>>
{
    public async Task<Response<OrderDto>> Handle(PayOrder command, CancellationToken ct)
    {
        var order = await orders.GetAsync(command.OrderId, ct);
        if (order is null)
        {
            return Response.NotFound($"Order {command.OrderId} not found", code: "ORD-404");
        }

        order.EnsurePayable();
        logger.LogInformation("Charging order {OrderId}: {Amount} {Currency}", order.Id, order.Total.Amount, order.Total.Currency);
        var receipt = await gateway.ChargeAsync(order.Id, order.Total.Amount, order.Total.Currency, ct);
        order.MarkAsPaid(receipt.PaymentId);
        await unitOfWork.CommitAsync(ct);
        return order.ToDto();
    }
}

// ---------------------------------------------------------------------------------------------
// Cancel an order: gives the stock back
// ---------------------------------------------------------------------------------------------

[InvalidatesCache(typeof(GetProduct), typeof(ListProducts))]
public sealed record CancelOrder(Guid OrderId) : ICommand<Response<OrderDto>>;

public sealed class CancelOrderHandler(IOrderRepository orders, IProductRepository products, IUnitOfWork unitOfWork)
    : ICommandHandler<CancelOrder, Response<OrderDto>>
{
    public async Task<Response<OrderDto>> Handle(CancelOrder command, CancellationToken ct)
    {
        var order = await orders.GetAsync(command.OrderId, ct);
        if (order is null)
        {
            return Response.NotFound($"Order {command.OrderId} not found", code: "ORD-404");
        }

        var lines = order.Cancel();
        var stock = (await products.GetManyAsync(lines.Select(l => l.ProductId).ToList(), ct)).ToDictionary(p => p.Id);
        foreach (var line in lines)
        {
            if (stock.TryGetValue(line.ProductId, out var product))
            {
                product.ReturnStock(line.Quantity);
            }
        }

        await unitOfWork.CommitAsync(ct);
        return order.ToDto();
    }
}

public sealed record GetOrder(Guid OrderId) : IQuery<Response<OrderDto>>;

public sealed class GetOrderHandler(IOrderRepository orders) : IQueryHandler<GetOrder, Response<OrderDto>>
{
    public async Task<Response<OrderDto>> Handle(GetOrder query, CancellationToken ct) =>
        await orders.GetAsync(query.OrderId, ct) is { } order
            ? order.ToDto()
            : Response.NotFound($"Order {query.OrderId} not found", code: "ORD-404");
}
