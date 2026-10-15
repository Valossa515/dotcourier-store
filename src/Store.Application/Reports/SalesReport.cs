using DotCourier;
using Store.Application.Abstractions;
using Store.Domain.Orders;

namespace Store.Application.Reports;

public sealed record TopProductDto(string ProductName, int Quantity, decimal Revenue);

public sealed record SalesReportDto(int PaidOrders, decimal Revenue, IReadOnlyList<TopProductDto> TopProducts, DateTimeOffset GeneratedAt);

/// <summary>
/// An expensive report: at most one runs at a time (bulkhead) and it is cut off after 3 s.
/// <see cref="SimulatedDelayMs"/> lets you make it slow on purpose to see the timeout.
/// </summary>
[HandlerTimeout(3_000)]
public sealed record GetSalesReport(int SimulatedDelayMs = 0) : IQuery<SalesReportDto>;

public sealed class GetSalesReportHandler(IOrderRepository orders) : IQueryHandler<GetSalesReport, SalesReportDto>
{
    public async Task<SalesReportDto> Handle(GetSalesReport query, CancellationToken ct)
    {
        if (query.SimulatedDelayMs > 0)
        {
            await Task.Delay(query.SimulatedDelayMs, ct);
        }

        var paid = await orders.ListByStatusAsync(OrderStatus.Paid, ct);
        var top = paid.SelectMany(o => o.Lines)
            .GroupBy(l => l.ProductName)
            .Select(g => new TopProductDto(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.Total.Amount)))
            .OrderByDescending(t => t.Revenue)
            .Take(5)
            .ToList();

        return new SalesReportDto(paid.Count, paid.Sum(o => o.Total.Amount), top, DateTimeOffset.UtcNow);
    }
}
