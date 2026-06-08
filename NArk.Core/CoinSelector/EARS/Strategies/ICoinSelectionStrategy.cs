namespace NArk.Core.CoinSelector.EARCoinSelector;

public interface ICoinSelectionStrategy
{
    SelectionStrategy Strategy { get; }
    SelectionResult? TrySelect(
        IReadOnlyList<CoinCandidate> candidates,
        SelectionContext context,
        CoinSelectionPolicy policy);
}