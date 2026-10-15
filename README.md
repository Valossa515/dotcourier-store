# DotCourier Store

> 🌐 **Language / Idioma:** 🇺🇸 **English** (current) | 🇧🇷 [Português](README.pt-BR.md)

A store (catalog, orders, payments, reports) built with **hexagonal architecture and DDD** on top of
[DotCourier](https://github.com/Valossa515/dotcourier), the MIT-licensed CQRS + Mediator library for
.NET. It consumes the packages published on nuget.org, exactly like any application would, and
exercises every DotCourier feature through real HTTP calls.

[![CI](https://github.com/Valossa515/dotcourier-store/actions/workflows/ci.yml/badge.svg)](https://github.com/Valossa515/dotcourier-store/actions/workflows/ci.yml)

## Running

```bash
dotnet run --project src/Store.Api
```

Swagger opens at **http://localhost:5199/swagger**. In Development the SQLite database (`store.db`)
is recreated on every start, seeded with 4 products (the Headset has no stock).

In Visual Studio or Rider, open `Store.slnx` and run the **Store.Api** profile. To call the
endpoints from the IDE, use `src/Store.Api/Store.Api.http`. To check everything end to end:

```bash
bash scripts/smoke.sh http://localhost:5199
```

CI runs the same script against a freshly started API on every push.

## Architecture

```
Store.Domain          Entities and rules: Product, Order, Money. Domain events.
                      No dependencies: no DotCourier, no EF Core, no ASP.NET.
Store.Application     Use cases (commands, queries, handlers) on DotCourier, FluentValidation
                      validators, and the ports: IProductRepository, IOrderRepository,
                      IUnitOfWork, IPaymentGateway, IEmailSender.
Store.Infrastructure  Adapters for the ports: EF Core + SQLite, a simulated payment gateway,
                      an e-mail sender that only logs.
Store.Api             HTTP adapter: endpoints, Swagger, DotCourier configuration
                      (messages translated through .resx).
```

Dependencies point inwards: `Api → Infrastructure → Application → Domain`. The domain throws
`DomainException`; the application turns it into an HTTP answer with `DomainExceptionBehavior`.
Every command runs in a database transaction (`AddPersistenceTransactions`, from
DotCourier.EntityFrameworkCore), and domain events are published as DotCourier notifications only
**after the commit**.

## Where each DotCourier feature shows up

| Feature | Where | How to see it in Swagger |
|---|---|---|
| `Dispatch` + `Response<T>` + `ToResult()` | every endpoint | any call: the HTTP status comes from `Response<T>` |
| FluentValidation in the pipeline | `CreateProductValidator`, `PlaceOrderValidator` | `POST /api/products` with empty fields → 400 with a code per field |
| Domain errors → 409/422 | `DomainExceptionBehavior` | order the Headset (no stock) → 409 `PRODUCT_OUT_OF_STOCK` |
| Status helpers and error codes | handlers | unknown product → 404 `PRD-404`; duplicate SKU → 409 `PRD-SKU-TAKEN` |
| `[Cacheable]` | `GetProduct` (60 s), `ListProducts` (30 s) | call twice and look for `Cache hit` in the console |
| `[InvalidatesCache]` | `ChangePrice`, `PlaceOrder`, `CancelOrder` | change a price: the next GET shows the new one |
| `[Idempotent]` + `IIdempotencyKey` | `PlaceOrder` (`Idempotency-Key` header) | repeat the POST with the same key: same order, no duplicate |
| Retry | `PayOrder` (3 attempts) | `PUT /api/simulation/payment-gateway?mode=Flaky&failures=2`, then pay |
| Circuit breaker | every request (50% failures, at least 3 calls) | `mode=Down` and pay a few times: 500, 500, 503 |
| Rate limiter | `ListProducts` (5 per 10 s) | 6 different searches in a row → 429 |
| Bulkhead | `GetSalesReport` (1 at a time) | two reports with `simulatedDelayMs=1500` at once → 503 |
| `[HandlerTimeout]` | `GetSalesReport` (3 s) | `simulatedDelayMs=5000` → 504 |
| Transaction per command | every command; `PayOrder` is `[NotTransactional]` | `POST /api/products/import` with a duplicate SKU in the 2nd item → 409, and the 1st item is not kept |
| After-commit actions | `StoreDbContext.CommitAsync` (`ICourierTransaction.OnCommittedAsync`) | a rolled-back import publishes no `ProductCreated` |
| Notifications | `SendOrderConfirmation`, `SendPaymentReceipt` | place and pay an order, then see the 📧 lines in the console |
| Open generic handler | `AuditTrail<T>` | every notification logs a `📒 [audit]` line |
| Translated messages | `LocalizedMessages` + `Resources/Messages*.resx` | send `Accept-Language: pt-BR` and errors come back in Portuguese, with codes |

## Logs

The console shows what DotCourier is doing: `[Courier] --> Handling ...`, cache hits, retries
(`attempt 2/3`), transactions (`Transaction committed` / `rolled back`), the circuit opening, e-mails and the audit trail. No customer data, such as
e-mail addresses, goes to the log.

## License

[MIT](LICENSE)
