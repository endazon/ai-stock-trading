using System.Net;
using System.Net.Http.Json;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-20, UC-06, ADR-0008, IADR-0070: 段階ゲートエンドポイントの認可（OwnerOnly）・承認遷移・fail-safe 昇格・
// 遷移履歴の永続化を検証する。各テストは専用ファクトリ（隔離 InMemory DB）で段階状態の相互干渉を避ける。
public class StageGateEndpointsTests
{
    private const string Owner = "trading-owner";
    private const string Service = "trading-service";

    private static HttpClient OwnerClient(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Owner);
        return client;
    }

    // 後続の実供給（BacktestService s2s 等）に相当する段階別実績の記録を DI 経由で行う（本番は Save の受け口）。
    private static void SeedPerformance(RiskWorkerWebApplicationFactory factory, StagePerformance performance)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStagePerformanceStore>().Save(performance);
    }

    [Fact]
    public async Task 未認証の_stage_gate_取得は401()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = factory.CreateClient();

        var res = await client.GetAsync("/risk-controls/stage-gate");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task サービスロールは段階遷移できない_403()
    {
        // IADR-0051 最小権限: trading-service は OwnerOnly の段階遷移を持たない（生成AI・自動処理は承認できない）。
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Service);

        var res = await client.PostAsJsonAsync("/risk-controls/stage-gate/transition",
            new { targetStage = (int)TradingStage.Stage1Simulate });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 既定では_Stage0から1昇格は_422_バックテスト未合格_fail_safe()
    {
        // 受け入れ基準（fail-safe）: 段階別実績未記録では昇格を許可しない（承認があっても不可）。
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.PostAsJsonAsync("/risk-controls/stage-gate/transition",
            new { targetStage = (int)TradingStage.Stage1Simulate });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var result = await res.Content.ReadFromJsonAsync<TransitionResultDto>();
        result!.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);
    }

    [Fact]
    public async Task バックテスト合格を記録すれば承認で昇格し履歴に残る()
    {
        // 受け入れ基準: バックテスト合格した戦略のみ Stage 1 へ／承認遷移が履歴に残り監査できる。
        using var factory = new RiskWorkerWebApplicationFactory();
        SeedPerformance(factory, new StagePerformance { BacktestPassed = true });
        var client = OwnerClient(factory);

        var res = await client.PostAsJsonAsync("/risk-controls/stage-gate/transition",
            new { targetStage = (int)TradingStage.Stage1Simulate });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await res.Content.ReadFromJsonAsync<TransitionResultDto>();
        result!.Accepted.Should().BeTrue();

        // 別リクエスト（別 DI スコープ）でも永続化された履歴が読める。
        var history = await client.GetFromJsonAsync<List<TransitionDto>>("/risk-controls/stage-gate/history");
        history.Should().ContainSingle();
        history![0].FromStage.Should().Be(TradingStage.Stage0Verification);
        history[0].ToStage.Should().Be(TradingStage.Stage1Simulate);
        history[0].ApprovedBy.Should().Be("test-owner");
    }

    [Fact]
    public async Task 昇格受理時に_StageTransitioned_をバス発行する()
    {
        // FR-20, FR-11, #167, IADR-0082: 受理遷移は中央監査集約のためバスへ発行する（受理時のみ）。
        using var factory = new RiskWorkerWebApplicationFactory();
        SeedPerformance(factory, new StagePerformance { BacktestPassed = true });
        var client = OwnerClient(factory);

        // ADR-0013, IADR-0129, #354: MassTransit の ITestHarness に代えて Wolverine.Tracking で発行を捕捉する。
        HttpResponseMessage res = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            res = await client.PostAsJsonAsync("/risk-controls/stage-gate/transition",
                new { targetStage = (int)TradingStage.Stage1Simulate });
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        session.Sent.MessagesOf<StageTransitioned>().Should().Contain(m =>
            m.FromStage == (int)TradingStage.Stage0Verification
                && m.ToStage == (int)TradingStage.Stage1Simulate
                && m.Kind == nameof(StageTransitionKind.Promotion)
                && m.ApprovedBy == "test-owner");
    }

    // ---- #466, FR-20, FR-11, SC-02, §4.1 追補3（質問票 第15回 Q13-b）, IADR-0180 ----
    //
    // **「警告を無視して昇格した事実」を実際に監査へ供給する唯一の箇所が本エンドポイントである。**
    // `AuditEntryFactoryTests` はイベントを直接 new する層であり、**ここで定数を書いても緑のまま**になる
    // （＝「その設定で昇格した」記録が既定値 100 として残る欠陥を検知できない）。本 2 本がその経路を塞ぐ。

    [Fact]
    public async Task 昇格受理時の_StageTransitioned_が引き下げられた実効設定値と警告有無を運ぶ()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        SeedPerformance(factory, new StagePerformance { BacktestPassed = true });
        var client = OwnerClient(factory);

        // 最小取引件数を既定 100 から 5 件へ引き下げる（SC-02 と同じ経路＝設定値の権威源）。
        var put = await client.PutAsJsonAsync("/risk-controls/settings/stage1-minimum-trade-count",
            new { minimumTradeCount = 5, reason = "#466 の検証" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage res = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            res = await client.PostAsJsonAsync("/risk-controls/stage-gate/transition",
                new { targetStage = (int)TradingStage.Stage1Simulate });
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var published = session.Sent.MessagesOf<StageTransitioned>().Should().ContainSingle().Subject;
        // **設定した 5 件がそのまま載る**（既定 100 が載っていたら「なぜ 5 件で上がったのか」を追えない）。
        published.Stage1MinimumTradeCount.Should().Be(5);
        published.Stage1BelowStatisticalBasis.Should().BeTrue();
    }

    // **否定形**: 既定値のままなら警告は出ていなかったと記録される（設定値そのものは常に載る）。
    [Fact]
    public async Task 既定の設定値のままなら_StageTransitioned_は警告なしとして記録する()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        SeedPerformance(factory, new StagePerformance { BacktestPassed = true });
        var client = OwnerClient(factory);

        HttpResponseMessage res = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            res = await client.PostAsJsonAsync("/risk-controls/stage-gate/transition",
                new { targetStage = (int)TradingStage.Stage1Simulate });
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var published = session.Sent.MessagesOf<StageTransitioned>().Should().ContainSingle().Subject;
        published.Stage1MinimumTradeCount.Should().Be(Stage1TradeCountBounds.Default);
        published.Stage1BelowStatisticalBasis.Should().BeFalse();
    }

    [Fact]
    public async Task 拒否遷移では_StageTransitioned_を発行しない()
    {
        // fail-safe: 受理不能な遷移（バックテスト未合格の昇格）ではイベントを発行しない（拒否時は非発行）。
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        HttpResponseMessage res = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            res = await client.PostAsJsonAsync("/risk-controls/stage-gate/transition",
                new { targetStage = (int)TradingStage.Stage1Simulate });
        });
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        session.Sent.MessagesOf<StageTransitioned>().Should().BeEmpty();
    }

    [Fact]
    public async Task 範囲外の_targetStage_は400で500にならない()
    {
        // 範囲外 enum（降格方向）は StageGatePolicy.SettingsFor の KeyNotFoundException（500）に落ちる前に 400 で弾く。
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.PostAsJsonAsync("/risk-controls/stage-gate/transition",
            new { targetStage = 99 });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 現況取得は現段階_Stage0_と昇格評価を返す()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var status = await client.GetFromJsonAsync<StatusDto>("/risk-controls/stage-gate");

        status!.CurrentStage.Should().Be(TradingStage.Stage0Verification);
        status.Promotion.TargetStage.Should().Be(TradingStage.Stage1Simulate);
        status.Promotion.Eligible.Should().BeFalse(); // 既定はバックテスト未合格
        status.History.Should().BeEmpty();
    }

    [Fact]
    public async Task 撤退評価は_既定では非発火で200()
    {
        // fail-safe 既定（実績なし）では Stage 0 の撤退は非発火。自動停止の機構自体はサービス単体で検証済み。
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.PostAsync("/risk-controls/stage-gate/withdrawal/evaluate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var assessment = await res.Content.ReadFromJsonAsync<WithdrawalDto>();
        assessment!.Triggered.Should().BeFalse();
    }

    // 応答 JSON の受け皿（プロパティ名は web 既定=camelCase）。
    private sealed record TransitionResultDto(bool Accepted, List<StageGateCriterion> RejectionReasons);

    private sealed record TransitionDto(int Sequence, TradingStage FromStage, TradingStage ToStage, string ApprovedBy);

    private sealed record PromotionDto(TradingStage? TargetStage, bool Eligible);

    private sealed record StatusDto(TradingStage CurrentStage, List<TransitionDto> History, PromotionDto Promotion);

    private sealed record WithdrawalDto(bool Triggered, bool HaltNewEntries, TradingStage? ProposedStage);
}
