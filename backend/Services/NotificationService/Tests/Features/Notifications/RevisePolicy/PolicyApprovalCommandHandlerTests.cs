using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.ReviewReport;
using NotificationService.Features.Notifications.RevisePolicy;
using Xunit;

namespace NotificationService.Tests;

// FR-13, FR-14, FR-07, ADR-0042 決定 1・2, ADR-0003, #1025, IADR-0433: `/policy` の確認ボタン（T-10-1396〜T-10-1404）。
// 確定 → （確定できたときだけ）台帳の案を引く → 案を作った時点の監視銘柄を期待値として適用 → 内訳を台帳と Discord へ。
public class PolicyApprovalCommandHandlerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string Key = "daily-2026-09-28";

    private static readonly Guid AttemptId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly WatchlistProposalDetail Proposal = new(
        AttemptId, Key, 3,
        [new WatchlistChangeSuggestionView("add", "NVDA", "AI 需要"), new WatchlistChangeSuggestionView("remove", "META", "決算前")],
        [new WatchlistSnapshotItemView("AAPL", "UnitedStates"), new WatchlistSnapshotItemView("META", "UnitedStates")],
        ApplyRecorded: false);

    private static DiscordBotOptions Options()
    {
        var options = new DiscordBotOptions { GuildId = Guild, ChannelId = Channel, KillSwitchConfirmationPhrase = "STOP TRADING" };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "developer";
        return options;
    }

    private static DiscordCommandContext Context(string raw = $"/policy approve {Key} 3", string user = OwnerUser) =>
        new(Guild, Channel, user, false, raw);

    private sealed class Reports(bool confirms = true) : IReportReviewController
    {
        public int Confirms { get; private set; }

        public string? OnBehalfOf { get; private set; }

        public Task<ReportReviewResult> GetReviewAsync(string periodKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReportReviewResult(true, 3, "版 3"));

        public Task<ReportConfirmResult> ConfirmAsync(string periodKey, int expectedVersion, string onBehalfOf, CancellationToken cancellationToken = default)
        {
            Confirms++;
            OnBehalfOf = onBehalfOf;
            return Task.FromResult(confirms
                ? new ReportConfirmResult(true, true, $"報告書 {periodKey}（版 {expectedVersion}）を確定しました。")
                : new ReportConfirmResult(true, false, "版番号が一致しません。最新のドラフトを確認してください。"));
        }

        public Task<ReportReviewResult> RequestChangesAsync(string periodKey, int expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReportReviewResult(true, expectedVersion, "差し戻し"));

        public Task<IReadOnlyList<string>> ListPeriodKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static (PolicyApprovalCommandHandler Handler, Reports Reports, FakePolicyRevisionController Policies, FakeWatchlistController Watchlist) Create(
        bool confirms = true, WatchlistProposalLookup? lookup = null, WatchlistApplyOutcome? apply = null)
    {
        var reports = new Reports(confirms);
        var policies = new FakePolicyRevisionController { Lookup = lookup ?? new WatchlistProposalLookup(true, true, Proposal, "照会しました") };
        var watchlist = new FakeWatchlistController
        {
            ApplyOutcome = apply ?? new WatchlistApplyOutcome(
                WatchlistApplyStatus.Applied,
                [new WatchlistApplyItemView("add", "NVDA", true, null), new WatchlistApplyItemView("remove", "META", false, "銘柄 META は監視対象にありません")],
                new FinnhubEstimateView(2880, 300, true),
                "適用しました"),
        };
        var handler = new PolicyApprovalCommandHandler(
            new ReportCommandHandler(reports, new VersionedConfirmationGuard(), Options(), NullLogger<ReportCommandHandler>.Instance),
            policies, watchlist, Options(), NullLogger<PolicyApprovalCommandHandler>.Instance);
        return (handler, reports, policies, watchlist);
    }

    // T-10-1396: 確定できたら、台帳の案の銘柄だけを、案を作った時点の監視銘柄を期待値として、本人の代理で適用し、内訳を記録して見せる。
    // Finnhub の推定は警告だけ（利用者裁定 2026-09-26）。
    [Fact]
    public async Task 確定できたら台帳の案だけを本人として適用し内訳を記録する()
    {
        var (handler, reports, policies, watchlist) = Create();

        var result = await handler.HandleAsync(Context());

        result.ConfirmedNow.Should().BeTrue();
        result.WatchlistApplyStatus.Should().Be(WatchlistApplyStatus.Applied);
        reports.OnBehalfOf.Should().Be("developer");
        policies.Lookups.Should().Equal((Key, 3));
        var apply = watchlist.Applies.Should().ContainSingle().Subject;
        apply.Expected.Should().Equal(Proposal.Snapshot!);
        apply.Changes.Should().Equal(Proposal.Changes, "適用するのは台帳に記録された案の銘柄だけ");
        (apply.ProposalRef, apply.OnBehalfOf).Should().Be(("daily-2026-09-28-v3", "developer"));
        var record = policies.Records.Should().ContainSingle().Subject;
        (record.AttemptId, record.Outcome, record.OnBehalfOf).Should().Be((AttemptId, "applied", "developer"));
        record.Items.Should().HaveCount(2);
        result.Message.Should().Contain("を確定しました").And.Contain("適用 1 件・適用せず 1 件")
            .And.Contain("追加 NVDA: 適用しました").And.Contain("除外 META: 適用しませんでした（銘柄 META は監視対象にありません）")
            .And.Contain("推定 2,880 回/日（暫定上限 300 回/日を超過・警告のみ）");
    }

    // T-10-1397: 確定できなかった（版落ち）なら案を引かず、適用しない。
    [Fact]
    public async Task 確定できなければ適用しない()
    {
        var (handler, _, policies, watchlist) = Create(confirms: false);

        var result = await handler.HandleAsync(Context());

        result.ConfirmedNow.Should().BeFalse();
        result.Message.Should().Contain("適用していません");
        policies.TotalCalls.Should().Be(0);
        watchlist.TotalCalls.Should().Be(0);
    }

    // T-10-1398: 二重押下（同じ版の 2 回目）は確定 API を呼ばず（層1）、適用も 2 回目は行わない。
    [Fact]
    public async Task 二重押下では二度目の適用をしない()
    {
        var (handler, reports, _, watchlist) = Create();

        await handler.HandleAsync(Context());
        var second = await handler.HandleAsync(Context());

        reports.Confirms.Should().Be(1);
        second.ConfirmedNow.Should().BeFalse();
        watchlist.Applies.Should().ContainSingle();
    }

    // T-10-1480（#1029, IADR-0433 の 2026-09-26 追記）: 同じプロセスで先に `/report approve` した版の `/policy` のボタンは、窓口の
    // 二重押下の吸収（層1）で確定を確かめられない。案を引かず適用せず、確定済みで未適用なら設定画面から変えられると案内する。
    [Fact]
    public async Task 先にreport_approveした版のボタンでは適用せず設定画面を案内する()
    {
        var reports = new Reports();
        var guard = new VersionedConfirmationGuard();
        var reportHandler = new ReportCommandHandler(reports, guard, Options(), NullLogger<ReportCommandHandler>.Instance);
        var policies = new FakePolicyRevisionController { Lookup = new WatchlistProposalLookup(true, true, Proposal, "照会しました") };
        var watchlist = new FakeWatchlistController();
        var handler = new PolicyApprovalCommandHandler(
            reportHandler, policies, watchlist, Options(), NullLogger<PolicyApprovalCommandHandler>.Instance);

        (await reportHandler.HandleAsync(Context($"/report approve {Key} 3"))).ConfirmedNow.Should().BeTrue("前提: /report approve で確定した");
        var result = await handler.HandleAsync(Context());

        result.ConfirmedNow.Should().BeFalse();
        result.Message.Should().Contain("適用していません").And.Contain("設定画面から変更してください");
        (reports.Confirms, policies.TotalCalls, watchlist.TotalCalls).Should().Be((1, 0, 0));
    }

    // T-10-1481（#1029）: 案の照会が一時的に失敗した後の押し直しも、同じプロセスでは確定を確かめられない。2 回目は照会も適用もせず、
    // 設定画面を案内する（1 回目の応答も設定画面を案内している）。
    [Fact]
    public async Task 照会の失敗の後の押し直しでは適用せず設定画面を案内する()
    {
        var (handler, reports, policies, watchlist) = Create(lookup: new WatchlistProposalLookup(false, false, null, "HTTP 503"));

        var first = await handler.HandleAsync(Context());
        var second = await handler.HandleAsync(Context());

        first.Message.Should().Contain("照会できなかったため、適用していません").And.Contain("設定画面から変更してください");
        second.ConfirmedNow.Should().BeFalse();
        second.Message.Should().Contain("適用していません").And.Contain("設定画面から変更してください");
        (reports.Confirms, policies.Lookups.Count, watchlist.TotalCalls).Should().Be((1, 1, 0));
    }

    // T-10-1399: 案の照会の失敗・/policy の案でない版・入れ替え無し・記録済みでは適用しない（照会の失敗は失敗と伝える）。
    [Theory]
    [MemberData(nameof(NoApplyLookups))]
    public async Task 案が無いか分からなければ適用しない(WatchlistProposalLookup lookup, string expected)
    {
        var (handler, _, policies, watchlist) = Create(lookup: lookup);

        var result = await handler.HandleAsync(Context());

        result.ConfirmedNow.Should().BeTrue();
        result.Message.Should().Contain(expected);
        watchlist.Applies.Should().BeEmpty();
        policies.Records.Should().BeEmpty();
    }

    public static TheoryData<WatchlistProposalLookup, string> NoApplyLookups() => new()
    {
        { new WatchlistProposalLookup(false, false, null, "HTTP 503"), "照会できなかったため、適用していません" },
        { new WatchlistProposalLookup(true, false, null, "案ではありません"), "入れ替え案はありません" },
        { new WatchlistProposalLookup(true, true, Proposal with { Changes = [] }, "照会"), "入れ替え案はありません" },
        { new WatchlistProposalLookup(true, true, Proposal with { ApplyRecorded = true }, "照会"), "既に適用の記録があります" },
    };

    // T-10-1400: 案を作った時点の監視銘柄が分からない案は適用せず、その旨を記録する。
    [Fact]
    public async Task 監視銘柄が分からない案は適用せず記録する()
    {
        var (handler, _, policies, watchlist) = Create(lookup: new WatchlistProposalLookup(true, true, Proposal with { Snapshot = null }, "照会"));

        var result = await handler.HandleAsync(Context());

        watchlist.Applies.Should().BeEmpty();
        policies.Records.Should().ContainSingle().Which.Outcome.Should().Be("snapshot-unknown");
        result.Message.Should().Contain("分からないため、入れ替えを適用していません");
    }

    // T-10-1401: 変わっていた（Stale）・受理されず（Rejected）・不明（Indeterminate）をそれぞれの言葉で伝え、記録する。
    [Theory]
    [InlineData(WatchlistApplyStatus.Stale, "stale", "案を作った後に監視銘柄が変わった")]
    [InlineData(WatchlistApplyStatus.Rejected, "rejected", "適用できませんでした")]
    [InlineData(WatchlistApplyStatus.Indeterminate, "indeterminate", "結果が分かりません")]
    public async Task 適用の失敗と不明をそれぞれ伝える(WatchlistApplyStatus status, string outcome, string expected)
    {
        var message = status switch
        {
            WatchlistApplyStatus.Stale => "案を作った後に監視銘柄が変わったため、入れ替えを 1 件も適用していません。",
            WatchlistApplyStatus.Rejected => "形式が不正です（入れ替えは 1 件も適用していません）",
            _ => "入れ替えの適用の結果が分かりません（設定画面で確認してください）",
        };
        var (handler, _, policies, _) = Create(apply: new WatchlistApplyOutcome(status, [], null, message));

        var result = await handler.HandleAsync(Context());

        result.WatchlistApplyStatus.Should().Be(status);
        result.Message.Should().Contain(expected);
        policies.Records.Should().ContainSingle().Which.Outcome.Should().Be(outcome);
    }

    // T-10-1402: 内訳の記録に失敗しても適用は巻き戻さず、利用者に記録の失敗を見せる。
    [Fact]
    public async Task 内訳の記録の失敗を見せる()
    {
        var (handler, _, policies, _) = Create();
        policies.RecordSucceeds = false;

        var result = await handler.HandleAsync(Context());

        result.Message.Should().Contain("内訳の記録に失敗しました");
    }

    // T-10-1403: 多層認証を通らない押下・銘柄を添えた形・版の無い形では、確定も照会も適用もしない。
    [Theory]
    [InlineData($"/policy approve {Key} 3", "stranger")]
    [InlineData($"/policy approve {Key} 3 NVDA", OwnerUser)]
    [InlineData($"/policy approve {Key}", OwnerUser)]
    [InlineData($"/report approve {Key} 3", OwnerUser)]
    public async Task 認可と解析を通らなければ何もしない(string raw, string user)
    {
        var (handler, reports, policies, watchlist) = Create();

        var result = await handler.HandleAsync(Context(raw, user));

        result.IsDenied.Should().BeTrue();
        (reports.Confirms, policies.TotalCalls, watchlist.TotalCalls).Should().Be((0, 0, 0));
    }

    // T-10-1404: 応答文は Discord の上限に収まる（入れ替え 10 件・長い理由）。推定が上限内なら「以内」と書く。
    [Fact]
    public async Task 応答は上限に収まる()
    {
        var (handler, _, _, _) = Create(apply: new WatchlistApplyOutcome(
            WatchlistApplyStatus.Applied,
            [.. Enumerable.Range(0, 10).Select(i => new WatchlistApplyItemView("add", $"SY{(char)('A' + i)}", false, new string('理', 250)))],
            new FinnhubEstimateView(1, 300, false), "m"));

        var result = await handler.HandleAsync(Context());

        result.Message.Length.Should().BeLessThanOrEqualTo(PolicyApprovalCommandHandler.MaxLength);
        PolicyApprovalCommandHandler.Breakdown(new WatchlistApplyOutcome(
            WatchlistApplyStatus.Applied, [], new FinnhubEstimateView(1, 300, false), "m")).Should().Contain("暫定上限 300 回/日以内");
    }
}
