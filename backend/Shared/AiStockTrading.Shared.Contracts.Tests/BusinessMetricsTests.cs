using System.Reflection;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// NFR-07, NFR-13, #287, IADR-0255: 業務メトリクスの計器が**実際に値を刻む**ことと、
// 名前レジストリ（BusinessMetricNames）が**実装と一致している**ことを固定する。
//
// 🔴 本クラスだけが本アセンブリで BusinessMetrics を使うため、否定形（「動かないこと」）を安全に表明できる。
// Meter はプロセス全体で観測されるため、他のテストクラスが同じ Meter へ書くと否定形は壊れる。
// サービス側のハンドラテストでは肯定形（Contain）だけを書き、否定形はここへ集約する。
public class BusinessMetricsTests
{
    /// <summary>レジストリが宣言する計器名（タグ名を除く）。命名規約 `ast.` で機械的に選り分ける。</summary>
    private static IReadOnlyList<string> RegisteredInstrumentNames() =>
        [.. typeof(BusinessMetricNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => v.StartsWith("ast.", StringComparison.Ordinal))
            .OrderBy(v => v, StringComparer.Ordinal)];

    // 🔴 **レジストリと実装が食い違うと、ダッシュボードは「値が来ない＝0 件で正常」に見える。**
    // 名前は 1 か所（BusinessMetricNames）にしか無いが、`CreateCounter` に別の文字列を直書きすれば
    // 食い違いは作れる。本テストは**実際に発火した計器名の集合**をレジストリと突き合わせる。
    [Fact]
    public void 実際に発火する計器名はレジストリの宣言と一致する()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();

        RecordEveryInstrument(metrics);

        var fired = capture.Measurements
            .Select(m => m.InstrumentName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        fired.Should().BeEquivalentTo(
            RegisteredInstrumentNames(),
            "レジストリの宣言と実際の計器名が食い違うと、ダッシュボードは値の来ないパネルを正常に見せる");
    }

    [Fact]
    public void Meter名はレジストリの宣言と一致する()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();

        metrics.RecordInformationCollected(1);

