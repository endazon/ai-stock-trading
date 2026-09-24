using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-05, FR-10, #292, IADR-0119: 判断対象の銘柄について現在の保有建玉を照会するポート。
// 実体はリスク管理（#12・#63 台帳）の GET /risk-controls/open-positions（既存・OwnerOrService）。
// 安全既定は NoOpHeldPositionProvider（常に不明）。
public interface IHeldPositionProvider
{
    /// <summary>
    /// FR-04, FR-10, ADR-0003, #865, IADR-0358: 保有状況の照会先が**実結線**されているか
    /// （<c>RiskManagement:BaseUrl</c> が設定され <c>HttpHeldPositionProvider</c> が配線されている）。
    ///
    /// 🔴 これは「不明」の意味を分けるためだけに在る。未結線（NoOp＝常に不明）は「照会していない」であり、
    /// 実結線の不明は「照会したが答えが得られなかった」である。後者のときだけ新規建て（Open）を見送る
    /// （<c>PositionEffectResolver</c> の <c>requireKnownHoldingForOpen</c>）。
    /// <see cref="ICurrentPriceProvider.IsEnabled"/> と同じ形（IADR-0099 決定3）。
    /// </summary>
    bool IsEnabled { get; }

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

    /// <summary>
    /// FR-04, FR-10, ADR-0003, #934, IADR-0390 決定2: (銘柄, 市場) の<b>当日の未約定の新規建て注文</b>
    /// （承認済み・終端イベント未着・残数量 &gt; 0）。判断の入力であり、<see cref="GetPositionAsync"/>（約定済みの保有）とは
    /// <b>別の第 3 の状態</b>である —— 数量・平均取得単価・含み損益へ混ぜない。
    ///
    /// 🔴 照会できない場合は <b>null（＝不明）</b>を返す。空（<see cref="WorkingEntryOrders.None"/>）＝「未約定の注文は無い」と
    /// 厳格に区別すること。不明を空へ倒すと、指値が板に残っているのに判断は「保有なし」を前提に同じ銘柄を重ねて買う
    /// （#934 の実測そのもの）。
    /// </summary>
    Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
        string symbol, Market market, CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-04, FR-10, #934, IADR-0390 決定3: 判断対象の銘柄の<b>未約定の新規建て注文</b>の一覧（リスク管理の
/// 未約定ビュー〔IADR-0346 と同じ定義〕の射影）。約定済みの保有（<see cref="HeldPosition"/>）とは別の型で運ぶ。
/// 🔴 「ブローカーが受理した」ことは表さない（承認済みで終端イベントが届いていない注文。受理済み・発注処理中・結果未着を含む）。
/// </summary>
public sealed record WorkingEntryOrders(IReadOnlyList<WorkingEntryOrder> Orders)
{
    /// <summary>未約定の新規建て注文は無い（照会は成功し、該当が無い）。不明（null）とは別の状態である。</summary>
    public static WorkingEntryOrders None { get; } = new([]);

    public bool Any => Orders.Count > 0;
}

/// <summary>
/// FR-04, FR-10, #934, IADR-0390: 未約定の新規建て注文 1 件。<paramref name="RemainingQuantity"/> は残数量
/// （承認数量 − 約定済み。約定済みの分は <see cref="HeldPosition"/> 側に入っている）。価格は承認価格（ローカル通貨）。
/// </summary>
public sealed record WorkingEntryOrder(TradeSide Side, int RemainingQuantity, decimal Price, DateTimeOffset ApprovedAt);

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
