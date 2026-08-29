using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, FR-17, #338, 04_report-templates（fixed）:
// **報告書テンプレート出力のゴールデンファイルテスト**（各報告書種別 × 代表データで形式を固定する）。
//
// 🔴 **形式そのものが計画の成果物である。** 04_report-templates は「決まった形式で生成・確定できるよう」
// テンプレートを定義しており、節の増減・見出しの改名・行の並びが**計画との差異**そのものである。
// 個別の Contain 検査だけでは、**節が丸ごと消えたことを検知できない**（消えた節の検査も一緒に消えるため）。
//
// 🔴 **供給あり／なしの 2 系統を持つ。** 未供給の描画は本サービスの規律の中核であり
// （「照会できていない」を「0 件」と書かない）、そこが崩れたことをゴールデンで止める。
//
// **更新の仕方**: 意図してテンプレートを変えたときは、期待と実出力の差分を確認したうえで
// `Golden/` の該当ファイルを実出力で置き換える（`dotnet test` の失敗メッセージに実出力が出る）。
public class ReportTemplateGoldenTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    // 代表データ。**固定値のみ**（時刻・乱数・カルチャに依存しない＝ゴールデンが決定的である）。
    private static ReportView Bare(ReportKind kind) => new()
    {
        Kind = kind,
        PeriodKey = kind switch
        {
            ReportKind.Weekly => "weekly-2026-W35",
            ReportKind.Monthly => "monthly-2026-08",
            _ => "daily-2026-08-28",
        },
        PeriodLabel = kind switch
        {
            ReportKind.Weekly => "2026-W35",
            ReportKind.Monthly => "2026-08",
            _ => "2026-08-28",
        },
        Markets = ["JP", "US"],
        AssumptionsVersion = 3,
        BasedOn = kind == ReportKind.Monthly ? "monthly-2026-07" : "weekly-2026-W35",
        ConfirmedAt = ConfirmedAt,
        Pnl = new PnlSummary(2_000m, 100m, 380m, 1_520m, -300m, 5, 4, 3),
        BuyCount = 3,
        SellCount = 2,
        PolicySummary = "監視銘柄は AAPL・TSLA。1 注文の上限は equity の 25%。",
        Narrative = "当期間は指数が上昇し、押し目買いが機能した。",
    };

    private static ReportView Supplied(ReportKind kind) => Bare(kind) with
    {
        MarginReductions =
        [
            new MaintenanceMarginReductionExecuted(
                new Guid("11111111-1111-1111-1111-111111111111"), 0.38m, 0.40m, 0.45m, 0.46m,
                [new MaintenanceMarginReductionItem("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, 10, 150m, 1_500m)],
                new DateTimeOffset(2026, 8, 5, 14, 30, 0, TimeSpan.Zero)),
        ],
        BuyInInferences = [],
        FxSourceStatus = new FxSourceStatus(
            [], [], [], ["日本銀行「外国為替市況（日次）」"], [],
            [new FxRateSourceUsed("USD", "boj", 1, 2, T0)]),
        LlmModelUsage = new LlmModelUsage(
            ReportNarrativePurposeOf(kind), "claude-opus-5", "claude-opus-5", "Primary"),
        LlmUsage = new LlmUsageRecord(
        [
            new LlmCostIncurred(3_000m, T0, LlmPurposes.TradeDecision, "claude-sonnet-5"),
            new LlmCostIncurred(450m, T0, LlmPurposes.ReportMonthly, "claude-opus-5"),
        ],
        [new LlmFallbackFired("report-daily", "claude-sonnet-5", "claude-haiku-4-5", "FallbackFired", T0)],
        [new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelUnavailable, "claude-sonnet-5", null, T0)],
        new ScreeningDegradationCounts(4, 2, new Dictionary<string, int> { ["RAG"] = 1, ["ニュース"] = 1 })),
        BorrowFees = new BorrowFeeRecord(
            [new BorrowFeeAccrued("AAPL", Market.UnitedStates, new DateOnly(2026, 8, 3), 0.06m, 10_000m, 1.64m, T0)],
            [new BorrowFeeAccrualUnavailable("TSLA", Market.UnitedStates, new DateOnly(2026, 8, 4), "料率照会に失敗", T0)]),
        FxTranslation = new FxTranslationSummary(-1_234m, 5),
        Uptime = new OpenDUptimeRecord(
        [
            new OpenDUptimeDay(new DateOnly(2026, 8, 3), 1.0m),
            new OpenDUptimeDay(new DateOnly(2026, 8, 4), 0.60m),
            new OpenDUptimeDay(new DateOnly(2026, 8, 5), 0.20m),
        ], Stage1CumulativeCountedDays: 41),
        ThreeWayComparison = new ThreeWayComparison(
            new ThreeWayMetric(0.55m, 0m, null),
            new ThreeWayMetric(12.5m, 0m, null),
            new ThreeWayMetric(0.08m, 0m, null),
            new ThreeWayMetric(120m, 0m, null),
            "③ 執行の差が勝率に出ている。"),
    };

    // 種別ごとの散文用途（ReportNarrativePurpose は Application 層のため、ここでは同値を写す）。
    private static string ReportNarrativePurposeOf(ReportKind kind) => kind switch
    {
        ReportKind.Weekly => LlmPurposes.ReportWeekly,
        ReportKind.Monthly => LlmPurposes.ReportMonthly,
        _ => LlmPurposes.ReportDaily,
    };

    [Theory]
    [InlineData(ReportKind.Daily, "daily-unsupplied.md")]
    [InlineData(ReportKind.Weekly, "weekly-unsupplied.md")]
    [InlineData(ReportKind.Monthly, "monthly-unsupplied.md")]
    public void 供給が無い場合のテンプレート出力を固定する(ReportKind kind, string goldenFile)
    {
        AssertGolden(ReportRenderer.RenderMarkdown(Bare(kind)), goldenFile);
    }

    [Theory]
    [InlineData(ReportKind.Daily, "daily-supplied.md")]
    [InlineData(ReportKind.Weekly, "weekly-supplied.md")]
    [InlineData(ReportKind.Monthly, "monthly-supplied.md")]
    public void 供給がある場合のテンプレート出力を固定する(ReportKind kind, string goldenFile)
    {
        AssertGolden(ReportRenderer.RenderMarkdown(Supplied(kind)), goldenFile);
    }

    // 🔴 **ゴールデンが存在しないことを緑で通さない。** ファイルが消えた／複写されなかった場合に
    // 「比較対象が無いので合格」になると、テンプレートの退行を検知するという目的そのものが失われる。
    //
    // 環境変数 `UPDATE_GOLDEN=1` で**ソースツリー側**のゴールデンを実出力で更新する（意図した変更のとき用）。
    // 🔴 更新モードでも**必ず失敗させる**——更新した実行がそのまま緑になると、
    // 「差分を見ずに上書きして通した」ことに誰も気づけない。人が差分をレビューしてから再実行する。
    private static void AssertGolden(string actual, string goldenFile)
    {
        var normalized = actual.ReplaceLineEndings("\n");

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var sourcePath = Path.Combine(Path.GetDirectoryName(ThisFile())!, "Golden", goldenFile);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, normalized);
            Assert.Fail($"UPDATE_GOLDEN=1 のため {goldenFile} を更新した。差分をレビューし、環境変数を外して再実行すること。");
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Golden", goldenFile);

        File.Exists(path).Should().BeTrue(
            $"ゴールデンファイル {goldenFile} が存在しません（比較対象が無い状態で緑にしない）。実出力:\n{actual}");

        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");

        normalized.Should().Be(expected,
            $"報告書テンプレートの出力が {goldenFile} と一致しません。"
            + "意図した変更なら UPDATE_GOLDEN=1 で更新し、差分をレビューすること。");
    }

    // ソースツリーの位置（更新モードでのみ使う）。ビルド時に埋め込まれる。
    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
