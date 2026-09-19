using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-05, FR-10, #292, IADR-0119: 判断対象の銘柄について現在の保有建玉を照会するポート。
// 実体はリスク管理（#12・#63 台帳）の GET /risk-controls/open-positions（既存・OwnerOrService）。
// 安全既定は NoOpHeldPositionProvider（常に不明）。
public interface IHeldPositionProvider
{
    /// <summary>
    /// (銘柄, 市場) の**符号付き**建玉数量（+ ロング / − ショート / 0 保有なし）。
    ///
    /// 照会できない場合は **null（＝不明）** を返す。0（保有なし）と厳格に区別すること。
    /// 失敗を 0 へ倒すと「保有していない」と誤断定し、裸の新規売りを通してしまう。
    /// </summary>
    Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken cancellationToken = default);

    /// <summary>
    /// FR-04, FR-10, ADR-0003, #854, IADR-0351 決定1: (銘柄, 市場) の保有状況（数量・平均取得単価・記録上の損切りライン）。
    /// 判断プロンプトへ載せる入力であり、計画 ADR-0003 が判断入力に定める「保有ポジション」の供給口である。
    ///
    /// 区別は <see cref="GetSignedQuantityAsync"/> と同じ: 保有なしは <see cref="HeldPosition.None"/>、
    /// 照会できない場合は **null（＝不明）**。不明を「保有なし」へ倒すと、LLM は保有を知らないまま
    /// 毎サイクルを新規買いの是非として判断する（#854 の実測そのもの）。
    /// </summary>
    Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-04, FR-10, #854, IADR-0351 決定1: 判断対象の銘柄の保有状況（リスク管理の取引台帳の射影）。
/// 価格はいずれも銘柄の市場の通貨（ローカル通貨）。
/// </summary>
/// <param name="SignedQuantity">符号付き数量（+ ロング / − ショート / 0 保有なし）。</param>
/// <param name="AverageEntryPrice">平均取得単価。応答に無い・正でない場合は null（＝不明。0 で埋めない）。</param>
/// <param name="StopLossPrice">
/// 記録上の損切りライン（判断が建てた時点で決めた権威データ＝IADR-0035。欠損建玉は既定比率の近似＝IADR-0030）。
/// 応答に無い・正でない場合は null（＝不明）。
/// </param>
public sealed record HeldPosition(int SignedQuantity, decimal? AverageEntryPrice, decimal? StopLossPrice)
{
    /// <summary>保有なし（照会は成功し、一覧に該当の建玉が無い）。不明（null）とは別の状態である。</summary>
    public static HeldPosition None { get; } = new(0, null, null);

    public bool IsHeld => SignedQuantity != 0;

    public bool IsLong => SignedQuantity > 0;
}
