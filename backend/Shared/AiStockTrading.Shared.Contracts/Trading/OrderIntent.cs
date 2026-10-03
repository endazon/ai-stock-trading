namespace AiStockTrading.Shared.Contracts.Trading;

// FR-04, FR-05: 取引判断サービスが生成する注文意図。リスク管理の検証を経て発注執行へ渡る。
// Price は**銘柄の市場の通貨（ローカル通貨）**の参照価格で、発注執行がブローカーへ送る注文価格の権威である
// （IADR-0107 決定1。moomoo アダプタは本値をそのまま注文価格に用いるため、基準通貨への換算値を入れてはならない）。
// PositionEffect（建玉効果）はエントリー/手仕舞いを表し、既定は Open（新規建て）。エントリー専用の
// リスク統制の適用可否はこの値で判定する（IADR-0004）。既定を Open とすることで、効果未指定の注文は
// 制約を厳しく掛ける安全側に倒れる。
// FR-03/04/10, IADR-0035: StopLossPrice は取引判断が決めた損切り価格（Price と同じローカル通貨）。#63 台帳へ
// （#1120, ADR-0049, IADR-0465: 参照価格 ∓ 下限を掛けた 1 株あたり幅＝max(LLM の幅, 下限)。下限は ATR が得られない間は参照価格の 2%）
// 永続化され、市場監視の損切りライン検知（IADR-0030）に実値として供給される。後方互換のため nullable
//（既定 null＝機械執行の Close 等では該当なし）。
// FR-10, FR-17, #257, #364, IADR-0107/0152: FxRateToBase は「ローカル通貨 1 単位あたりの基準通貨（USD）額」。
// 取引判断が意図生成時（＝約定時レートの近似・計画 05_trading-assumptions §3）に確定して同伴させ、統制・台帳は
// NotionalInBase で基準通貨の金額を判定する。既定 1＝基準通貨市場（米国株）。
// **既定 1 の意味は基準通貨の反転で変わる**（旧: 日本株）。既存の永続データが本移行を跨いで混在しないことは
// EF マイグレーション AssertLedgerSafeForUsdBaseCurrency が構造的に検査する（IADR-0152 決定5）。
// FR-10, UC-06, #847, IADR-0357: MarketOrder は「この注文を成行で送る」ことを表す（既定 false＝従来どおり指値）。
// 利用者の手仕舞い（Close）だけが true を立てる —— 現在値の指値は下落局面で置いていかれ、**手仕舞いが必要な
// 場面でこそ効かない**（稼働環境で実測。#847）。成行でも Price は**参照価格**として載せる（台帳・監査・通知・
// 内蔵 paper の約定価格が使う）。実ブローカー（moomoo）は成行注文に価格を載せないため、送信内容には影響しない。
// 既定 false により、エントリー・保護レグ・判断由来の決済の挙動は 1 バイトも変わらない。
// 取引台帳（approved_orders）は OrderIntent の列を明示写像しており本値を持たない（発注時にしか意味を持たない）。
// 🔴 FR-10, ADR-0049 決定1・決定2, #1122, IADR-0486 決定5: StopFloorSource は「この新規建ての損切り幅（StopLossPrice）を、どの出所の
// 下限を掛けてから引いたか」の印（Fallback2Pct / Atr14）。取引判断が新規建てにだけ立てる。**null＝下限を掛けたかが分からない**
// （#1122 より前の判断・決済・保護レグ・利用者の手仕舞い等）。発注執行は発注結果の記録と予約の行に残し、既存の S1 への下限の遡及
// （IADR-0472）が「サイジングの時点で下限を掛けて建てた行」を広げないために読む（ATR の下限は参照価格の 2% より狭いことがある）。
// 発注には使わない（ブローカーへ送る内容は変わらない）。取引台帳は本値を持たない（明示写像）。
public record OrderIntent(
    string Symbol,
    Market Market,
    TradeSide Side,
    ProductType ProductType,
    BrokerProvider Mode,
    int Quantity,
    decimal Price,
    PositionEffect PositionEffect = PositionEffect.Open,
    decimal? StopLossPrice = null,
    decimal FxRateToBase = 1m,
    bool MarketOrder = false,
    StopWidthFloorSource? StopFloorSource = null)
{
    /// <summary>ローカル通貨建ての概算約定金額（執行・スリッページ評価用）。</summary>
    public decimal Notional => Quantity * Price;

    /// <summary>基準通貨（USD）建ての概算約定金額。リスク統制の金額判定はこちらを用いる（IADR-0107 / IADR-0152）。</summary>
    public decimal NotionalInBase => Quantity * Price * FxRateToBase;
}
