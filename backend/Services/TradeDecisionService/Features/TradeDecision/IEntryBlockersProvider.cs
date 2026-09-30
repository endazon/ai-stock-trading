using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// 🔴 FR-10, FR-04, ADR-0003, #1113, IADR-0463 決定 4: 銘柄単位の「新規建ての可否」を照会するポート。
// 実体はリスク管理の GET /risk-controls/entry-blockers（gRPC `GetEntryBlockers`）で、**審査と同じ述語**で状態から確定する
// 拒否理由を方向別に返す。判断側は**結果を読むだけ**で、規則（損切り・建玉数・kill switch 等）を持たない。
// 安全既定は NoOpEntryBlockersProvider（常に不明）。不明なら判断は LLM を呼ぶ（見送らない。審査が止める）。
public interface IEntryBlockersProvider
{
    /// <summary>
    /// (銘柄, 市場) の新規建ての可否。照会できない・応答を解釈できない場合は <b>null（＝不明）</b>。
    /// 🔴 空の一覧は「確定する拒否は無い」であり、不明（null）と取り違えない。
    /// </summary>
    Task<EntryBlockers?> GetAsync(string symbol, Market market, CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-10, #1113, IADR-0463 決定 3: 新規建ての方向ごとの、状態から確定する拒否理由（リスク管理の審査と同じ述語の結果）。
/// <see cref="LongSide"/> は買いの新規建て（ロングを建てる）、<see cref="ShortSide"/> は売りの新規建て（ショートを建てる）。
/// </summary>
public sealed record EntryBlockers(IReadOnlyList<RejectionReason> LongSide, IReadOnlyList<RejectionReason> ShortSide)
{
    /// <summary>確定する拒否は無い（照会は成功し、両方向とも空）。</summary>
    public static EntryBlockers None { get; } = new([], []);

    /// <summary>新規建て（<paramref name="entrySide"/>）の方向の理由。買い → ロング、売り → ショート。</summary>
    public IReadOnlyList<RejectionReason> ForEntry(TradeSide entrySide) =>
        entrySide == TradeSide.Buy ? LongSide : ShortSide;
}
