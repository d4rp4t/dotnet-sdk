using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed class CoinSelectionEngine
{
    private readonly IReadOnlyList<ICoinSelectionStrategy> _strategies;

    public CoinSelectionEngine(IReadOnlyList<ICoinSelectionStrategy> strategies)
    {
        _strategies = strategies;
    }

    public SelectionResult Select(
        IReadOnlyList<CoinCandidate> candidates,
        SelectionContext context,
        CoinSelectionPolicy policy)
    {
        var buckets = BuildBuckets(candidates, policy.ExpiryWindowBlocks);
        var valid = new List<SelectionResult>();

        foreach (var strategy in _strategies)
        {
            var result = strategy.TrySelect(buckets, context, policy);
            if (result is not null && result.IsValid)
                valid.Add(result);
        }

        if (valid.Count == 0)
            throw new NotEnoughFundsException("No valid selection", null, context.TargetAmount);

        return valid
            .OrderBy(r => r.Waste)
            .ThenBy(r => r.SelectedCoins.Count)
            .First();
    }

    // Groups candidates into buckets where all coins within a bucket expire within
    // ExpiryWindowBlocks of the earliest coin in that bucket (~24h by default).
    // Coins with no expiry height (ExpiryGroup == 0) form their own bucket.
    internal static IReadOnlyList<ExpiryBucket> BuildBuckets(
        IReadOnlyList<CoinCandidate> candidates,
        uint windowBlocks)
    {
        var sorted = candidates.OrderBy(c => c.ExpiryGroup).ToList();
        var buckets = new List<ExpiryBucket>();
        var current = new List<CoinCandidate>();
        var windowStart = 0u;

        foreach (var coin in sorted)
        {
            if (current.Count == 0)
            {
                current.Add(coin);
                windowStart = coin.ExpiryGroup;
            }
            else if (windowStart == 0u || coin.ExpiryGroup - windowStart <= windowBlocks)
            {
                // Same window: include (or no-expiry coins all go together)
                current.Add(coin);
            }
            else
            {
                buckets.Add(MakeBucket(current));
                current = [coin];
                windowStart = coin.ExpiryGroup;
            }
        }

        if (current.Count > 0)
            buckets.Add(MakeBucket(current));

        return buckets;
    }

    private static ExpiryBucket MakeBucket(List<CoinCandidate> coins) =>
        new(ExpiryGroup: coins.Min(c => c.ExpiryGroup),
            Coins: coins.OrderByDescending(c => c.Value).ToList(),
            TotalValue: coins.Sum(c => c.Value));
}
