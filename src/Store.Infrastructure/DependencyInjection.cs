using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Store.Application.Abstractions;
using Store.Domain.Catalog;
using Store.Domain.Common;
using Store.Infrastructure.Payments;
using Store.Infrastructure.Persistence;

namespace Store.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<StoreDbContext>(o => o.UseSqlite(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<StoreDbContext>());
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();

        services.AddSingleton<PaymentGatewaySimulator>();
        services.AddSingleton<IPaymentGateway, FakePaymentGateway>();
        services.AddSingleton<IEmailSender, LoggingEmailSender>();
        return services;
    }

    /// <summary>Creates the SQLite database (optionally from scratch) and seeds a small catalog.</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, bool reset)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        if (reset)
        {
            await db.Database.EnsureDeletedAsync();
        }

        await db.Database.EnsureCreatedAsync();
        if (await db.Products.AnyAsync())
        {
            return;
        }

        db.Products.AddRange(
            Product.Create("Mechanical keyboard", "KB-001", new Money(249.90m), 10),
            Product.Create("Wireless mouse", "MS-001", new Money(99.90m), 25),
            Product.Create("27\" monitor", "MN-027", new Money(1899.00m), 5),
            Product.Create("Headset", "HS-001", new Money(399.00m), 0));
        await db.CommitAsync(CancellationToken.None);
    }
}

internal sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        logger.LogInformation("📧 E-mail \"{Subject}\" sent", subject); // recipient left out on purpose: personal data
        return Task.CompletedTask;
    }
}
