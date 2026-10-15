using DotCourier.Outbox;
using Microsoft.EntityFrameworkCore;
using Store.Domain.Catalog;
using Store.Domain.Orders;

namespace Store.Infrastructure.Persistence;

public sealed class StoreDbContext(DbContextOptions<StoreDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The courier_outbox table: domain events are stored here, in the same transaction as the data.
        modelBuilder.AddCourierOutbox();

        modelBuilder.Entity<Product>(product =>
        {
            product.HasKey(p => p.Id);
            product.Property(p => p.Name).HasMaxLength(100);
            product.Property(p => p.Sku).HasMaxLength(32);
            product.HasIndex(p => p.Sku).IsUnique();
            product.OwnsOne(p => p.Price, money =>
            {
                money.Property(m => m.Amount).HasColumnName("Price");
                money.Property(m => m.Currency).HasColumnName("Currency").HasMaxLength(3);
            });
            product.Ignore(p => p.DomainEvents);
        });

        modelBuilder.Entity<Order>(order =>
        {
            order.HasKey(o => o.Id);
            order.Property(o => o.CustomerEmail).HasMaxLength(200);
            order.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
            order.Ignore(o => o.Total);
            order.Ignore(o => o.DomainEvents);
            order.OwnsMany(o => o.Lines, line =>
            {
                line.WithOwner().HasForeignKey("OrderId");
                line.Property<int>("Id");
                line.HasKey("Id");
                line.Property(l => l.ProductName).HasMaxLength(100);
                line.Property(l => l.Currency).HasMaxLength(3);
                line.Ignore(l => l.Total);
            });
            order.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }
}
