namespace Dafa.Contracts.Core.Domain;

/// <summary>
/// Static placeholder FX rates to DKK, used for value bands and management totals.
/// Replace with a rate source (ERP / data warehouse) before production use.
/// </summary>
public static class CurrencyRates
{
    private static readonly Dictionary<string, decimal> ToDkkRates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DKK"] = 1.00m,
        ["EUR"] = 7.46m,
        ["USD"] = 6.90m,
        ["SEK"] = 0.65m,
        ["NOK"] = 0.64m,
        ["GBP"] = 8.70m,
        ["PLN"] = 1.73m,
        ["CNY"] = 0.95m
    };

    public static readonly string[] Currencies = { "DKK", "EUR", "USD", "SEK", "NOK", "GBP", "PLN", "CNY" };

    public static bool IsKnown(string? currency) => currency != null && ToDkkRates.ContainsKey(currency);

    public static decimal ToDkk(decimal? amount, string? currency)
    {
        if (amount is null)
        {
            return 0m;
        }
        if (currency != null && ToDkkRates.TryGetValue(currency, out var rate))
        {
            return decimal.Round(amount.Value * rate, 2);
        }
        return amount.Value;
    }
}
