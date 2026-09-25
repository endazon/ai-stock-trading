extern alias ReportWorker;
extern alias RiskManagementWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using NotificationService.Domain;
using NotificationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ReportDomain = ReportWorker::ReportService.Domain;
using ReportFeatures = ReportWorker::ReportService.Features.Reports;
using RiskDomain = RiskManagementWorker::RiskManagementService.Domain;
using RiskFeatures = RiskManagementWorker::RiskManagementService.Features.RiskManagement;

namespace NotificationService.Tests;

// 🔴 T-10-913〜916, FR-07, FR-14, FR-20, UC-06, #957, IADR-0408（2026-09-25 追記。T-10-800 の同型）: 通知（Discord の操作）が読む
// 報告書のレビュー照会とリスク管理の段階ゲートに、**送り手の本物の型を送り手の実際の JSON 設定で直列化した応答**を読ませる
// （報告書＝web 既定＋文字列列挙、リスク管理＝web 既定で列挙は数値）。既存の各アダプタのテストは手書きの JSON であり、送り手で
// 項目名を変えても緑のまま、実行時は既定値で読む（`UnsuppliedInputs` → 確定前の「入力が未供給」の警告が出ない／`Accepted` →
// 受理された遷移を「拒否」と報告する／`HaltNewEntries` → 撤退評価の自動停止を報告しない）。送り手がその設定で出していることは
// 報告書側の T-10-921 とリスク管理側の T-10-805（Program.cs の JSON 設定は全エンドポイント共通）が固定する。
public class OperationReadContractTests
{
    private static readonly JsonSerializerOptions ReportWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly JsonSerializerOptions RiskWire = new(JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    // 最小取引件数を 50 件へ下げた設定（送り手が BelowStatisticalBasis=true を宣言する）。
    private static readonly RiskDomain.Stage1GateCriteria Lowered = new(60, 50, 120);

    private static readonly RiskDomain.StageTransition Promoted = new(
        1, TradingStage.Stage1Simulate, TradingStage.Stage2MinimalLive, RiskDomain.StageTransitionKind.Promotion, "owner", T0, "昇格");

    private static HttpClient Client(object value, JsonSerializerOptions options, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(status, JsonSerializer.Serialize(value, value.GetType(), options))) { BaseAddress = new Uri("http://sender") };

    // 🔴 T-10-913: 報告書のレビュー照会（GET /reports/{periodKey}/review）。未供給の入力の警告が確定前に出る。
    [Fact]
    public async Task レビュー照会は送り手の本物の型を直列化した応答から版と未供給の入力を読める()
    {
        var view = new ReportFeatures.ReportReviewView("daily-2026-09-01", ReportDomain.ReviewState.Drafting, 3, ["建玉", "散文（LLM）"]);
        var controller = new HttpReportReviewController(Client(view, ReportWire), NullLogger<HttpReportReviewController>.Instance);

        var result = await controller.GetReviewAsync("daily-2026-09-01");

        (result.Succeeded, result.Version).Should().Be((true, 3));
        result.Message.Should().Contain(ReportUnsuppliedNotice.Prefix).And.Contain("建玉").And.Contain("散文（LLM）");
    }

    // 🔴 T-10-970, FR-14, #843 項目1, IADR-0418: 入力補完が読む軽い一覧（GET /reports/period-keys）。送り手の本物の型
    // `ReportPeriodKeyItem` を報告書の設定で直列化した応答から、会話キーが新しい順に読める（送り手の改名で候補が黙って空にならない）。
    [Fact]
    public async Task 入力補完の軽い一覧は送り手の本物の型を直列化した応答から会話キーを読める()
    {
        ReportFeatures.ReportPeriodKeyItem[] items =
        [
            new("daily-2026-09-17", new DateOnly(2026, 9, 17)),
            new("daily-2026-09-18", new DateOnly(2026, 9, 18)),
            new("weekly-2026-W38", new DateOnly(2026, 9, 14)),
        ];
        var controller = new HttpReportReviewController(Client(items, ReportWire), NullLogger<HttpReportReviewController>.Instance);

        var keys = await controller.ListPeriodKeysAsync();

        keys.Should().Equal("daily-2026-09-18", "daily-2026-09-17", "weekly-2026-W38");
    }

    // 🔴 T-10-944, FR-14, FR-09, UC-03, NFR, #952, IADR-0420: 差し戻し（POST /reports/{periodKey}/request-changes）。送り手は受理の本文に
    // 状態機械の記録（`ReportReview`）を返し、受け手は版を読んで利用者へ示す。サービス間の読み取り契約の検査（T-10-945）が develop で
    // 最初に挙げた所見（本物）で、送り手の `Version` を改名しても両サービスの試験が緑のまま「版 0 を差し戻しました」と表示していた。
    [Fact]
    public async Task 差し戻しの結果は送り手の本物の型を直列化した応答から版を読める()
    {
        var review = new ReportDomain.ReportReview("daily-2026-09-01", ReportDomain.ReviewState.ChangesRequested, 4);
        var controller = new HttpReportReviewController(Client(review, ReportWire), NullLogger<HttpReportReviewController>.Instance);

        var result = await controller.RequestChangesAsync("daily-2026-09-01", 4);

        (result.Succeeded, result.Version).Should().Be((true, 4));
        result.Message.Should().Contain("版 4");
    }

    // 🔴 T-10-914: 段階ゲートの現況（GET /risk-controls/stage-gate）。
    [Fact]
    public async Task 段階ゲートの現況は送り手の本物の型を直列化した応答から読める()
    {
        var status = new RiskFeatures.StageGateStatus(
            TradingStage.Stage2MinimalLive,
            new RiskDomain.StageSettings(TradingStage.Stage2MinimalLive, BrokerProvider.MoomooReal, 0.30m),
            [Promoted],
            new RiskDomain.PromotionAssessment(
                TradingStage.Stage3ScaledLive, false, [RiskDomain.StageGateCriterion.SlippageOrCostExceeded]),
            new RiskDomain.WithdrawalAssessment(false, null, false, null),
            new RiskDomain.Stage1Progress(60, 120),
            Lowered,
            new RiskFeatures.ShortSellReleaseState(RiskDomain.ShortSellReleaseVerdictStatus.Missing, null, "fp", "strategy", false, null));
        var controller = new HttpStageGateController(Client(status, RiskWire), NullLogger<HttpStageGateController>.Instance);

        var result = await controller.GetStatusAsync();

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain("現段階: Stage 2（少額実弾）").And.Contain("モード: moomoo REAL（実弾）")
            .And.Contain("実効スリッページ・費用が想定超過").And.Contain("#1 昇格 Stage 1（SIMULATE）→Stage 2（少額実弾）");
        result.Stage1Warning.Should().Contain("50 件");
    }

    // 🔴 T-10-915: 段階遷移（POST /risk-controls/stage-gate/transition）。受理（200）と受理不能（422）。
    [Fact]
    public async Task 段階遷移の受理と拒否は送り手の本物の型を直列化した応答から読める()
    {
        var accepted = new RiskDomain.StageTransitionResult(
            true, Promoted, new RiskDomain.StageSettings(TradingStage.Stage2MinimalLive, BrokerProvider.MoomooReal, 0.30m), [], Lowered);
        var rejected = new RiskDomain.StageTransitionResult(
            false, null, null, [RiskDomain.StageGateCriterion.Stage1TradeCountInsufficient], RiskDomain.Stage1GateCriteria.Default);

        var ok = await new HttpStageGateController(Client(accepted, RiskWire), NullLogger<HttpStageGateController>.Instance)
            .RequestTransitionAsync(2, "owner");
        var ng = await new HttpStageGateController(
                Client(rejected, RiskWire, HttpStatusCode.UnprocessableEntity), NullLogger<HttpStageGateController>.Instance)
            .RequestTransitionAsync(2, "owner");

        (ok.Succeeded, ok.Accepted).Should().Be((true, true));
        ok.Message.Should().Contain("Stage 2（少額実弾）");
        ok.Stage1Warning.Should().Contain("50 件");
        (ng.Succeeded, ng.Accepted).Should().Be((true, false));
        ng.Message.Should().Contain("取引件数が 100 件に届かない");
        ng.Stage1Warning.Should().BeNull();
    }

    // 🔴 T-10-916: 撤退評価（POST /risk-controls/stage-gate/withdrawal/evaluate）。自動停止を報告する。
    [Fact]
    public async Task 撤退評価は送り手の本物の型を直列化した応答から自動停止を読める()
    {
        var assessment = new RiskDomain.WithdrawalAssessment(
            true, RiskDomain.WithdrawalReason.DrawdownBreachedMultiple, true, TradingStage.Stage1Simulate);
        var controller = new HttpStageGateController(Client(assessment, RiskWire), NullLogger<HttpStageGateController>.Instance);

        var result = await controller.EvaluateWithdrawalAsync();

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain("実DDがバックテスト最大DD×倍率に到達").And.Contain("新規建てを自動停止しました")
            .And.Contain("提案: Stage 1（SIMULATE） へ差し戻し");
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
