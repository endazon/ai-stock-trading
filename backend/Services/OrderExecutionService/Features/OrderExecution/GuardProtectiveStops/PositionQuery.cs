using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

// FR-10, #1093, IADR-0458: 建玉照会の失敗の種類。**ガードの巡回の中で 1 回だけ照会し直してよいか**を決めるためだけに使う。
// 共有ポート（IBrokerPositionSource）の契約「照会不能は null」は変えない —— 分類はこのサービスの中に閉じる。
public enum PositionQueryFailure
{
    /// <summary>成功（Positions が null でない）。</summary>
    None = 0,

    /// <summary>一時的な失敗（返信待ちの打ち切り・接続の確立の失敗・応答待ち中の切断・結果不明）。少し待てば通り得る。</summary>
    Transient = 1,

    /// <summary>OpenD の頻度制限（失敗も枠を消費するので、待ちを長く取る）。</summary>
    RateLimited = 2,

    /// <summary>それ以外（業務上の失敗・応答の読み損ね・分類できない例外）。照会し直さない。</summary>
    Other = 3,
}

/// <summary>FR-10, #1093, IADR-0458: 分類つきの建玉照会の結果。<c>Positions</c> が null なら照会不能（据え置きの根拠）。</summary>
public sealed record PositionQueryResult(IReadOnlyList<BrokerPositionSnapshot>? Positions, PositionQueryFailure Failure)
{
    public static PositionQueryResult Success(IReadOnlyList<BrokerPositionSnapshot> positions) =>
        new(positions, PositionQueryFailure.None);

    public static PositionQueryResult Failed(PositionQueryFailure failure) => new(null, failure);
}

/// <summary>
/// FR-10, #1093, IADR-0458: 失敗の種類つきで建玉を照会できる口（moomoo のアダプタが実装する）。
/// 🔴 **ガードの建玉照会（巡回に 1 回）だけが使う。** 発注の経路（決済ゲート・S1 の武装と決済）は使わない
/// （承認から発注までの遅延を増やさず、発注の経路に再試行を持ち込まない。IADR-0211 決定 3）。
/// </summary>
public interface IClassifiedPositionSource
{
    Task<PositionQueryResult> QueryPositionsAsync(CancellationToken cancellationToken = default);
}
