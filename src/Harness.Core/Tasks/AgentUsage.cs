namespace Harness.Core.Tasks;

/// <summary>
/// Tokens an agent used, by kind: input not read from the cache, input read from the cache, input written to the
/// cache, output (reasoning included, as the APIs bill it) and the reasoning part of the output.
/// </summary>
public sealed record TokenCounts(
    long Input = 0,
    long CachedInput = 0,
    long CacheWrite = 0,
    long Output = 0,
    long Reasoning = 0
)
{
    public static TokenCounts Zero { get; } = new();

    public long Total => Input + CachedInput + CacheWrite + Output;

    public TokenCounts Add(TokenCounts other) =>
        new(
            Input + other.Input,
            CachedInput + other.CachedInput,
            CacheWrite + other.CacheWrite,
            Output + other.Output,
            Reasoning + other.Reasoning
        );
}

/// <summary>A model's API price in US dollars per million tokens of each kind.</summary>
public sealed record ModelPrice(double Input, double CachedInput, double CacheWrite, double Output)
{
    public double Cost(TokenCounts tokens) =>
        (
            tokens.Input * Input
            + tokens.CachedInput * CachedInput
            + tokens.CacheWrite * CacheWrite
            + tokens.Output * Output
        ) / 1_000_000d;
}

/// <summary>
/// Model prices by model id. A model matches its own entry, else the longest entry it starts with (any case), so
/// "claude-haiku-5-5-20261001" takes "claude-haiku-5-5"'s price.
/// </summary>
public sealed class PriceTable(IReadOnlyDictionary<string, ModelPrice> prices)
{
    public IReadOnlyDictionary<string, ModelPrice> Prices { get; } = prices;

    public ModelPrice? Find(string model)
    {
        ModelPrice? found = null;
        var length = -1;
        foreach (var (name, price) in Prices)
        {
            if (name.Length > length && model.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                (found, length) = (price, name.Length);
            }
        }
        return found;
    }
}
