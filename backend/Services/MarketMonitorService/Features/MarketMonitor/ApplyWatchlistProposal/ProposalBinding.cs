using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Trading;
using MarketMonitorService.Domain;

namespace MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal;

/// <summary>NFR-06, IADR-0634 決定 3: 結び付きの確かめの段（OwnerSecondFactorGate と同じ 3 つ）。既定は Off（何もしない）。</summary>
public enum WatchlistProposalBindingMode
{
    Off,
    Audit,
    Enforce,
}

/// <summary>NFR-06, IADR-0634: 構成 <c>Watchlist:ProposalBinding</c>。</summary>
public sealed class WatchlistProposalBindingOptions
{
    public const string SectionName = "Watchlist:ProposalBinding";

    public WatchlistProposalBindingMode Mode { get; set; } = WatchlistProposalBindingMode.Off;
}

/// <summary>NFR-06, IADR-0634 決定 3: 結び付きの結果。</summary>
public enum WatchlistProposalBindingOutcome
{
    /// <summary>段が Off（確かめない）。</summary>
    Skipped,

    /// <summary>第 2 要素つきで確定済み・未適用の案と、要求が一致した。</summary>
    Bound,

    /// <summary>案の出所（proposalRef）が「会話キー-v版」の形でない。</summary>
    MalformedReference,

    /// <summary>その版の案が無い。</summary>
    NotProposal,

    /// <summary>その版で確定されていない。</summary>
    NotConfirmed,

    /// <summary>報告書の第 2 要素が段 2 でない（確定が第 2 要素を通ったと言えない）。</summary>
    SecondFactorNotEnforced,

    /// <summary>既に適用の記録がある。</summary>
    AlreadyApplied,

    /// <summary>案を作った時点の監視銘柄が台帳に無い。</summary>
    SnapshotUnknown,

    /// <summary>要求の入れ替え・期待値が台帳の案と違う。</summary>
    Mismatch,

    /// <summary>台帳へ届かない・読めない（Enforce は拒む）。</summary>
    Unavailable,
}

/// <summary>判定。<see cref="Allowed"/> が false なら適用しない。</summary>
public sealed record WatchlistProposalBindingDecision(WatchlistProposalBindingOutcome Outcome, bool Allowed);

/// <summary>NFR-06, IADR-0634: 台帳（報告書サービス）の照会の結果。</summary>
public enum WatchlistProposalLookupStatus
{
    Found,
    Invalid,
    NotProposal,
    NotConfirmed,
    Unavailable,
}

/// <summary>台帳の案（報告書の <c>WatchlistProposalBindingView</c> と同じ形）。</summary>
public sealed record BoundWatchlistProposal(
    IReadOnlyList<BoundWatchlistChange> Changes,
    IReadOnlyList<BoundWatchlistSnapshotEntry>? Snapshot,
    bool ApplyRecorded,
    bool SecondFactorEnforced);

public sealed record BoundWatchlistChange(string? Action, string? Symbol);

public sealed record BoundWatchlistSnapshotEntry(string? Symbol, string? Market);

public sealed record WatchlistProposalLookupResult(WatchlistProposalLookupStatus Status, BoundWatchlistProposal? Proposal);

/// <summary>NFR-06, IADR-0634: 台帳を照会する口。</summary>
public interface IWatchlistProposalSource
{
    Task<WatchlistProposalLookupResult> FindAsync(string periodKey, int version, CancellationToken cancellationToken);
}

/// <summary>宛先（Reports:BaseUrl）が無い・読めない構成。すべて Unavailable（閉じる向き）。</summary>
public sealed class UnavailableWatchlistProposalSource : IWatchlistProposalSource
{
    public Task<WatchlistProposalLookupResult> FindAsync(string periodKey, int version, CancellationToken cancellationToken) =>
        Task.FromResult(new WatchlistProposalLookupResult(WatchlistProposalLookupStatus.Unavailable, null));
}

