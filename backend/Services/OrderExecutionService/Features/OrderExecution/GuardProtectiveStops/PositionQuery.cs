using AiStockTrading.Shared.Contracts.Ports;
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

    /// <summary>
    /// FR-10, NFR, #1164, IADR-0487 決定2: 照会し直しをした回数（<see cref="PositionQueryRetry"/> だけが載せる。既定 0）。
    /// </summary>
    public int Retries { get; init; }

    /// <summary>FR-10, NFR, #1164, IADR-0487 決定2: 照会し直しをしたときの、最初の照会の失敗の種類（していなければ None）。</summary>
    public PositionQueryFailure FirstFailure { get; init; }

    /// <summary>
    /// 🔴 NFR, FR-10, #1164, IADR-0487 決定1・2: 照会の状態の台帳（<c>PositionQueryStatusChanged.FailureKind</c>）へ載せる種類。
    /// <b>どの経路もこれを報告する</b>（書き方を経路ごとに持たない）。
    /// <list type="bullet">
    /// <item>成功・分類の無い失敗（分類つきの口を持たない供給元）→ null（台帳では「不明」）。</item>
    /// <item>照会し直していない失敗 → 1 語（<c>Transient</c> / <c>RateLimited</c> / <c>Other</c>）。</item>
    /// <item>照会し直した失敗 → <c>最初→最後</c>（例 <c>Transient→Other</c>）。最終結果だけを載せると、照会し直しが効いたかを
    /// 台帳から読めない（#1164 の取り違え）。最初の種類だけを載せると、据え置きの原因（最後の失敗）が消える。</item>
    /// </list>
    /// </summary>
    public string? ReportedFailureKind =>
        Positions is not null || Failure == PositionQueryFailure.None
            ? null
            : Retries > 0
                ? $"{FirstFailure}→{Failure}"
                : Failure.ToString();
}

/// <summary>
/// 🔴 FR-10, NFR, #1164, IADR-0487 決定1: 建玉照会の<b>共有の入口</b>。台帳へ報告する建玉照会は、すべてここを通して
/// 失敗の種類を受け取る（分類器は moomoo のアダプタの 1 か所だけ。経路ごとに分類しない・捨てない）。
/// </summary>
public static class PositionQueries
{
    /// <summary>
    /// 分類つきの口（<see cref="IClassifiedPositionSource"/>）を持つ供給元はそれで照会する。持たない供給元は
    /// <see cref="IBrokerPositionSource.GetPositionsAsync"/> を呼び、失敗の種類は分からないもの（None＝台帳では不明）とする。
    /// 例外は呼び出し側へそのまま伝わる（扱いは呼び出し側の従来の規律に従う）。
    /// </summary>
    public static async Task<PositionQueryResult> QueryAsync(
        IBrokerPositionSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source is IClassifiedPositionSource classified
            ? await classified.QueryPositionsAsync(cancellationToken).ConfigureAwait(false)
            : new PositionQueryResult(
                await source.GetPositionsAsync(cancellationToken).ConfigureAwait(false), PositionQueryFailure.None);
    }
}

/// <summary>
/// FR-10, #1093, IADR-0458: 失敗の種類つきで建玉を照会できる口（moomoo のアダプタが実装する）。
/// 🔴 **照会し直しはガードの建玉照会（巡回に 1 回）だけが持つ。** 発注の経路（決済ゲート・S1 の武装と決済）も
/// #1164 / IADR-0487 決定1 から <see cref="PositionQueries.QueryAsync"/> 経由でこの口を呼ぶが、種類を受け取るだけで
/// 照会は 1 回のまま（承認から発注までの遅延を増やさず、発注の経路に再試行を持ち込まない。IADR-0211 決定 3）。
/// </summary>
public interface IClassifiedPositionSource
{
    Task<PositionQueryResult> QueryPositionsAsync(CancellationToken cancellationToken = default);
}
