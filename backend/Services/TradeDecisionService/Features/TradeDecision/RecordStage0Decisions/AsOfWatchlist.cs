namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-15, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442: Stage 0 の記録が使う**当時の監視銘柄**の供給。
//
// 🔴 「再構成できた（一覧）」と「再構成できない（理由）」を**別の値**で持つ（原則 A）。できないことを空の一覧で表すと、
// プロンプトは「当時 0 件だった・この銘柄は対象外」と事実でないことを書き、その記録が合格根拠に入り得る。

/// <summary>当時の監視銘柄の照会結果。<see cref="Reconstructed"/> か <see cref="NotReconstructable"/> でしか作れない。</summary>
public sealed record AsOfWatchlist
{
    private AsOfWatchlist(IReadOnlyList<WatchedSymbol>? symbols, string? reason)
    {
        Symbols = symbols;
        Reason = reason;
    }

    /// <summary>再構成できた一覧（0 件もあり得る）。できないときは null。</summary>
    public IReadOnlyList<WatchedSymbol>? Symbols { get; }

    /// <summary>再構成できないときの理由。できたときは null。</summary>
    public string? Reason { get; }

    public static AsOfWatchlist Reconstructed(IReadOnlyList<WatchedSymbol> symbols) =>
        new(symbols ?? throw new ArgumentNullException(nameof(symbols)), null);

    public static AsOfWatchlist NotReconstructable(string reason) =>
        new(null, string.IsNullOrWhiteSpace(reason) ? "理由不明" : reason);
}

/// <summary>FR-04, FR-15, #1049, IADR-0442 決定 3: 指定した時刻に有効だった監視銘柄の供給ポート。</summary>
public interface IAsOfWatchlistSource
{
    Task<AsOfWatchlist> GetWatchlistAtAsync(DateTimeOffset at, CancellationToken cancellationToken = default);
}

/// <summary>
/// 市場監視へ結線していない構成（<c>MarketMonitor:BaseUrl</c> が空・不正）の供給。常に「再構成できない」
/// （その記録は合否から外れる。構成の固定リストや記録の対象銘柄では代えない）。
/// </summary>
public sealed class UnwiredAsOfWatchlistSource : IAsOfWatchlistSource
{
    public const string Reason = "市場監視へ結線されていません（MarketMonitor:BaseUrl が未設定）。";

    public Task<AsOfWatchlist> GetWatchlistAtAsync(DateTimeOffset at, CancellationToken cancellationToken = default) =>
        Task.FromResult(AsOfWatchlist.NotReconstructable(Reason));
}

/// <summary>
/// FR-15, ADR-0033 決定 2, #1049, IADR-0442 決定 3: 記録の判断時点（<c>DateOnly AsOf</c>）を照会の時刻へ換える。
/// <para>
/// <b>AsOf の UTC の日の終わり（<c>AsOf 23:59:59.9999999Z</c>。境界は含む）</b>。as-of の参考情報の切り方（発行時刻の UTC の日付 ≤ AsOf。
/// <see cref="AsOfDecisionInput"/>）と揃える。翌日 00:00:00Z ちょうどの変更は含まない。
/// </para>
/// </summary>
public static class AsOfWatchlistInstant
{
    public static DateTimeOffset EndOfUtcDay(DateOnly asOf) =>
        new(asOf.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc), TimeSpan.Zero);
}