/// <summary>
/// 🔴 NFR-06, FR-13, FR-14, ADR-0042 決定 1, IADR-0634: 監視銘柄の入れ替えの適用の門。<b>提供側（市場監視）で確かめる</b>。
/// <list type="bullet">
///   <item>適用してよいのは、報告書の台帳にある「その版で確定された（確定は第 2 要素つき）・未適用の案」と、要求の入れ替え・期待値が
///   一致するときだけ（ボットの渡す任意の銘柄の一覧では入れ替えない）。</item>
///   <item>Audit（段 1）は結果を記録するだけで、適用を止めない。Enforce（段 2）は Bound 以外を拒む（台帳へ届かなければ拒む）。</item>
///   <item>第 2 要素のコードは確定の時に報告書が確かめる（IADR-0626 の状態の持ち主はリスク管理）。適用では求めない
///   （一度使ったコードは再び通らないため）。</item>
/// </list>
/// </summary>
public sealed partial class WatchlistProposalBindingGate(
    WatchlistProposalBindingOptions options, IWatchlistProposalSource source, ILogger<WatchlistProposalBindingGate> logger)
{
    /// <summary>ログの印（運用の手順書が数える）。</summary>
    public const string LogMarker = "watchlist-proposal-binding:";

    // ボットが送る案の出所（PolicyApprovalCommandHandler の `{key}-v{version}`）。会話キーは英数字とハイフン（報告書の照会と同じ 32 文字まで）。
    [GeneratedRegex(@"\A(?<key>[A-Za-z0-9-]{1,32})-v(?<version>[1-9][0-9]{0,8})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    public WatchlistProposalBindingMode Mode => options.Mode;

    public async Task<WatchlistProposalBindingDecision> CheckAsync(
        string proposalRef, IReadOnlyList<MonitoredSymbol>? expected, IReadOnlyList<ProposedWatchlistChange>? changes,
        CancellationToken cancellationToken)
    {
        if (options.Mode == WatchlistProposalBindingMode.Off)
            return new(WatchlistProposalBindingOutcome.Skipped, true);

        var outcome = await DecideAsync(proposalRef, expected, changes, cancellationToken).ConfigureAwait(false);
        var allowed = options.Mode == WatchlistProposalBindingMode.Audit || outcome == WatchlistProposalBindingOutcome.Bound;
        logger.Log(
            outcome == WatchlistProposalBindingOutcome.Bound ? LogLevel.Information : LogLevel.Warning,
            "{Marker} proposal={ProposalRef} outcome={Outcome} mode={Mode} allowed={Allowed}",
            LogMarker, ReferencePattern().IsMatch(proposalRef) ? proposalRef : "-", outcome, options.Mode, allowed);
        return new(outcome, allowed);
    }

    private async Task<WatchlistProposalBindingOutcome> DecideAsync(
        string proposalRef, IReadOnlyList<MonitoredSymbol>? expected, IReadOnlyList<ProposedWatchlistChange>? changes,
        CancellationToken cancellationToken)
    {
        var match = ReferencePattern().Match(proposalRef);
        if (!match.Success
            || !int.TryParse(match.Groups["version"].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var version))
            return WatchlistProposalBindingOutcome.MalformedReference;

        var found = await source.FindAsync(match.Groups["key"].Value, version, cancellationToken).ConfigureAwait(false);
        switch (found.Status)
        {
            case WatchlistProposalLookupStatus.Invalid:
                return WatchlistProposalBindingOutcome.MalformedReference;
            case WatchlistProposalLookupStatus.NotProposal:
                return WatchlistProposalBindingOutcome.NotProposal;
            case WatchlistProposalLookupStatus.NotConfirmed:
                return WatchlistProposalBindingOutcome.NotConfirmed;
            case WatchlistProposalLookupStatus.Unavailable:
                return WatchlistProposalBindingOutcome.Unavailable;
        }

        var proposal = found.Proposal!;
        if (!proposal.SecondFactorEnforced)
            return WatchlistProposalBindingOutcome.SecondFactorNotEnforced;
        if (proposal.ApplyRecorded)
            return WatchlistProposalBindingOutcome.AlreadyApplied;
        if (proposal.Snapshot is not { } snapshot)
            return WatchlistProposalBindingOutcome.SnapshotUnknown;

        return SameChanges(proposal.Changes, changes) && SameSnapshot(snapshot, expected)
            ? WatchlistProposalBindingOutcome.Bound
            : WatchlistProposalBindingOutcome.Mismatch;
    }

    // 入れ替えは操作と銘柄の並びで比べる（理由の文は比べない）。
    private static bool SameChanges(IReadOnlyList<BoundWatchlistChange> ledger, IReadOnlyList<ProposedWatchlistChange>? requested)
    {
        if (requested is null || requested.Count != ledger.Count)
            return false;
        for (var i = 0; i < ledger.Count; i++)
        {
            var action = requested[i].Action == ProposedWatchlistAction.Add ? "add" : "remove";
            if (!string.Equals(ledger[i].Action, action, StringComparison.Ordinal)
                || !string.Equals(ledger[i].Symbol?.Trim(), requested[i].Symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    // 期待値（案を作った時点の監視銘柄）は集合で比べる（銘柄の大小文字は区別しない。案の検証〔WatchlistProposalPlan〕と同じ）。
    private static bool SameSnapshot(IReadOnlyList<BoundWatchlistSnapshotEntry> ledger, IReadOnlyList<MonitoredSymbol>? requested)
    {
        if (requested is null)
            return false;
        var expected = new HashSet<(string, Market)>();
        foreach (var entry in ledger)
        {
            if (string.IsNullOrWhiteSpace(entry.Symbol) || !Enum.TryParse<Market>(entry.Market, ignoreCase: false, out var market)
                || !Enum.IsDefined(market))
                return false;
            expected.Add((entry.Symbol.Trim().ToUpperInvariant(), market));
        }

        var actual = requested.Select(s => (s.Symbol.Trim().ToUpperInvariant(), s.Market)).ToHashSet();
        return expected.SetEquals(actual) && actual.Count == requested.Count;
    }
}

/// <summary>NFR-06, IADR-0634: Enforce で結び付かなかった（REST は 403・gRPC は PERMISSION_DENIED）。文言は固定（入力を含まない）。</summary>
internal static class WatchlistProposalBindingRejection
{
    public const string Message = "確定済みの方針の案と結び付けられないため、監視銘柄の入れ替えを 1 件も適用していません。"
        + "方針を確認ボタン（確認コードつき）で確定してから適用してください。";
}
