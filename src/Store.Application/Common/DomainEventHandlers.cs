using DotCourier;
using DotCourier.Behaviors;
using Microsoft.Extensions.Logging;
using Store.Application.Abstractions;
using Store.Domain.Orders;

namespace Store.Application.Common;

public sealed class SendOrderConfirmation(IEmailSender email) : INotificationHandler<DomainEventNotification<OrderPlaced>>
{
    public Task Handle(DomainEventNotification<OrderPlaced> notification, CancellationToken ct) =>
        email.SendAsync(
            notification.Event.CustomerEmail,
            "We received your order",
            $"Order {notification.Event.OrderId} placed. Total: {notification.Event.Total:0.00}.",
            ct);
}

public sealed class SendPaymentReceipt(IEmailSender email, IOrderRepository orders) : INotificationHandler<DomainEventNotification<OrderPaid>>
{
    public async Task Handle(DomainEventNotification<OrderPaid> notification, CancellationToken ct)
    {
        var order = await orders.GetAsync(notification.Event.OrderId, ct);
        if (order is not null)
        {
            await email.SendAsync(order.CustomerEmail, "Payment confirmed", $"Payment {notification.Event.PaymentId} received.", ct);
        }
    }
}

/// <summary>
/// One open generic handler that runs for every notification (DotCourier 0.3.0+), e.g. an audit trail.
/// </summary>
public sealed class AuditTrail<TNotification>(ILogger<AuditTrail<TNotification>> logger) : INotificationHandler<TNotification>
    where TNotification : INotification
{
    public Task Handle(TNotification notification, CancellationToken ct)
    {
        // Type name only: notifications carry personal data (e-mails), which must not reach the logs.
        logger.LogInformation("📒 [audit] {Notification}", CourierDiagnostics.GetRequestName(typeof(TNotification)));
        return Task.CompletedTask;
    }
}
