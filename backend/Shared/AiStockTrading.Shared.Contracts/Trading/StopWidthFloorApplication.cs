namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-11, ADR-0049 決定2/決定3, #1120, IADR-0465 決定2: 損切り幅の下限の出所。
// 🔴 **0 を有効値にしない**（`Unspecified`）。旧記録・欠落した本文を読んだときに、黙って「退避の 2%」へ倒れない。
// 序数は固定する（監査の本文は名前で書くが、メッセージの直列化は序数になり得る）。
public enum StopWidthFloorSource
{
    /// <summary>未指定（欠落）。有効値ではない。</summary>
    Unspecified = 0,

    /// <summary>ATR が得られないときの退避値＝参照価格（アンカー後の現在値）の 2%（計画 05_trading-assumptions §5）。</summary>
    Fallback2Pct = 1,

    /// <summary>1.0 × ATR(14, 日足)。日足が判断へ通るまで（ADR-0048 決定 3 と同じ条件）は供給されない。</summary>
    Atr14 = 2,
}

/// <summary>
/// FR-10, FR-11, ADR-0049 決定3, #1120, IADR-0465 決定2: 新規建ての損切り幅に下限を掛けた結果（監査に残す）。
/// <para>
/// 値はすべて**1 株あたり・銘柄の市場の通貨（ローカル通貨）**で、<see cref="OrderIntent.Price"/> と同じ単位である。
/// 適用した幅 ＝ max(AI の幅, 下限)。<see cref="Widened"/> は AI の幅が下限を割ったときだけ true（ちょうど下限は false）。
/// </para>
/// <para>
/// 🔴 AI の幅が下限を割っても**見送らない**（ADR-0049 決定3）。広げた事実が読めなければ AI の提案の傾向を評価できないため、
/// 判断の記録（<c>TradeDecisionMade.StopWidth</c>）に載せて監査台帳（7 年保持）へ残す。
/// </para>
/// </summary>
public sealed record StopWidthFloorApplication(
    decimal AiWidthPerShare,
    decimal FloorPerShare,
    StopWidthFloorSource FloorSource,
    decimal AppliedWidthPerShare,
    bool Widened);
