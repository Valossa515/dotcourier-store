using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Domain.Catalog;
using Store.Domain.Orders;

namespace Store.Infrastructure.Persistence;

internal sealed class ProductRepository(StoreDbContext db) : IProductRepository
{
    public Task<Product?> GetAsync(Guid id, CancellationToken ct) =>
        db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Product>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<Product>> ListAsync(string? search, CancellationToken ct)
    {
        var query = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(p => EF.Functions.Like(p.Name, pattern) || EF.Functions.Like(p.Sku, pattern));
        }

        return await query.OrderBy(p => p.Name).ToListAsync(ct);
    }

    public Task<bool> SkuExistsAsync(string sku, CancellationToken ct) =>
        db.Products.AnyAsync(p => p.Sku == sku, ct);

    public void Add(Product product) => db.Products.Add(product);
}

internal sealed class OrderRepository(StoreDbContext db) : IOrderRepository
{
    public Task<Order?> GetAsync(Guid id, CancellationToken ct) =>
        db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<Order>> ListByStatusAsync(OrderStatus status, CancellationToken ct) =>
        await db.Orders.AsNoTracking().Where(o => o.Status == status).ToListAsync(ct);

    public void Add(Order order) => db.Orders.Add(order);
}
