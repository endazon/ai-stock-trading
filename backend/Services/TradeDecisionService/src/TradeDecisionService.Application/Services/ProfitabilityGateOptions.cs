namespace AiStockTrading.TradeDecision.Application.Services;

// FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価ゲートの構成。
// 既定は無効（Enabled=false）＝現行の判断挙動を一切変えない（段階的有効化・opt-in）。有効化は Configuration:BaseUrl
// （実前提条件）の配線と対で意味を持つ（未配線なら費用未解決で Hold＝空振りせず安全）。
public sealed record ProfitabilityGateOptions
{
    private readonly decimal _decisionCostJpy;

    // 採算評価を行うか。既定 false（現行挙動）。true のとき採算不成立・費用見積り不能は Hold に倒す。
    public bool Enabled { get; init; }

    // この取引の判断費用の固定見積り（円・任意項）。往復取引費用に加算してしきい値を上げる。
    // 既定 0（中立）。LLM 月次費用計測（IADR-0055）とは別目的の per-trade 見積りで二重計上ではない（決定6）。負値は 0 に正規化。
    public decimal DecisionCostJpy
    {
        get => _decisionCostJpy;
        init => _decisionCostJpy = value > 0m ? value : 0m;
    }

    public static ProfitabilityGateOptions Default { get; } = new();
}
