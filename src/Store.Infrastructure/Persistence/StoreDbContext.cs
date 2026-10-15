using DotCourier;
using DotCourier.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Domain.Catalog;
using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Infrastructure.Persistence;

public sealed class StoreDbContext(DbContextOptions<StoreDbContext> options, IPublisher publisher, ICourierTransaction transaction)
    : DbContext(options), IUnitOfWork
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<Order> Orders => Set<Order>();

    /// <summary>
    /// Saves, then publishes the domain events raised by the saved aggregates as DotCourier
    /// notifications (<see cref="DomainEventNotification{TEvent}"/>). Inside a command the save is part
    /// of the command's transaction, so the events wait for its commit
    /// (<see cref="ICourierTransaction.OnCommittedAsync"/>): a command that fails later, after saving,
    /// sends no e-mail about data that was rolled back.
    /// </summary>
    public async Task CommitAsync(CancellationToken ct)
    {
        var aggregates = ChangeTracker.Entries<AggregateRoot>()
            .Select(e => e.Entity)
            .Where(a => a.DomainEvents.Count > 0)
            .ToList();
        var events = aggregates.SelectMany(a => a.DomainEvents).ToList();
        aggregates.ForEach(a => a.ClearDomainEvents());

        await SaveChangesAsync(ct);
        await transaction.OnCommittedAsync(token => PublishAsync(events, token), ct);
    }

    private async Task PublishAsync(List<IDomainEvent> events, CancellationToken ct)
    {
        foreach (var domainEvent in events)
        {
            var notification = Activator.CreateInstance(typeof(DomainEventNotification<>).MakeGenericType(domainEvent.GetType()), domainEvent)!;
            await publisher.Publish(notification, ct);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
