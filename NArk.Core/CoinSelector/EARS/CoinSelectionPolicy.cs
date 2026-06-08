namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed record CoinSelectionPolicy(
    bool ExpiryFirst = true,
    bool PreferNoChange = true,
    bool PreferLowFee = true,
    bool AllowExpiryMixingFallback = true,
    bool AllowRandomization = true,
    int RandomTopK = 3,
    int MaxBnBInputs = 12,
    int MaxLocalSearchIterations = 50);