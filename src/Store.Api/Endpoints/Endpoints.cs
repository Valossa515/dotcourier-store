using DotCourier;
using Microsoft.AspNetCore.Mvc;
using Store.Application.Catalog;
using Store.Application.Common;
using Store.Application.Orders;
using Store.Application.Reports;
using Store.Infrastructure.Payments;

namespace Store.Api.Endpoints;

// Driving adapter: HTTP -> use cases. Endpoints only translate HTTP into commands/queries and back;
// no try/catch, no status-code decisions: DotCourier's Response<T> carries them.

public sealed record CreateProductRequest(string Name, string Sku, decimal Price, int Stock);

public sealed record ChangePriceRequest(decimal NewPrice);

public sealed record PlaceOrderRequest(string CustomerEmail, List<PlaceOrderItem> Items);

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/products").WithTags("Products");

        products.MapGet("/", (string? search, ICourier courier, CancellationToken ct) =>
                courier.Dispatch(new ListProducts(search), ct).ToResult())
            .WithSummary("List products")
            .WithDescription("[Cacheable(30)] per search term. Rate limited to 5 calls per 10 s: the 6th answers 429.")
            .Produces<Response<IReadOnlyList<ProductDto>>>()
            .Produces<Response<object>>(StatusCodes.Status429TooManyRequests);

        products.MapGet("/{id:guid}", (Guid id, ICourier courier, CancellationToken ct) =>
                courier.Dispatch(new GetProduct(id), ct).ToResult())
            .WithSummary("Get a product")
            .WithDescription("[Cacheable(60)]: the second call is served from the cache (see the log). 404 is never cached.")
            .Produces<Response<ProductDto>>()
            .Produces<Response<object>>(StatusCodes.Status404NotFound);

        products.MapPost("/", (CreateProductRequest body, ICourier courier, CancellationToken ct) =>
                courier.Dispatch(new CreateProduct(body.Name, body.Sku, body.Price, body.Stock), ct).ToResult())
            .WithSummary("Create a product")
            .WithDescription("FluentValidation runs before the handler (400 with field codes). Duplicate SKU: 409 PRD-SKU-TAKEN.")
            .Produces<Response<ProductDto>>(StatusCodes.Status201Created)
            .Produces<Response<object>>(StatusCodes.Status400BadRequest)
            .Produces<Response<object>>(StatusCodes.Status409Conflict);

        products.MapPost("/import", (CreateProductRequest[] body, ICourier courier, CancellationToken ct) =>
                courier.Dispatch(new ImportProducts(body.Select(p => new CreateProduct(p.Name, p.Sku, p.Price, p.Stock)).ToList()), ct).ToResult())
            .WithSummary("Import products (all or nothing)")
            .WithDescription("One transaction for the whole import: a duplicate SKU or an invalid item rolls back the products already saved (409 / 400). See 'Transaction rolled back' in the log.")
            .Produces<Response<IReadOnlyList<ProductDto>>>(StatusCodes.Status201Created)
            .Produces<Response<object>>(StatusCodes.Status400BadRequest)
            .Produces<Response<object>>(StatusCodes.Status409Conflict);

        products.MapPut("/{id:guid}/price", (Guid id, ChangePriceRequest body, ICourier courier, CancellationToken ct) =>
                courier.Dispatch(new ChangePrice(id, body.NewPrice), ct).ToResult())
            .WithSummary("Change a product's price")
            .WithDescription("[InvalidatesCache(GetProduct, ListProducts)]: the next GET runs the handler again with the new price.")
            .Produces<Response<ProductDto>>()
            .Produces<Response<object>>(StatusCodes.Status400BadRequest)
            .Produces<Response<object>>(StatusCodes.Status404NotFound);
    }
}

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/orders").WithTags("Orders");

        orders.MapPost("/", ([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, PlaceOrderRequest body, ICourier courier, CancellationToken ct) =>
                courier.Dispatch(new PlaceOrder(idempotencyKey ?? string.Empty, body.CustomerEmail, body.Items), ct).ToResult())
            .WithSummary("Place an order")
            .WithDescription("[Idempotent]: send the same Idempotency-Key twice and you get the same order back, without a second order " +
                             "or a second stock removal. Not enough stock: 409 PRODUCT_OUT_OF_STOCK (a domain rule).")
            .Produces<Response<OrderDto>>(StatusCodes.Status201Created)
            .Produces<Response<object>>(StatusCodes.Status400BadRequest)
            .Produces<Response<object>>(StatusCodes.Status404NotFound)
            .Produces<Response<object>>(StatusCodes.Status409Conflict);

        orders.MapGet("/{id:guid}", (Guid id, ICourier courier, CancellationToken ct) =>
                courier.Dispatch(new GetOrder(id), ct).ToResult())
            .WithSummary("Get an order")
            .Produces<Response<OrderDto>>()
            .Produces<Response<object>>(StatusCodes.Status404NotFound);

        orders.MapPost("/{id:guid}/pay", (Guid id, ICourier courier, CancellationToken ct) =>
                courier.Dispatch(new PayOrder(id), ct).ToResult())
            .WithSummary("Pay an order")
            .WithDescription("Calls the (fake) payment gateway with retry (3 attempts) and a circuit breaker. " +
                             "Use /api/simulation/payment-gateway to make the gateway flaky or down.")
            .Produces<Response<OrderDto>>()
            .Produces<Response<object>>(StatusCodes.Status409Conflict)
            .Produces<Response<object>>(StatusCodes.Status500InternalServerError)
            .Produces<Response<object>>(StatusCodes.Status503ServiceUnavailable);

        orders.MapPost("/{id:guid}/cancel", (Guid id, ICourier courier, CancellationToken ct) =>
                courier.Dispatch(new CancelOrder(id), ct).ToResult())
            .WithSummary("Cancel an order")
            .WithDescription("Gives the stock back. A paid order cannot be cancelled: 409 ORDER_ALREADY_PAID.")
            .Produces<Response<OrderDto>>()
            .Produces<Response<object>>(StatusCodes.Status409Conflict);
    }
}

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reports/sales", (int? simulatedDelayMs, ICourier courier, CancellationToken ct) =>
                courier.Dispatch(new GetSalesReport(simulatedDelayMs ?? 0), ct).ToResult())
            .WithTags("Reports")
            .WithSummary("Sales report")
            .WithDescription("[HandlerTimeout(3000)] + bulkhead of 1. simulatedDelayMs=5000 shows the 504; two calls at once show the 503.")
            .Produces<Response<SalesReportDto>>()
            .Produces<Response<object>>(StatusCodes.Status503ServiceUnavailable)
            .Produces<Response<object>>(StatusCodes.Status504GatewayTimeout);
    }
}

public static class SimulationEndpoints
{
    public static void MapSimulationEndpoints(this IEndpointRouteBuilder app)
    {
        var simulation = app.MapGroup("/api/simulation").WithTags("Simulation (testing only)");

        simulation.MapGet("/payment-gateway", (PaymentGatewaySimulator simulator) => Results.Ok(simulator.Describe()))
            .WithSummary("Current payment gateway behavior");

        simulation.MapPut("/payment-gateway", (GatewayMode mode, int? failures, PaymentGatewaySimulator simulator) =>
            {
                simulator.Set(mode, failures ?? 0);
                return Results.Ok(simulator.Describe());
            })
            .WithSummary("Make the payment gateway Healthy, Flaky (next N calls fail) or Down")
            .WithDescription("Flaky with failures=2: paying succeeds thanks to retry. Down: payments fail, and after 3 failed payments the circuit opens (503).");
    }
}