        // MeterCapture は Meter 名で購読するため、名前が違えば 1 件も捕まらない。
        capture.Measurements.Should().NotBeEmpty();
    }

    // FR-01, FR-02: 収集件数は 0 件でも 1 件の測定値として出る（「回って 0 件」と「止まっている」の区別）。
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(250)]
    public void 収集件数は与えた値のまま計上される(int itemCount)
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();

        metrics.RecordInformationCollected(itemCount);

        capture.SumOf(BusinessMetricNames.InformationItemsCollected).Should().Be(itemCount);
    }

    // FR-04: 判断の内訳（Buy / Sell / 見送り）が action タグで読み分けられる。
    [Theory]
    [InlineData(TradeSide.Buy, "buy")]
    [InlineData(TradeSide.Sell, "sell")]
    [InlineData(null, BusinessMetrics.ActionNoTrade)]
    public void 判断の内訳は_action_タグで読み分けられる(TradeSide? side, string expected)
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();

        metrics.RecordTradeDecision(BusinessMetrics.TriggerScheduled, side);

        capture.TagValuesOf(BusinessMetricNames.TradeCycleDecisions, BusinessMetricNames.TagAction)
            .Should().Equal(expected);
        capture.TagValuesOf(BusinessMetricNames.TradeCycleDecisions, BusinessMetricNames.TagTrigger)
            .Should().Equal(BusinessMetrics.TriggerScheduled);
    }

    // FR-10, FR-19（境界値テーブル）: 拒否理由の列挙は**全要素**が 1 件ずつ計上される。
    // 1 注文に複数の統制が同時に効き得るため、先頭 1 件だけを数えると内訳が過少になる。
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void 拒否理由は列挙の全要素が計上される(int reasonCount)
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();

        var reasons = Enum.GetValues<RejectionReason>().Take(reasonCount).ToArray();
        metrics.RecordOrderScreening(approved: false, reasons);

        capture.ValuesOf(BusinessMetricNames.RiskRejections).Should().HaveCount(reasonCount);
        capture.TagValuesOf(BusinessMetricNames.RiskRejections, BusinessMetricNames.TagReason)
            .Should().BeEquivalentTo(reasons.Select(r => r.ToString()));
    }

    // FR-10, FR-19（プロパティベース）: **どの拒否理由でも**計上は 1 件・タグは列挙名そのものである。
    // 語彙が増えたときに既定値へ黙って落ちる（＝内訳が欠ける）ことが無いことを、全要素で確かめる。
    [Fact]
    public void すべての拒否理由が固有のタグ値として計上される()
    {
        foreach (var reason in Enum.GetValues<RejectionReason>())
        {
            using var capture = new MeterCapture(BusinessMetricNames.MeterName);
            using var metrics = new BusinessMetrics();

            metrics.RecordOrderScreening(approved: false, [reason]);

            capture.TagValuesOf(BusinessMetricNames.RiskRejections, BusinessMetricNames.TagReason)
                .Should().Equal(reason.ToString());
        }
    }

    // FR-10（否定形）: **承認では拒否カウンタが動かない。**
    // 動くと「統制違反の件数」が承認のたびに水増しされ、統制が効いているかの判断を誤らせる。
    [Fact]
    public void 承認では拒否カウンタが動かない()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();

        metrics.RecordOrderScreening(approved: true, []);

        capture.ValuesOf(BusinessMetricNames.RiskRejections).Should().BeEmpty();
        // 対の肯定形: 承認でも審査回数は数える（数えないと「違反 0 件」と「審査が動いていない」が同じ形になる）。
        capture.TagValuesOf(BusinessMetricNames.RiskScreenings, BusinessMetricNames.TagOutcome)
            .Should().Equal(BusinessMetrics.OutcomeApproved);
    }

    // FR-05（否定形）: 発注結果と見送りは**別の計器**である。見送りを記録しても発注結果は動かない。
    [Fact]
    public void 発注見送りは発注結果の計器を動かさない()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();

        metrics.RecordOrderDispatchForgone(OrderDispatchForgoneReason.BrokerUnavailable);

        capture.ValuesOf(BusinessMetricNames.OrderExecutions).Should().BeEmpty(
            "見送りは注文がブローカーへ届いていない状態であり、注文状態を持たない");
        capture.TagValuesOf(BusinessMetricNames.OrderDispatchForgone, BusinessMetricNames.TagReason)
            .Should().Equal(nameof(OrderDispatchForgoneReason.BrokerUnavailable));
    }

    // FR-05: 発注結果は status と provider の 2 タグで読み分けられる（Stage 1 の算入対象は provider で決まる）。
    [Fact]
    public void 発注結果は_status_と_provider_のタグを持つ()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();

        metrics.RecordOrderExecuted(OrderStatus.Filled, BrokerProvider.MoomooSimulate);

        var measurement = capture.ValuesOf(BusinessMetricNames.OrderExecutions).Should().ContainSingle().Which;
        measurement.Tags[BusinessMetricNames.TagStatus].Should().Be(nameof(OrderStatus.Filled));
        measurement.Tags[BusinessMetricNames.TagProvider].Should().Be(nameof(BrokerProvider.MoomooSimulate));
    }

    // NFR-13（境界値テーブル）: 上限消費率は与えた値をそのまま最終観測値として持つ（80/100 がしきい値）。
    [Theory]
    [InlineData(0)]
    [InlineData(79.99)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(133.34)]
    public void 上限消費率は与えた値をそのまま計上する(double percent)
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();

        metrics.RecordLlmCost(nameof(CostCategoryLabels.Llm), 10m, (decimal)percent);

        capture.ValuesOf(BusinessMetricNames.LlmCostLimitRatioPercent)
            .Should().ContainSingle().Which.Value.Should().BeApproximately(percent, 0.001);
    }

    // FR-01, ADR-0031（計画）決定2〜3, IADR-0292: Finnhub 日次要求見積りと暫定上限に対する比率の計上。
    [Fact]
    public void Finnhub日次要求見積りは与えた値をそのまま計上する()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();

        metrics.RecordFinnhubDailyVolumeEstimate(estimatedDailyRequests: 480, limitRatioPercent: 160);

        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate)
            .Should().ContainSingle().Which.Value.Should().Be(480);
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeLimitRatioPercent)
            .Should().ContainSingle().Which.Value.Should().Be(160);
    }

    /// <summary>本テスト内でのみ用いる費用カテゴリの表示名（CostControl の enum は別プロジェクトにある）。</summary>
    private static class CostCategoryLabels
    {
        public const string Llm = nameof(Llm);
    }

    /// <summary>レジストリの全計器を 1 回ずつ発火させる（「宣言はあるが誰も発火させない計器」を検出するため）。</summary>
    private static void RecordEveryInstrument(BusinessMetrics metrics)
    {
        metrics.RecordInformationCollected(1);
        metrics.RecordTradeDecision(BusinessMetrics.TriggerScheduled, TradeSide.Buy);
        metrics.RecordTradeDecisionDuration(BusinessMetrics.TriggerScheduled, 12.5);
        metrics.RecordOrderScreening(approved: false, [RejectionReason.KillSwitchActive]);
        metrics.RecordOrderExecuted(OrderStatus.Filled, BrokerProvider.InternalPaper);
        metrics.RecordOrderDispatchForgone(OrderDispatchForgoneReason.BrokerUnavailable);
        metrics.RecordLlmCost(nameof(CostCategoryLabels.Llm), 100m, 5m);
        metrics.RecordFinnhubDailyVolumeEstimate(estimatedDailyRequests: 480, limitRatioPercent: 160);
    }
}
