using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, FR-20, ADR-0008, #208, IADR-0109: Stage 0 合格基準の既定値を較正結果として固定する。
// 較正の手順と実測値は IADR-0109（再生成は Stage0CalibrationReportTests）。
public class Stage0GateCriteriaTests
{
    [Fact]
    public void 最小試行数の既定は20_多重検定補正が効く水準()
    {
        // IADR-0109: 200 候補を探索して下限ぶんだけ記録した最悪ケースでも、真のエッジ 0 が
        // DSR 0.95 を通す割合は 0.62%（記録 1 件なら 100%・2 件でも 57.20%）。
        // かつ補正項 SR0 の推定変動係数が 16.3% まで下がり、補正が「計算できる」だけでなく安定する。
        Stage0GateCriteria.Default.MinTrials.Should().Be(20);
    }

    [Fact]
    public void 最小試行数は2以上でなければ補正が消える_構造的下限()
    {
        // DeflatedSharpeRatio.ExpectedMaxSharpe は trials<2 で 0 を返す＝多重検定補正が恒等的に消える。
        // 下限は必ずこの構造的境界より上に置く（IADR-0109 決定 1）。
        Stage0GateCriteria.Default.MinTrials.Should().BeGreaterThan(1);
        DeflatedSharpeRatio.ExpectedMaxSharpe(varianceOfTrialSharpes: 0.25, trials: 1).Should().Be(0d);
        DeflatedSharpeRatio.ExpectedMaxSharpe(varianceOfTrialSharpes: 0.25, trials: 2).Should().BeGreaterThan(0d);
    }

    [Fact]
    public void 較正で変更しない閾値は据え置く()
    {
        // IADR-0109 決定 2/3/4: DSR 0.95 は名目 5% 水準として実測と整合（単一試行の偽陽性率 5.06%）。
        // PBO 0.5 は雑音の中心（平均 0.5055）だが、厳格化しても既知エッジを同程度に落とすため据え置く。
        // 最大DD 0.15 は計画書（05_trading-assumptions）の DD 上限由来で、実装側の自由変数ではない。
        Stage0GateCriteria.Default.MinDeflatedSharpe.Should().Be(0.95);
        Stage0GateCriteria.Default.MaxProbabilityOfOverfitting.Should().Be(0.50);
        Stage0GateCriteria.Default.MaxDrawdownTolerance.Should().Be(0.15m);
    }
}
