using AiStockTrading.Backtest.Domain.Tests.Calibration;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, FR-20, ADR-0008, #208, IADR-0110: Stage 0 の試行数下限（MinTrials）較正の土台。
//
// 較正の問い: 「真のエッジが 0 の候補を N 個試して最良を選ぶ」とき、Stage 0 のエッジ有意条件
// （DSR ≥ MinDeflatedSharpe）はどれだけ誤って合格を出すか。DSR の多重検定補正（期待最大 Sharpe SR0）は
// **台帳に記録された試行数**からしか計算できないため、記録の粒度が補正の効き方を決める。
//
// 実市場データは用いない（Stooq は現在プログラム取得不可・IADR-0110）。ここで測るのは
// 「補正が働くかどうか」という統計的性質であり、真のエッジ 0 の合成標本で十分に決まる。
public class Stage0NoiseCalibrationTests
{
    private const int SampleLength = 252; // 1 年（営業日）
    private const double DsrThreshold = 0.95; // Stage0GateCriteria.Default.MinDeflatedSharpe

    [Fact]
    public void 記録試行数1では期待最大Sharpeが0になる_多重検定補正が不活性()
    {
        // これが MinTrials=1 の実害。DeflatedSharpeRatio.ExpectedMaxSharpe は trials<2 で 0 を返すため、
        // 台帳に 1 件しか無いと DSR は素の probabilistic Sharpe へ退化する（ADR-0008 が要求する補正が消える）。
        var outcome = Stage0NoiseCalibration.RunOnce(
            searchSize: 50, recordedTrials: 1, sampleLength: SampleLength,
            sampler: new DeterministicNormalSampler(7UL));

        outcome.ExpectedMaxSharpe.Should().Be(0d);
    }

    [Fact]
    public void 同じ標本でも記録試行数を増やすとDSRは下がる_補正が効く()
    {
        // 同一の探索結果に対し、台帳の粒度だけを変える。補正が効けば DSR は必ず下がる（緩む方向には動かない）。
        const int searchSize = 50;
        var underReported = Stage0NoiseCalibration.RunOnce(
            searchSize, recordedTrials: 1, SampleLength, new DeterministicNormalSampler(11UL));
        var truthful = Stage0NoiseCalibration.RunOnce(
            searchSize, recordedTrials: searchSize, SampleLength, new DeterministicNormalSampler(11UL));

        truthful.BestInSampleSharpe.Should().Be(underReported.BestInSampleSharpe); // 同じ探索結果
        truthful.ExpectedMaxSharpe.Should().BeGreaterThan(0d);
        truthful.DeflatedSharpe.Should().BeLessThan(underReported.DeflatedSharpe);
    }

    [Fact]
    public void 単一候補を正直に記録した場合の偽陽性率は名目水準付近()
    {
        // 探索そのものが 1 件なら多重検定の問題は無い。DSR は「真 Sharpe>0 の確率」なので、
        // 真のエッジ 0 に対する合格率は名目 5% 付近に収まるはず（補正の要否を測る基準線）。
        var rate = Stage0NoiseCalibration.FalsePositiveRate(
            searchSize: 1, recordedTrials: 1, sampleLength: SampleLength,
            repetitions: 4_000, seed: 101UL, dsrThreshold: DsrThreshold);

        rate.Should().BeInRange(0.03, 0.08);
    }

    [Fact]
    public void 探索を過少申告すると偽陽性率が跳ね上がる_MinTrials1の実害()
    {
        // 50 候補を試して 1 件だけ記録する＝現行の MinTrials=1 が許容する最小の台帳。
        var underReported = Stage0NoiseCalibration.FalsePositiveRate(
            searchSize: 50, recordedTrials: 1, sampleLength: SampleLength,
            repetitions: 4_000, seed: 202UL, dsrThreshold: DsrThreshold);

        underReported.Should().BeGreaterThan(0.30);
    }

    [Fact]
    public void 正直に記録すれば探索を広げても偽陽性率は抑えられる()
    {
        var truthful = Stage0NoiseCalibration.FalsePositiveRate(
            searchSize: 50, recordedTrials: 50, sampleLength: SampleLength,
            repetitions: 4_000, seed: 202UL, dsrThreshold: DsrThreshold);
        var underReported = Stage0NoiseCalibration.FalsePositiveRate(
            searchSize: 50, recordedTrials: 1, sampleLength: SampleLength,
            repetitions: 4_000, seed: 202UL, dsrThreshold: DsrThreshold);

        truthful.Should().BeLessThan(underReported);
        truthful.Should().BeLessThan(0.10);
    }

    [Fact]
    public void 真のエッジ0の性能行列ではPBOが05付近に集まる_閾値05は雑音の中心()
    {
        // PBO（CSCV）は「IS 最良が OOS で中央値以下へ落ちる確率」。真のエッジが 0 なら IS 最良の OOS 順位は
        // 一様になるため PBO の中心は 0.5 になる。つまり閾値 0.5 は**雑音の中心そのもの**で、
        // この条件単独では雑音を約半分通す。MaxProbabilityOfOverfitting の水準判断の土台。
        var summary = Stage0NoiseCalibration.NoisePboDistribution(
            strategies: 10, blocks: 8, partitions: 8, blockLength: 63,
            repetitions: 300, seed: 404UL, threshold: 0.50);

        summary.Mean.Should().BeInRange(0.35, 0.65);
        summary.ShareWithinThreshold.Should().BeInRange(0.30, 0.70);
    }

    [Fact]
    public void 記録試行数2は期待最大Sharpeの推定が不安定_下限を2に置けない根拠()
    {
        // N=2 では IS Sharpe の標本分散が 1 自由度しか無く、SR0 の推定が回ごとに大きく振れる。
        // 「補正が計算できる」ことと「補正が信頼できる」ことは別であり、MinTrials の下限は後者で決める。
        var two = Stage0NoiseCalibration.ExpectedMaxSharpeDispersion(
            searchSize: 2, sampleLength: SampleLength, repetitions: 2_000, seed: 303UL);
        var twenty = Stage0NoiseCalibration.ExpectedMaxSharpeDispersion(
            searchSize: 20, sampleLength: SampleLength, repetitions: 2_000, seed: 303UL);

        // 相対ばらつき（変動係数）が N=2 では大きく、N=20 で明確に縮む。
        two.RelativeDispersion.Should().BeGreaterThan(twenty.RelativeDispersion);
    }
}
