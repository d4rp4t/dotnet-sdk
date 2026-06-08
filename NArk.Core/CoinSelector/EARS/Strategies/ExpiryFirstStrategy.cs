using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed class ExpiryFirstStrategy : ICoinSelectionStrategy
{
    public SelectionStrategy Strategy => SelectionStrategy.ExpiryFirst;

    public SelectionResult? TrySelect(
        IReadOnlyList<CoinCandidate> candidates,
        SelectionContext context,
        CoinSelectionPolicy policy)
    {
        var groups = candidates
            .GroupBy(c => c.ExpiryGroup)
            .OrderBy(g => g.Key);

        foreach (var group in groups)
        {
            var ordered = group.OrderByDescending(c => c.Value).ToList();
            var result = GreedyWithinGroup(ordered, context, group.Key);
            if (result is not null)
                return result;
        }

        if (!policy.AllowExpiryMixingFallback)
            return null;

        var all = candidates.OrderByDescending(c => c.Value).ToList();
        return GreedyWithinGroup(all, context, 0u, expiryMixed: true);
    }

    private static SelectionResult? GreedyWithinGroup(
        List<CoinCandidate> coins,
        SelectionContext context,
        uint expiryGroup,
        bool expiryMixed = false)
    {
        var selected = new List<ArkCoin>();
        var total = Money.Zero;

        foreach (var coin in coins)
        {
            if (selected.Count >= context.MaxInputs)
                break;

            selected.Add(coin.Coin);
            total += coin.Value;

            if (total >= context.TargetAmount)
                break;
        }

        if (total < context.TargetAmount)
            return null;

        var change = total - context.TargetAmount;

        if (change > Money.Zero && change < context.DustThreshold && !context.AllowSubDust)
            return null;

        return new SelectionResult(
            SelectedCoins: selected,
            TotalValue: total,
            Change: change,
            ExpiryGroup: expiryGroup,
            Strategy: SelectionStrategy.ExpiryFirst,
            Waste: change,
            IsValid: true,
            ExpiryMixedFallback: expiryMixed);
    }
}
