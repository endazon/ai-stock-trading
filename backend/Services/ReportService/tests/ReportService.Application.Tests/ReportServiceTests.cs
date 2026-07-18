using AiStockTrading.Report.Application;
using AiStockTrading.Report.Application.Adapters;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Domain;
using FluentAssertions;
using Xunit;
using AppSvc = AiStockTrading.Report.Application.Services.ReportService;

namespace AiStockTrading.Report.Application.Tests;

// FR-06/07, UC-03〜05, ADR-0007: 報告書の upsert・版番号付き冪等確定・確定済み日報方針照会を検証する。
public class ReportServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private static AppSvc NewService() => new(new InMemoryReportStore(), new FixedClock());

    private static TradingReport Daily(string key, DateOnly date, string policy = "翌営業日は押し目買い", int av = 1) =>
        new() { PeriodKey = key, Kind = ReportKind.Daily, PeriodStart = date, PolicySummary = policy, AssumptionsVersion = av };

    [Fact]
    public void ドラフトを作成すると_Version1_の_Draft_になる()
    {
        var svc = NewService();
        svc.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), expectedVersion: 0).Should().Be(1);

        var stored = svc.Get("daily-2026-07-10");
        stored!.Version.Should().Be(1);
        stored.Report.State.Should().Be(ReportState.Draft);
    }

    [Fact]
    public void 確定は_Draft_から_Confirmed_へ遷移し_ConfirmedAt_を記録する()
    {
        var svc = NewService();
        svc.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), 0);

        var result = svc.Confirm("daily-2026-07-10", expectedVersion: 1, actor: "owner");

        result!.Transitioned.Should().BeTrue();
        result.Report.State.Should().Be(ReportState.Confirmed);
        result.Report.ConfirmedAt.Should().Be(Now);
    }

    [Fact]
    public void 確定済みの再確定は冪等_遷移しない()
    {
        var svc = NewService();
        svc.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), 0);
        svc.Confirm("daily-2026-07-10", 1, "owner");

        var again = svc.Confirm("daily-2026-07-10", 1, "owner");

        again!.Transitioned.Should().BeFalse();
        again.Report.State.Should().Be(ReportState.Confirmed);
    }

    [Fact]
    public void 版番号が不一致の確定は競合で弾かれる()
    {
        var svc = NewService();
        svc.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), 0);

        var act = () => svc.Confirm("daily-2026-07-10", expectedVersion: 99, actor: "owner");

        act.Should().Throw<ReportConcurrencyException>();
    }

    [Fact]
    public void 存在しない報告書の確定は_null()
    {
        NewService().Confirm("daily-nope", 1, "owner").Should().BeNull();
    }

    [Fact]
    public void 確定済み報告書は変更できない()
    {
        var svc = NewService();
        svc.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), 0);
        svc.Confirm("daily-2026-07-10", 1, "owner");

        var act = () => svc.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10), "改ざん"), 2);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void 確定済み日報方針は最新の確定日報を返す()
    {
        var svc = NewService();
        svc.UpsertDraft(Daily("daily-2026-07-09", new DateOnly(2026, 7, 9), "9日方針"), 0);
        svc.Confirm("daily-2026-07-09", 1, "owner");
        svc.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10), "10日方針", av: 2), 0);
        svc.Confirm("daily-2026-07-10", 1, "owner");

        var policy = svc.GetConfirmedDailyPolicy();

        policy!.Date.Should().Be(new DateOnly(2026, 7, 10)); // 最新
        policy.Summary.Should().Be("10日方針");
        policy.AssumptionsVersion.Should().Be(2);
    }

    [Fact]
    public void 未確定なら確定済み日報方針は_null()
    {
        var svc = NewService();
        svc.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), 0); // ドラフトのみ

        svc.GetConfirmedDailyPolicy().Should().BeNull();
    }

    [Fact]
    public void 確定にはアクターが必須()
    {
        var svc = NewService();
        svc.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10)), 0);

        var act = () => svc.Confirm("daily-2026-07-10", 1, actor: "");

        act.Should().Throw<ArgumentException>();
    }

    // FR-06, UC-03, IADR-0071 決定4: 初回月報ブートストラップ。確定済み月報が無ければ初期監視銘柄のドラフトを返す。
    [Fact]
    public void 初回月報ブートストラップ_確定済み月報が無ければドラフトを返す()
    {
        var svc = NewService(); // clock = 2026-07-10

        var draft = svc.BuildMonthlyBootstrap(["AAPL", "7203"], assumptionsVersion: 1);

        draft.Should().NotBeNull();
        draft!.Kind.Should().Be(ReportKind.Monthly);
        draft.PeriodKey.Should().Be("monthly-2026-07");
        draft.PolicySummary.Should().Contain("AAPL");
    }

    [Fact]
    public void 初回月報ブートストラップ_確定済み月報があれば不要で_null()
    {
        var svc = NewService();
        svc.UpsertDraft(new TradingReport
        {
            PeriodKey = "monthly-2026-06",
            Kind = ReportKind.Monthly,
            PeriodStart = new DateOnly(2026, 6, 1),
            PolicySummary = "6月方針",
        }, 0);
        svc.Confirm("monthly-2026-06", 1, "owner");

        svc.BuildMonthlyBootstrap(["AAPL"], 1).Should().BeNull();
    }

    // FR-07, ADR-0003, IADR-0071 決定2: 無応答時の既定動作（継続）は構造的に成立している。
    // 提示済みだが未確定の新ドラフトがあっても、方針照会は直近の確定済みを返し続ける（新方針は自動適用されない）。
    [Fact]
    public void 無応答時の既定動作_未確定の新ドラフトは方針を上書きせず直近の確定済みを継続する()
    {
        var svc = NewService();
        // 7/9 を確定（過去に人が承認した方針）。
        svc.UpsertDraft(Daily("daily-2026-07-09", new DateOnly(2026, 7, 9), "9日方針"), 0);
        svc.Confirm("daily-2026-07-09", 1, "owner");
        // 7/10 の新方針をドラフトのみ（提示済みだが未応答＝未確定）。
        svc.UpsertDraft(Daily("daily-2026-07-10", new DateOnly(2026, 7, 10), "10日方針（未確定）", av: 2), 0);

        var policy = svc.GetConfirmedDailyPolicy();

        // 無応答（未確定）の新方針は適用されず、直近の確定済み（9日方針）を継続する。
        policy!.Date.Should().Be(new DateOnly(2026, 7, 9));
        policy.Summary.Should().Be("9日方針");
    }
}
