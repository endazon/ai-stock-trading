using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.QueryDailyBars;

// FR-04, FR-15, ADR-0048 決定 2, #1118, IADR-0467 決定 2: 日足の照会結果（`GET /order-execution/daily-bars` の応答）。
//
// 🔴 **既定値（0）は Unavailable である。** 項目の欠落・読み違いが「取れた」へ倒れないようにする（借株可否の View と同じ規律）。
// 🔴 **足は前復権（分割・配当で過去を調整した値）である。** 出来高も分割で調整される（#1117 の実測）。1 回の応答の中の足は同じ基準で揃う。
// 🔴 **当日の未確定足を捨てるのは受け手（判断）の責務である** —— 送り手は要求された期間をそのまま返す（期間の終わりに当日を含めない
// のは受け手が決める。OpenD は当日を含む期間に未確定の当日足を返す＝#1117 の実測）。
//
// Symbol・Market は要求の写し（受け手が別の銘柄の答えを読んでいないことを確かめる）。From・To は要求した期間（両端を含む）。
// Bars は日付の昇順。空白足（取引の無い日）・欄の欠けた足は含まない。
public sealed record DailyBarsView(
    string Symbol,
    Market Market,
    DailyBarsStatus Status,
    string? UnavailableReason,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<DailyBarView> Bars);

// 日足 1 本（前復権の OHLCV。値は OpenD の応答の double を decimal へ写したもの）。
public sealed record DailyBarView(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

// 🔴 **既定値（0）は Unavailable である。**
public enum DailyBarsStatus
{
    Unavailable = 0,
    Available = 1,
}

// Unavailable の理由（監査・ログで「なぜ取れないか」を読むための語彙。受け手は判定に使わない）。
public static class DailyBarsUnavailableReasons
{
    /// <summary>米国株ではない（日足 K 線の履歴源は米国株だけ。ADR-0023 決定 5）。照会しない。</summary>
    public const string MarketNotSupported = "market-not-supported";

    /// <summary>発注先が OpenD を持たない（内蔵 paper）。照会しない。</summary>
    public const string BrokerNotSupported = "broker-not-supported";

    /// <summary>自制の予算（60 秒あたりの回数）を使い切った。照会しない。</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>照会が失敗した（接続失敗・切断・打ち切り・非成功の応答・続きの鍵つきの不完全な応答）。</summary>
    public const string QueryFailed = "query-failed";
}
