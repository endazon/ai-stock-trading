using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.QueryShortPermit;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 改訂）, #967, IADR-0425 決定1: 借株可否の照会結果（`GET /order-execution/short-permit` の応答）。
//
// 🔴 **「分からない」（Unknown）と「借りられない」（NotPermitted）を分ける**（Principle A）。受け手（リスク管理）は
// Unknown なら空売り文脈を組まず（照会できないなら空売りしない＝ADR-0016 決定3）、NotPermitted なら文脈を組んで
// 一次ゲートで拒否する（他の規則も評価して監査へ載せる）。どちらも拒否だが、監査に残る意味が違う。
//
// **`ShortFeeRate`（借株料）は運ばない。** 単位が確定していない（ADR-0026 PoC 項目 9）値を境界の外へ出すと、
// 受け手が年率と読んで写像する経路が生まれる（IADR-0158 決定3）。
//
// Symbol・Market は要求の写し（受け手が「別の銘柄の答えを読んでいない」ことを確かめるため）。
// ObservedAt はブローカーから答えを得た時刻（キャッシュから返すときも照会した時刻のまま）。Unknown のときは null。
public sealed record ShortPermitView(
    string Symbol,
    Market Market,
    ShortPermitStatus Status,
    string? UnknownReason,
    DateTimeOffset? ObservedAt);

// 🔴 **既定値（0）は Unknown である。** 項目の欠落・読み違いが「許可」へ倒れないようにする。
public enum ShortPermitStatus
{
    Unknown = 0,
    Permitted = 1,
    NotPermitted = 2,
}

// Unknown の理由（監査・ログで「なぜ分からないか」を読むための語彙。受け手は判定に使わない）。
public static class ShortPermitUnknownReasons
{
    /// <summary>空売りの対象市場ではない（ADR-0016 決定13。米国株のみ）。照会しない。</summary>
    public const string MarketNotSupported = "market-not-supported";

    /// <summary>発注先が照会を持たない（内蔵 paper）。</summary>
    public const string BrokerNotSupported = "broker-not-supported";

    /// <summary>照会の予算（30 秒あたりの回数）を使い切った。照会しない。</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>照会が失敗した（不達・非成功の応答・打ち切り。SIMULATE 口座では実測どおりならこれが常態）。</summary>
    public const string QueryFailed = "query-failed";

    /// <summary>応答に当該銘柄の行、または借株可否の欄が無かった。</summary>
    public const string FieldMissing = "field-missing";
}
