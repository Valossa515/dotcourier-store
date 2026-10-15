using DotCourier;
using FluentValidation;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Domain.Catalog;
using Store.Domain.Common;

namespace Store.Application.Catalog;

// ---------------------------------------------------------------------------------------------
// Queries (cached)
// ---------------------------------------------------------------------------------------------

/// <summary>Cached for 60 s; evicted by any command that changes products.</summary>
[Cacheable(60)]
public sealed record GetProduct(Guid Id) : IQuery<Response<ProductDto>>;

public sealed class GetProductHandler(IProductRepository products) : IQueryHandler<GetProduct, Response<ProductDto>>
{
    public async Task<Response<ProductDto>> Handle(GetProduct query, CancellationToken ct) =>
        await products.GetAsync(query.Id, ct) is { } product
            ? product.ToDto()
            : Response.NotFound($"Product {query.Id} not found", code: "PRD-404");
}

/// <summary>Cached for 30 s per search term; rate limited (see AddApplication).</summary>
[Cacheable(30)]
public sealed record ListProducts(string? Search) : IQuery<IReadOnlyList<ProductDto>>;

public sealed class ListProductsHandler(IProductRepository products) : IQueryHandler<ListProducts, IReadOnlyList<ProductDto>>
{
    public async Task<IReadOnlyList<ProductDto>> Handle(ListProducts query, CancellationToken ct) =>
        (await products.ListAsync(query.Search, ct)).Select(p => p.ToDto()).ToList();
}

// ---------------------------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------------------------

[InvalidatesCache(typeof(ListProducts))]
public sealed record CreateProduct(string Name, string Sku, decimal Price, int Stock) : ICommand<Response<ProductDto>>;

public sealed class CreateProductValidator : AbstractValidator<CreateProduct>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100).WithErrorCode("PRD-NAME");
        RuleFor(x => x.Sku).NotEmpty().Matches("^[A-Za-z0-9-]{3,32}$").WithErrorCode("PRD-SKU");
        RuleFor(x => x.Price).GreaterThan(0).WithErrorCode("PRD-PRICE");
        RuleFor(x => x.Stock).GreaterThanOrEqualTo(0).WithErrorCode("PRD-STOCK");
    }
}

public sealed class CreateProductHandler(IProductRepository products, IUnitOfWork unitOfWork)
    : ICommandHandler<CreateProduct, Response<ProductDto>>
{
    public async Task<Response<ProductDto>> Handle(CreateProduct command, CancellationToken ct)
    {
        if (await products.SkuExistsAsync(command.Sku.Trim().ToUpperInvariant(), ct))
        {
            return Response.Conflict($"SKU '{command.Sku}' is already in use", code: "PRD-SKU-TAKEN");
        }

        var product = Product.Create(command.Name, command.Sku, new Money(command.Price), command.Stock);
        products.Add(product);
        await unitOfWork.CommitAsync(ct);
        return Response.Created(product.ToDto());
    }
}

// ---------------------------------------------------------------------------------------------
// Import: all or nothing, thanks to the command's transaction
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Creates several products by sending <see cref="CreateProduct"/> for each one. The nested commands
/// join this command's transaction, so one bad item (duplicate SKU, invalid field) rolls back the
/// products already saved: the import is all or nothing.
/// </summary>
[InvalidatesCache(typeof(ListProducts))]
public sealed record ImportProducts(IReadOnlyList<CreateProduct> Products) : ICommand<Response<IReadOnlyList<ProductDto>>>;

public sealed class ImportProductsValidator : AbstractValidator<ImportProducts>
{
    public ImportProductsValidator() => RuleFor(x => x.Products).NotEmpty().Must(p => p.Count <= 50).WithErrorCode("PRD-IMPORT-SIZE");
}

public sealed class ImportProductsHandler(ISender sender) : ICommandHandler<ImportProducts, Response<IReadOnlyList<ProductDto>>>
{
    public async Task<Response<IReadOnlyList<ProductDto>>> Handle(ImportProducts command, CancellationToken ct)
    {
        var imported = new List<ProductDto>();
        for (var i = 0; i < command.Products.Count; i++)
        {
            // Invalid fields throw (400) and roll back too; business failures come back as a Response.
            var result = await sender.Send(command.Products[i], ct);
            if (!result.IsSuccess)
            {
                return Response.Failure($"Item {i + 1}: {result.Error}", result.StatusCode, code: result.ErrorCode);
            }

            imported.Add(result.Data!);
        }

        return Response.Created<IReadOnlyList<ProductDto>>(imported);
    }
}

[InvalidatesCache(typeof(GetProduct), typeof(ListProducts))]
public sealed record ChangePrice(Guid ProductId, decimal NewPrice) : ICommand<Response<ProductDto>>;

public sealed class ChangePriceValidator : AbstractValidator<ChangePrice>
{
    public ChangePriceValidator() => RuleFor(x => x.NewPrice).GreaterThan(0).WithErrorCode("PRD-PRICE");
}

public sealed class ChangePriceHandler(IProductRepository products, IUnitOfWork unitOfWork)
    : ICommandHandler<ChangePrice, Response<ProductDto>>
{
    public async Task<Response<ProductDto>> Handle(ChangePrice command, CancellationToken ct)
    {
        var product = await products.GetAsync(command.ProductId, ct);
        if (product is null)
        {
            return Response.NotFound($"Product {command.ProductId} not found", code: "PRD-404");
        }

        product.ChangePrice(new Money(command.NewPrice, product.Price.Currency));
        await unitOfWork.CommitAsync(ct);
        return product.ToDto();
    }
}
