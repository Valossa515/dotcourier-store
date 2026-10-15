using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Store.Application.Abstractions;

namespace Store.Infrastructure.Payments;

public enum GatewayMode
{
    /// <summary>Every charge succeeds.</summary>
    Healthy,

    /// <summary>The next N charges fail, then it recovers (retry should absorb it).</summary>
    Flaky,

    /// <summary>Every charge fails (the circuit breaker should open).</summary>
    Down,
}

/// <summary>Controls the fake gateway so resilience can be exercised from Swagger (see /api/simulation).</summary>
public sealed class PaymentGatewaySimulator
{
    private readonly Lock _lock = new();
    private int _failuresLeft;

    public GatewayMode Mode { get; private set; } = GatewayMode.Healthy;

    public int Attempts { get; private set; }

    public void Set(GatewayMode mode, int failures)
    {
        lock (_lock)
        {
            Mode = mode;
            _failuresLeft = Math.Max(0, failures);
            Attempts = 0;
        }
    }

    internal bool NextCallFails()
    {
        lock (_lock)
        {
            Attempts++;
            switch (Mode)
            {
                case GatewayMode.Down:
                    return true;
                case GatewayMode.Flaky when _failuresLeft > 0:
                    _failuresLeft--;
                    return true;
                default:
                    return false;
            }
        }
    }

    public object Describe()
    {
        lock (_lock)
        {
            return new { mode = Mode.ToString(), failuresLeft = _failuresLeft, attempts = Attempts };
        }
    }
}

public sealed class PaymentGatewayUnavailableException() : Exception("Payment gateway did not respond (simulated).");

/// <summary>Stand-in for a real payment provider: slow, sometimes down, idempotent per order.</summary>
internal sealed class FakePaymentGateway(PaymentGatewaySimulator simulator, ILogger<FakePaymentGateway> logger) : IPaymentGateway
{
    private readonly ConcurrentDictionary<Guid, string> _receipts = new();

    public async Task<PaymentReceipt> ChargeAsync(Guid orderId, decimal amount, string currency, CancellationToken ct)
    {
        await Task.Delay(150, ct);

        if (_receipts.TryGetValue(orderId, out var existing))
        {
            return new PaymentReceipt(existing);
        }

        if (simulator.NextCallFails())
        {
            logger.LogWarning("💳 Payment gateway failed for order {OrderId} (attempt {Attempt})", orderId, simulator.Attempts);
            throw new PaymentGatewayUnavailableException();
        }

        var paymentId = "pay_" + Guid.NewGuid().ToString("N")[..12];
        _receipts[orderId] = paymentId;
        logger.LogInformation("💳 Charged {Amount} {Currency} for order {OrderId}: {PaymentId}", amount, currency, orderId, paymentId);
        return new PaymentReceipt(paymentId);
    }
}
