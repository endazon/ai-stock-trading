using NotificationService.Features.Notifications;

namespace NotificationService.Tests;

// FR-07, FR-13, FR-14, #1016, #1025: `/policy` の試験で共用する偽物（呼び出しを記録し、応答を差し替えられる）。
internal sealed class FakePolicyRevisionController : IPolicyRevisionController
{
    public FakePolicyRevisionController(PolicyRevisionCommandOutcome? outcome = null) =>
        Outcome = outcome ?? new PolicyRevisionCommandOutcome(true, false, "保存しました");

    public PolicyRevisionCommandOutcome Outcome { get; set; }

    public WatchlistProposalLookup Lookup { get; set; } = new(true, false, null, "この版は /policy の案ではありません");

    public bool RecordSucceeds { get; set; } = true;

    public List<(string? PeriodKey, string Instruction, string OnBehalfOf, IReadOnlyList<WatchlistSnapshotItemView>? Watchlist)> Calls { get; } = [];

    public List<(string PeriodKey, int Version)> Lookups { get; } = [];

    public List<(Guid AttemptId, string Outcome, IReadOnlyList<WatchlistApplyItemView> Items, string Message, string OnBehalfOf)> Records { get; } = [];

    public int TotalCalls => Calls.Count + Lookups.Count + Records.Count;

    public Task<PolicyRevisionCommandOutcome> ReviseAsync(
        string? periodKey, string instruction, string onBehalfOf, IReadOnlyList<WatchlistSnapshotItemView>? currentWatchlist,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((periodKey, instruction, onBehalfOf, currentWatchlist));
        return Task.FromResult(Outcome);
    }

    public Task<WatchlistProposalLookup> GetWatchlistProposalAsync(string periodKey, int version, CancellationToken cancellationToken = default)
    {
        Lookups.Add((periodKey, version));
        return Task.FromResult(Lookup);
    }

    public Task<bool> RecordWatchlistApplyAsync(
        Guid attemptId, string outcome, IReadOnlyList<WatchlistApplyItemView> items, string message, string onBehalfOf,
        CancellationToken cancellationToken = default)
    {
        Records.Add((attemptId, outcome, items, message, onBehalfOf));
        return Task.FromResult(RecordSucceeds);
    }
}

internal sealed class FakeWatchlistController : IMarketMonitorWatchlistController
{
    public WatchlistSnapshotResult Snapshot { get; set; } =
        new(true, [new WatchlistSnapshotItemView("AAPL", "UnitedStates"), new WatchlistSnapshotItemView("MSFT", "UnitedStates")], "照会しました");

    public WatchlistApplyOutcome ApplyOutcome { get; set; } = new(WatchlistApplyStatus.Applied, [], null, "適用しました");

    public int Gets { get; private set; }

    public List<(IReadOnlyList<WatchlistSnapshotItemView> Expected, IReadOnlyList<WatchlistChangeSuggestionView> Changes, string ProposalRef, string OnBehalfOf)> Applies { get; } = [];

    public int TotalCalls => Gets + Applies.Count;

    public Task<WatchlistSnapshotResult> GetWatchlistAsync(CancellationToken cancellationToken = default)
    {
        Gets++;
        return Task.FromResult(Snapshot);
    }

    public Task<WatchlistApplyOutcome> ApplyProposalAsync(
        IReadOnlyList<WatchlistSnapshotItemView> expected, IReadOnlyList<WatchlistChangeSuggestionView> changes, string proposalRef,
        string onBehalfOf, CancellationToken cancellationToken = default)
    {
        Applies.Add((expected, changes, proposalRef, onBehalfOf));
        return Task.FromResult(ApplyOutcome);
    }
}
