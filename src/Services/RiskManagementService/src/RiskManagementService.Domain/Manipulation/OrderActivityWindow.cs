using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain.Manipulation;

// FR-19, IADR-0040: ある（銘柄, 市場）の直近窓の注文アクティビティ。相場操縦検知アルゴリズムの入力単位。
public sealed record OrderActivityWindow
{
    /// <summary>対象銘柄コード。</summary>
    public required string Symbol { get; init; }

    /// <summary>対象市場。</summary>
    public required Market Market { get; init; }

    /// <summary>窓の基準時刻（判定時刻）。窓は [AsOf - lookback, AsOf] を表す。</summary>
    public required DateTimeOffset AsOf { get; init; }

    /// <summary>窓内の注文ライフサイクル要約群。</summary>
    public required IReadOnlyList<OrderActivityRecord> Records { get; init; }

    /// <summary>空窓（記録なし）。最小標本ガードにより無嫌疑となる。</summary>
    public static OrderActivityWindow Empty(string symbol, Market market, DateTimeOffset asOf) =>
        new()
        {
            Symbol = symbol,
            Market = market,
            AsOf = asOf,
            Records = [],
        };
}
