extern alias RiskManagementWorker;

using BacktestService.Domain;
using BacktestService.Features.Backtest.EvaluateStage0Gate;
using RiskManagementWorker::RiskManagementService.Domain;
using AwesomeAssertions;
using Xunit;

namespace BacktestService.Tests;

// FR-15, FR-20, ADR-0008, #208, IADR-0110: Stage 0 合格基準の既定値を較正結果として固定する。
// 較正の手順と実測値は IADR-0110（再生成は Stage0CalibrationReportTests）。
public class Stage0GateCriteriaTests
{
    [Fact]
    public void 最小試行数の既定は20_多重検定補正が効く水準()
    {
        // IADR-0110: 200 候補を探索して下限ぶんだけ記録した最悪ケースでも、真のエッジ 0 が
        // DSR 0.95 を通す割合は 0.62%（記録 1 件なら 100%・2 件でも 57.20%）。
        // かつ補正項 SR0 の推定変動係数が 16.3% まで下がり、補正が「計算できる」だけでなく安定する。
        Stage0GateCriteria.Default.MinTrials.Should().Be(20);
    }

    [Fact]
    public void 最小試行数は2以上でなければ補正が消える_構造的下限()
    {
        // DeflatedSharpeRatio.ExpectedMaxSharpe は trials<2 で 0 を返す＝多重検定補正が恒等的に消える。
        // 下限は必ずこの構造的境界より上に置く（IADR-0110 決定 1）。
        Stage0GateCriteria.Default.MinTrials.Should().BeGreaterThan(1);
        DeflatedSharpeRatio.ExpectedMaxSharpe(varianceOfTrialSharpes: 0.25, trials: 1).Should().Be(0d);
        DeflatedSharpeRatio.ExpectedMaxSharpe(varianceOfTrialSharpes: 0.25, trials: 2).Should().BeGreaterThan(0d);
    }

    // FR-15, ADR-0039 決定2, #777, IADR-0337 決定2: **下限の正本は計画へ移った。実装は値を動かさない。**
    // 既定値と公開定数が同値であることを固定し、片方だけ書き換える是正漏れを止める。
    [Fact]
    public void 最小試行数の正本は計画でありDefaultと公開定数が同値である()
    {
        Stage0GateCriteria.MinTrialsDefault.Should().Be(20);
        Stage0GateCriteria.Default.MinTrials.Should().Be(
            Stage0GateCriteria.MinTrialsDefault,
            "ADR-0039 決定2: 値の正本は計画である。変更が要るなら計画へ環流する（実装で動かさない）");
    }

    // FR-15, ADR-0039 決定1, #777, IADR-0337 決定3: **PBO の評価を始める試行数（2）は構成へ出さない。**
    // 構造的な境界（trials<2 で補正項が 0・CSCV は候補 2 本以上を要求）から来る値であり、較正値ではない。
    [Fact]
    public void PBOの評価を始める試行数は2であり構造的境界と一致する()
    {
        Stage0GateService.MinTrialsForPbo.Should().Be(2);
        DeflatedSharpeRatio.ExpectedMaxSharpe(varianceOfTrialSharpes: 0.25, trials: 1).Should().Be(0d);
        // 下限 20 は PBO の評価を始める試行数より上にある（門の順序が入れ替わらない）。
        Stage0GateCriteria.MinTrialsDefault.Should().BeGreaterThan(Stage0GateService.MinTrialsForPbo);
    }

    [Fact]
    public void 較正で変更しない閾値は据え置く()
    {
        // IADR-0110 決定 2/3: DSR 0.95 は名目 5% 水準として実測と整合（単一試行の偽陽性率 5.06%）。
        // PBO 0.5 は雑音の中心（平均 0.5055）だが、厳格化しても既知エッジを同程度に落とすため据え置く。
        Stage0GateCriteria.Default.MinDeflatedSharpe.Should().Be(0.95);
        Stage0GateCriteria.Default.MaxProbabilityOfOverfitting.Should().Be(0.50);
    }

    // FR-15, FR-20, ADR-0018 決定2, #333（#306 吸収）, IADR-0138:
    // **退行防止テスト**。0.15（ADR-0008 の旧レンジ「10〜15%」の上限側）へ戻す変更を検知する。
    // 0.15 のままだと Stage 0 は運用の DD 停止ライン（10%）より 5 ポイント緩い戦略を合格させ得る——
    // 検証で通した戦略が運用開始と同時に停止条件へ抵触するという、ゲートとして倒錯した状態になる。
    [Fact]
    public void Stage0の最大DD許容値は10パーセントであり運用の停止ラインと同値である()
    {
        Stage0GateCriteria.Default.MaxDrawdownTolerance.Should().Be(
            0.10m,
            "ADR-0018 決定2: 検証段階だからといって意図的に緩めない。0.15 は旧レンジからの逆算であり退行である");

        // 運用の DD 停止ライン（FR-10・05_trading-assumptions §5）と**同値**であることを固定する。
        // 片方だけを動かすと「検証で通した戦略が運用開始と同時に止まる」倒錯が再発する。
        Stage0GateCriteria.Default.MaxDrawdownTolerance.Should().Be(
            TradingDefaults.CreateRiskLimits().MaxDrawdownRatio,
            "Stage 0 の許容 DD と運用の DD 停止ラインは同値でなければゲートが合格の意味を失う");
    }

    // **境界値**（テスト仕様書 T-06）: 閾値ちょうど（10.0%）は合格側である（判定は超過のみ不合格）。
    [Theory]
    [InlineData(0.099, false)]
    [InlineData(0.100, false)]
    [InlineData(0.101, true)]
    public void 最大DDは閾値ちょうどまで合格する(decimal maxDrawdown, bool shouldFail)
    {
        var evaluation = new Stage0GateEvaluation(
            DeflatedSharpe: 1.0,
            Pbo: new PboVerdict.Evaluated(0.1),
            MaxDrawdown: maxDrawdown,
            DoubledCostTotalReturn: 1m,
            WalkForwardOutOfSampleReturn: 1m,
            TrialCount: 20,
            DataCutoffSatisfied: true);

        var result = Stage0GateEvaluator.Evaluate(evaluation, Stage0GateCriteria.Default);

        result.FailedChecks.Contains(Stage0GateCheck.MaxDrawdown).Should().Be(shouldFail);
        result.Passed.Should().Be(!shouldFail);
    }

    // **否定形**: 旧許容値（0.15）の範囲にある DD を持つ戦略が、規則の緩さで合格に化けないこと。
    // 0.10 超〜0.15 の帯は ADR-0018 が「不合格へ転じる」と明記した帯である（同 ADR §結果）。
    [Theory]
    [InlineData(0.11)]
    [InlineData(0.13)]
    [InlineData(0.15)]
    public void 旧許容値の帯にあるDDはもはや合格しない(decimal maxDrawdown)
    {
        var evaluation = new Stage0GateEvaluation(
            DeflatedSharpe: 1.0,
            Pbo: new PboVerdict.Evaluated(0.1),
            MaxDrawdown: maxDrawdown,
            DoubledCostTotalReturn: 1m,
            WalkForwardOutOfSampleReturn: 1m,
            TrialCount: 20,
            DataCutoffSatisfied: true);

        Stage0GateEvaluator.Evaluate(evaluation, Stage0GateCriteria.Default)
            .Passed.Should().BeFalse("0.10 超〜0.15 は ADR-0018 により不合格へ転じた帯である");
    }
}
