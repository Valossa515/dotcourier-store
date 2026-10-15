using Store.Domain.Common;

namespace Store.Domain.Catalog;

public sealed class Product : AggregateRoot
{
    private Product()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string Sku { get; private set; } = string.Empty;

    public Money Price { get; private set; } = Money.Zero();

    public int Stock { get; private set; }

    public static Product Create(string name, string sku, Money price, int stock)
    {
        if (price.Amount <= 0)
        {
            throw new DomainException("PRODUCT_INVALID_PRICE", "A product's price must be positive.");
        }

        if (stock < 0)
        {
            throw new DomainException("PRODUCT_INVALID_STOCK", "Stock cannot be negative.");
        }

        var product = new Product
        {
            Name = name.Trim(),
            Sku = sku.Trim().ToUpperInvariant(),
            Price = price,
            Stock = stock,
        };
        product.Raise(new ProductCreated(product.Id, product.Sku, DateTimeOffset.UtcNow));
        return product;
    }

    public void ChangePrice(Money newPrice)
    {
        if (newPrice.Amount <= 0)
        {
            throw new DomainException("PRODUCT_INVALID_PRICE", "A product's price must be positive.");
        }

        var oldPrice = Price;
        Price = newPrice;
        Raise(new ProductPriceChanged(Id, oldPrice.Amount, newPrice.Amount, DateTimeOffset.UtcNow));
    }

    public void RemoveStock(int quantity)
    {
        if (quantity > Stock)
        {
            throw new DomainException(
                "PRODUCT_OUT_OF_STOCK",
                $"Only {Stock} unit(s) of '{Name}' available.",
                DomainErrorKind.Conflict);
        }

        Stock -= quantity;
    }

    public void ReturnStock(int quantity) => Stock += quantity;
}

public sealed record ProductCreated(Guid ProductId, string Sku, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ProductPriceChanged(Guid ProductId, decimal OldPrice, decimal NewPrice, DateTimeOffset OccurredAt) : IDomainEvent;
