using System.Globalization;
using System.Resources;
using DotCourier;

namespace Store.Api;

/// <summary>
/// DotCourier's own messages (generic 500, validation summary, timeout, resilience rejections) read
/// from Resources/Messages.resx and Messages.pt-BR.resx. <see cref="CourierMessage.From"/> evaluates
/// them on every error, so each request is answered in its Accept-Language.
/// </summary>
internal static class LocalizedMessages
{
    private static readonly ResourceManager Resources = new("Store.Api.Resources.Messages", typeof(LocalizedMessages).Assembly);

    public static void Apply(CourierMessages messages)
    {
        messages.UnexpectedError = Localized(nameof(CourierMessages.UnexpectedError), "000");
        messages.ValidationFailed = Localized(nameof(CourierMessages.ValidationFailed), "VAL-000");
        messages.HandlerNotFound = Localized(nameof(CourierMessages.HandlerNotFound), "NO-HANDLER");
        messages.RequestTimeout = Localized(nameof(CourierMessages.RequestTimeout), "TIMEOUT");
        messages.CircuitOpen = Localized(nameof(CourierMessages.CircuitOpen), "CIRCUIT-OPEN");
        messages.RateLimitExceeded = Localized(nameof(CourierMessages.RateLimitExceeded), "RATE-LIMITED");
        messages.BulkheadRejected = Localized(nameof(CourierMessages.BulkheadRejected), "BUSY");
    }

    private static CourierMessage Localized(string key, string code) =>
        CourierMessage.From(() => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key, code);
}
