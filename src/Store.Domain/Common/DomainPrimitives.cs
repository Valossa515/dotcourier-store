namespace Store.Domain.Common;

/// <summary>Something that happened in the domain, raised by an aggregate and published after it is saved.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Base class of aggregates: identity plus the domain events raised since the last save.</summary>
public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public Guid Id { get; protected set; } = Guid.NewGuid();

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

public enum DomainErrorKind
{
    /// <summary>The operation breaks a business rule (maps to 422).</summary>
    RuleViolation,

    /// <summary>The operation conflicts with the current state (maps to 409).</summary>
    Conflict,
}

/// <summary>A business rule was broken. The domain knows nothing about HTTP or DotCourier; the application layer translates this.</summary>
public sealed class DomainException(string code, string message, DomainErrorKind kind = DomainErrorKind.RuleViolation)
    : Exception(message)
{
    public string Code { get; } = code;

    public DomainErrorKind Kind { get; } = kind;
}

/// <summary>An amount of money in a currency. Immutable value object.</summary>
public sealed record Money
{
    private Money()
    {
    }

    public Money(decimal amount, string currency = "BRL")
    {
        if (amount < 0)
        {
            throw new DomainException("MONEY_NEGATIVE", "An amount of money cannot be negative.");
        }

        Amount = decimal.Round(amount, 2);
        Currency = currency;
    }

    public decimal Amount { get; private init; }

    public string Currency { get; private init; } = "BRL";

    public static Money Zero(string currency = "BRL") => new(0, currency);

    public Money Times(int quantity) => new(Amount * quantity, Currency);

    public static Money operator +(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new DomainException("MONEY_CURRENCY_MISMATCH", $"Cannot add {left.Currency} and {right.Currency}.");
        }

        return new Money(left.Amount + right.Amount, left.Currency);
    }
}
