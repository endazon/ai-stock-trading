using AiStockTrading.Backtest.Domain.Tests.Calibration;
using Xunit;
using Xunit.Abstractions;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, FR-20, ADR-0008, #208, IADR-0109: IADR-0109 に記録した較正表を再生成する実行。
//
// 反復回数が多く実行時間が長いため CI では走らせない（既定はスキップ）。較正値を再確認・更新するときだけ
// 環境変数で明示的に有効化する:
//
//   STAGE0_CALIBRATION_REPORT=1 dotnet test backend/Services/BacktestService/tests/BacktestService.Domain.Tests \
//     --filter FullyQualifiedName~Stage0CalibrationReportTests -l "console;verbosity=detailed"
//
// 出力は決定論的（種を固定した splitmix64）。同一ランタイムであれば同じ数値が再現する。
public class Stage0CalibrationReportTests(ITestOutputHelper output)
{
    private const string EnableVariable = "STAGE0_CALIBRATION_REPORT";
    private const int SampleLength = 252;
    private const double DsrThreshold = 0.95;
    private const int Repetitions = 20_000;
    private const int DispersionRepetitions = 5_000;

    private const int UnderReportingSearchSize = 200;
    private const int PboRepetitions = 2_000;

    private const double EdgedAnnualSharpe = 1.0;

    private static readonly double[] PboThresholds = [0.50, 0.40, 0.30, 0.25, 0.20, 0.10];

    private static readonly int[] SearchSizes = [1, 2, 5, 10, 20, 50, 100, 200];
    private static readonly int[] RecordedCounts = [1, 2, 5, 10, 20, 50, 100, 200];

    [Fact]
    public void 較正表を再生成する()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            output.WriteLine($"{EnableVariable}=1 が未設定のためスキップします（既定・CI では走らせない）。");
            return;
        }

        output.WriteLine($"標本長={SampleLength} 営業日 / DSR 閾値={DsrThreshold} / 反復={Repetitions}");
        output.WriteLine("");
        output.WriteLine("| 探索候補数 N | 偽陽性率（記録1件＝過少申告） | 偽陽性率（N件を正直に記録） |");
        output.WriteLine("| --- | --- | --- |");
        foreach (var searchSize in SearchSizes)
        {
            var underReported = Stage0NoiseCalibration.FalsePositiveRate(
                searchSize, recordedTrials: 1, SampleLength, Repetitions, seed: 20260728UL, DsrThreshold);
            var truthful = Stage0NoiseCalibration.FalsePositiveRate(
                searchSize, recordedTrials: searchSize, SampleLength, Repetitions, seed: 20260728UL, DsrThreshold);
            output.WriteLine($"| {searchSize} | {underReported:P2} | {truthful:P2} |");
        }

        output.WriteLine("");
        output.WriteLine($"探索 {UnderReportingSearchSize} 件を一部だけ記録した場合の偽陽性率（下限が被害をどこまで抑えるか）");
        output.WriteLine("| 記録試行数 | 偽陽性率 |");
        output.WriteLine("| --- | --- |");
        foreach (var recorded in RecordedCounts)
        {
            var rate = Stage0NoiseCalibration.FalsePositiveRate(
                UnderReportingSearchSize, recorded, SampleLength, Repetitions, seed: 20260728UL, DsrThreshold);
            output.WriteLine($"| {recorded} | {rate:P2} |");
        }

        output.WriteLine("");
        output.WriteLine($"真のエッジ 0 の性能行列に対する PBO の分布（戦略10・区間8・分割8・区間長63・反復={PboRepetitions}）");
        var pbo = Stage0NoiseCalibration.NoisePboDistribution(
            strategies: 10, blocks: 8, partitions: 8, blockLength: 63,
            repetitions: PboRepetitions, seed: 20260728UL, threshold: 0.50);
        output.WriteLine($"平均={pbo.Mean:F4} / 中央値={pbo.Median:F4}");
        output.WriteLine($"対照: 戦略0 に年率 Sharpe {EdgedAnnualSharpe:F1} のエッジを注入した場合の合格率（偽陰性の代償）");
        output.WriteLine("| PBO 閾値 | 雑音が通る割合 | 既知エッジが通る割合 |");
        output.WriteLine("| --- | --- | --- |");
        foreach (var threshold in PboThresholds)
        {
            var noise = Stage0NoiseCalibration.NoisePboDistribution(
                strategies: 10, blocks: 8, partitions: 8, blockLength: 63,
                repetitions: PboRepetitions, seed: 20260728UL, threshold: threshold);
            var edged = Stage0NoiseCalibration.NoisePboDistribution(
                strategies: 10, blocks: 8, partitions: 8, blockLength: 63,
                repetitions: PboRepetitions, seed: 20260728UL, threshold: threshold,
                annualSharpeOfFirstStrategy: EdgedAnnualSharpe);
            output.WriteLine($"| ≤ {threshold:F2} | {noise.ShareWithinThreshold:P2} | {edged.ShareWithinThreshold:P2} |");
        }

        output.WriteLine("");
        output.WriteLine($"期待最大 Sharpe（補正項 SR0）の推定ばらつき（正直に記録・反復={DispersionRepetitions}）");
        output.WriteLine("| 記録試行数 N | SR0 平均 | SR0 標準偏差 | 変動係数 |");
        output.WriteLine("| --- | --- | --- | --- |");
        foreach (var searchSize in SearchSizes.Where(n => n >= 2))
        {
            var dispersion = Stage0NoiseCalibration.ExpectedMaxSharpeDispersion(
                searchSize, SampleLength, DispersionRepetitions, seed: 20260728UL);
            output.WriteLine(
                $"| {searchSize} | {dispersion.Mean:F4} | {dispersion.StandardDeviation:F4} | {dispersion.RelativeDispersion:P1} |");
        }
    }
}
