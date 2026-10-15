using DotCourier.Outbox;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Domain.Common;

namespace Store.Infrastructure.Persistence;

/// <summary>
/// Saves the changes together with the domain events the saved aggregates raised, written to the
/// transactional outbox (<see cref="IOutbox"/>) as <see cref="DomainEventNotification{TEvent}"/>.
/// One <c>SaveChanges</c> writes both, inside the command's transaction: an event exists if and only
/// if its data does. The outbox processor then delivers it to the notification handlers (e-mails,
/// audit) in the background, retrying until they succeed, even after a crash or a restart.
/// </summary>
internal sealed class UnitOfWork(StoreDbContext db, IOutbox outbox) : IUnitOfWork
{
    public async Task CommitAsync(CancellationToken ct)
    {
        var aggregates = db.ChangeTracker.Entries<AggregateRoot>()
            .Select(e => e.Entity)
            .Where(a => a.DomainEvents.Count > 0)
            .ToList();

        foreach (var domainEvent in aggregates.SelectMany(a => a.DomainEvents))
        {
            var notification = (DotCourier.INotification)Activator.CreateInstance(
                typeof(DomainEventNotification<>).MakeGenericType(domainEvent.GetType()), domainEvent)!;
            outbox.Add(notification);
        }

        aggregates.ForEach(a => a.ClearDomainEvents());
        await db.SaveChangesAsync(ct);
    }
}
