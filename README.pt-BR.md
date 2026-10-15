# DotCourier Store

> 🌐 **Language / Idioma:** 🇺🇸 [English](README.md) | 🇧🇷 **Português** (atual)

Uma loja (catálogo, pedidos, pagamento, relatório) em **arquitetura hexagonal com DDD**, usada
para testar na prática cada funcionalidade do [DotCourier](https://github.com/Valossa515/dotcourier).
Consome os pacotes publicados no nuget.org, exatamente como qualquer aplicação.

[![CI](https://github.com/Valossa515/dotcourier-store/actions/workflows/ci.yml/badge.svg)](https://github.com/Valossa515/dotcourier-store/actions/workflows/ci.yml)

## Rodando

```bash
dotnet run --project src/Store.Api
```

Abre o Swagger em **http://localhost:5199/swagger**. Em Development o banco SQLite (`store.db`) é
recriado a cada start, com 4 produtos (um deles, o Headset, sem estoque).

No Visual Studio ou Rider, abra `Store.slnx` e rode o perfil **Store.Api**. Para chamar os
endpoints direto da IDE, use `src/Store.Api/Store.Api.http`. Para rodar tudo de ponta a ponta:

```bash
bash scripts/smoke.sh http://localhost:5199
```

O CI roda esse mesmo script contra uma API recém-iniciada a cada push.

## Arquitetura

```
Store.Domain          Entidades e regras: Product, Order, Money. Eventos de domínio.
                      Sem dependências: nada de DotCourier, EF Core ou ASP.NET.
Store.Application     Casos de uso (commands, queries, handlers) com DotCourier, validators
                      FluentValidation e as portas: IProductRepository, IOrderRepository,
                      IUnitOfWork, IPaymentGateway, IEmailSender.
Store.Infrastructure  Adaptadores das portas: EF Core + SQLite, gateway de pagamento simulado,
                      e-mail que só loga.
Store.Api             Adaptador HTTP: endpoints, Swagger e configuração do DotCourier
                      (mensagens traduzidas via .resx).
```

As dependências apontam para dentro: `Api → Infrastructure → Application → Domain`. O domínio lança
`DomainException`, e a aplicação a converte em resposta HTTP com o `DomainExceptionBehavior`. Os
eventos de domínio são publicados depois do `SaveChanges` como notificações do DotCourier.

## Onde cada funcionalidade do DotCourier aparece

| Funcionalidade | Onde | Como ver no Swagger |
|---|---|---|
| `Dispatch` + `Response<T>` + `ToResult()` | todos os endpoints | qualquer chamada: o status HTTP vem do `Response<T>` |
| FluentValidation no pipeline | `CreateProductValidator`, `PlaceOrderValidator` | `POST /api/products` com campos vazios → 400 com código por campo |
| Erros de domínio → 409/422 | `DomainExceptionBehavior` | pedir o Headset (sem estoque) → 409 `PRODUCT_OUT_OF_STOCK` |
| Atalhos e códigos de erro | handlers | produto inexistente → 404 `PRD-404`; SKU repetido → 409 `PRD-SKU-TAKEN` |
| `[Cacheable]` | `GetProduct` (60 s), `ListProducts` (30 s) | chame duas vezes e veja `Cache hit` no console |
| `[InvalidatesCache]` | `ChangePrice`, `PlaceOrder`, `CancelOrder` | troque o preço e o próximo GET já mostra o preço novo |
| `[Idempotent]` + `IIdempotencyKey` | `PlaceOrder` (header `Idempotency-Key`) | repita o POST com a mesma chave: o mesmo pedido volta, sem duplicar |
| Retry | `PayOrder` (3 tentativas) | `PUT /api/simulation/payment-gateway?mode=Flaky&failures=2` e pague |
| Circuit breaker | todas as requests (50% de falhas, mínimo de 3) | `mode=Down` e pague algumas vezes: 500, 500, 503 |
| Rate limiter | `ListProducts` (5 a cada 10 s) | 6 buscas diferentes seguidas → 429 |
| Bulkhead | `GetSalesReport` (1 por vez) | dois relatórios com `simulatedDelayMs=1500` ao mesmo tempo → 503 |
| `[HandlerTimeout]` | `GetSalesReport` (3 s) | `simulatedDelayMs=5000` → 504 |
| Notificações | `SendOrderConfirmation`, `SendPaymentReceipt` | faça e pague um pedido e veja os 📧 no console |
| Handler genérico aberto | `AuditTrail<T>` | toda notificação gera um `📒 [audit]` no console |
| Mensagens traduzidas | `LocalizedMessages` + `Resources/Messages*.resx` | envie `Accept-Language: pt-BR` e os erros vêm em português, com código |

## Logs

O console mostra o que o DotCourier está fazendo: `[Courier] --> Handling ...`, acertos de cache,
retries (`attempt 2/3`), circuito abrindo, e-mails e auditoria. Nenhum dado de cliente, como e-mail,
vai para o log.

## Licença

[MIT](LICENSE)
