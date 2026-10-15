using DotCourier;
using Microsoft.Extensions.DependencyInjection;
using Store.Application.Catalog;
using Store.Application.Common;
using Store.Application.Orders;
using Store.Application.Reports;

namespace Store.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers every use case of this assembly in DotCourier and composes the pipeline.
    /// <paramref name="configure"/> lets the host add what belongs to it (e.g. localized messages).
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services, Action<CourierConfiguration>? configure = null) =>
        services.AddCourier(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<PlaceOrder>()
                .AddFluentValidation()
                .AddCaching()
                .AddResilience(o =>
                {
                    // Circuit breaker for every request type: opens after 3 failed calls (50%+) within 30 s.
                    o.CircuitBreaker.MinimumThroughput = 3;
                    o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(20);

                    // The payment gateway is idempotent per order, so paying can be retried safely.
                    o.ForRequest<PayOrder>(r =>
                    {
                        r.Retry.Enabled = true;
                        r.Retry.MaxAttempts = 3;
                        r.Retry.Delay = TimeSpan.FromMilliseconds(200);
                    });

                    // Listing is cheap to call and easy to abuse.
                    o.ForRequest<ListProducts>(r =>
                    {
                        r.RateLimiter.Enabled = true;
                        r.RateLimiter.PermitLimit = 5;
                        r.RateLimiter.Window = TimeSpan.FromSeconds(10);
                    });

                    // Only one sales report at a time.
                    o.ForRequest<GetSalesReport>(r =>
                    {
                        r.Bulkhead.Enabled = true;
                        r.Bulkhead.MaxConcurrency = 1;
                    });
                })
                // Innermost, next to the handler: business-rule failures become 409/422 before retry sees them.
                .AddOpenBehavior(typeof(DomainExceptionBehavior<,>), int.MaxValue - 1);

            configure?.Invoke(cfg);
        });
}
