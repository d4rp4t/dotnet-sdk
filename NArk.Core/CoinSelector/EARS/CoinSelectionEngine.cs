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
        var valid = new List<SelectionResult>();

        foreach (var strategy in _strategies)
        {
            var result = strategy.TrySelect(candidates, context, policy);
            if (result is not null && result.IsValid)
                valid.Add(result);
        }

        if (valid.Count == 0)
            throw new NotEnoughFundsException("No valid selection", null, context.TargetAmount);

        return valid.OrderBy(r => r.Waste)
            .ThenBy(r => r.SelectedCoins.Count)
            .First();
    }
}