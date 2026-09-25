namespace NotificationService.Features.Notifications;

// FR-13, FR-14, ADR-0042 決定 1・2, #1025, IADR-0433: 市場監視サービスの監視銘柄の照会と、`/policy` の入れ替え案の一括適用。
//
// 🔴 **FR-14 の例外はこの口だけである（ADR-0042 決定 2）。** 口は 2 つに限る——現在の監視銘柄の照会（案の土台と楽観排他の基準）と、
// 利用者が確認ボタンで確定した**案**の適用。銘柄を自由に追加・削除する口は持たない（`DiscordSettingsAreReadOnlyTests` が固定）。
// 適用に渡す入れ替えは報告書サービスの台帳に記録された案だけであり（`PolicyApprovalCommandHandler`）、Discord から打ち込まれた値ではない。
public interface IMarketMonitorWatchlistController
{
    /// <summary>現在の監視銘柄。失敗は Succeeded=false（「空」と区別する）。</summary>
    Task<WatchlistSnapshotResult> GetWatchlistAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 入れ替え案を適用する（案を作った時点の監視銘柄 <paramref name="expected"/> と現在が違えば 1 件も適用されない）。
    /// <paramref name="onBehalfOf"/> は多層認証が解決した利用者（変更履歴に本人として残る）。
    /// </summary>
    Task<WatchlistApplyOutcome> ApplyProposalAsync(
        IReadOnlyList<WatchlistSnapshotItemView> expected,
        IReadOnlyList<WatchlistChangeSuggestionView> changes,
        string proposalRef,
        string onBehalfOf,
        CancellationToken cancellationToken = default);
}

// 監視銘柄の 1 件（Market は "UnitedStates" / "Japan"）。
public sealed record WatchlistSnapshotItemView(string Symbol, string Market);

public sealed record WatchlistSnapshotResult(bool Succeeded, IReadOnlyList<WatchlistSnapshotItemView> Items, string Message);

public enum WatchlistApplyStatus
{
    /// <summary>適用を試みた（内訳に適用・適用せずが入る。0 件の適用もあり得る）。</summary>
    Applied,

    /// <summary>案を作った後に監視銘柄が変わったため 1 件も適用しなかった（409）。</summary>
    Stale,

    /// <summary>受理されなかった（400・403 等）。1 件も適用していない。</summary>
    Rejected,

    /// <summary>🔴 結果が分からない（タイムアウト・伝送の例外・解釈できない 2xx）。適用されたかもしれない。</summary>
    Indeterminate,
}

public sealed record WatchlistApplyItemView(string Action, string Symbol, bool Applied, string? SkipReason);

public sealed record FinnhubEstimateView(long EstimatedDailyRequests, int ProvisionalDailyLimit, bool Exceeds);

public sealed record WatchlistApplyOutcome(
    WatchlistApplyStatus Status,
    IReadOnlyList<WatchlistApplyItemView> Items,
    FinnhubEstimateView? Estimate,
    string Message);
