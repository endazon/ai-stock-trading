using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR-01, ADR-0006, IADR-0129（2026-09-17 追記）, #811: ハンドラ生成コードの読み込み方式（TypeLoadMode）の決め方と、
// Static のときの**起動時の**表明を固定する。
//
// なぜ要るのか（#808 の実測）: 既定の Dynamic はメッセージ型ごとに 1 通目の受信時に Roslyn を走らせ、その作業メモリが
// glibc malloc に残って 512Mi 容器を OOMKilled にした。稼働イメージはビルド段で `codegen write` を通し Static で読む。
// ただし Wolverine のチェーン組み立ては遅延なので、Static にしただけでは生成コードの無いイメージが「起動・readiness・
// キュー宣言・consumer 接続はすべて成功したまま、メッセージだけを処理しない」形で静かに壊れる。共通ヘルパは
// Static のとき起動時に全生成型の存在を表明し、欠けていれば起動を失敗させる。
[Collection(ProcessEnvironmentCollection.Name)]
public class WolverineTypeLoadModeTests
{
    private const string ServiceName = "ai-stock-trading.type-load-mode-probe-service";

    // 解決順: 環境変数 ＞ 生成コードの有無 ＞ Dynamic。
    [Theory]
    [InlineData(null, false, TypeLoadMode.Dynamic)]
    [InlineData("", false, TypeLoadMode.Dynamic)]
    [InlineData("   ", false, TypeLoadMode.Dynamic)]
    [InlineData(null, true, TypeLoadMode.Static)]
    [InlineData("Static", false, TypeLoadMode.Static)]
    [InlineData("static", false, TypeLoadMode.Static)]
    [InlineData(" STATIC ", false, TypeLoadMode.Static)]
    [InlineData("Dynamic", true, TypeLoadMode.Dynamic)]
    [InlineData("Auto", false, TypeLoadMode.Auto)]
    public void 読み込み方式は環境変数と生成コードの有無から決まる(string? configured, bool preGenerated, TypeLoadMode expected)
    {
        WolverineExtensions.ResolveTypeLoadMode(configured, preGenerated).Should().Be(expected);
    }

    // 不正な値を黙って Dynamic に倒すと、実行時コンパイルを止めたつもりのイメージが静かに Roslyn を積む（#808 の再発）。
    [Theory]
    [InlineData("Production")]
    [InlineData("true")]
    [InlineData("Statik")]
    public void 環境変数が不正なら起動時に止める(string configured)
    {
        var act = () => WolverineExtensions.ResolveTypeLoadMode(configured, preGeneratedCodePresent: false);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*{WolverineExtensions.TypeLoadModeVariable}*")
            .WithMessage($"*{configured}*");
    }

    // 生成コードの有無は codegen write が必ず書く登録簿型で判定する。リポジトリには生成物をコミットしないので、
    // テストアセンブリも Wolverine 本体も「無し」である（＝dev/test は Dynamic のまま）。
    [Fact]
    public void 生成コードを持たないアセンブリは_生成物なし_と判定される()
    {
        WolverineExtensions.HasPreGeneratedHandlerCode(typeof(WolverineTypeLoadModeTests).Assembly).Should().BeFalse();
        WolverineExtensions.HasPreGeneratedHandlerCode(typeof(WolverineOptions).Assembly).Should().BeFalse();
    }

    // 既定（env なし・生成物なし）の共通配線は Dynamic を選ぶ（既存の WolverineHandlerCodegenTests が実際に動く前提）。
    // #811（PR #814 の監査指摘）: 共通配線は実プロセスの環境変数を読むので、テストを実行する環境に
    // WOLVERINE_TYPE_LOAD_MODE が在ると結果が変わる。テストの間だけ未設定にして元へ戻し、周囲の環境に依らせない
    // （書き換えが並列の他テストへ漏れないよう、本クラスは並列化しないコレクションで走らせる）。
    [Fact]
    public void 共通配線の既定は_Dynamic_である()
    {
        var ambient = Environment.GetEnvironmentVariable(WolverineExtensions.TypeLoadModeVariable);
        Environment.SetEnvironmentVariable(WolverineExtensions.TypeLoadModeVariable, null);
        try
        {
            var options = new WolverineOptions();

            options.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");

            options.CodeGeneration.TypeLoadMode.Should().Be(TypeLoadMode.Dynamic);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WolverineExtensions.TypeLoadModeVariable, ambient);
        }
    }

    // 呼び出し側が先に決めた方式は上書きしない（JasperFx の TypeLoadModeHasChanged と同じ意味）。
    [Fact]
    public void 呼び出し側の明示設定は共通配線に上書きされない()
    {
        var options = new WolverineOptions();
        options.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

        options.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");

        options.CodeGeneration.TypeLoadMode.Should().Be(TypeLoadMode.Static);
    }

    // 🔴 本件の要: Static なのに生成コードが無ければ**起動時に**落ちる（1 通目の受信時ではない）。
    // codegen 段を通していないイメージ・ハンドラを足したのに再生成していないイメージを、Pod の起動失敗として表面化させる。
    [Fact]
    public async Task 生成コードの無いアセンブリを_Static_で起動すると起動時に失敗する()
    {
        var builder = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // 稼働イメージの WOLVERINE_TYPE_LOAD_MODE=Static と同じ状態（プロセス環境変数を触らずに固定する）。
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            });

        var act = async () =>
        {
            using var host = builder.Build();
            await host.StartAsync();
        };

        // テストアセンブリは Wolverine のハンドラ（TypeLoadModeProbeHandler）を持つが生成コードを持たないため、
        // 表明が欠けたコードファイルを名指しして起動を失敗させる。
        await act.Should().ThrowAsync<MissingTypeException>()
            .WithMessage($"*{nameof(TypeLoadModeProbeEvent)}*");
    }
}

// #811: プロセス環境変数を一時的に書き換えるテストを、他のテストと並列に走らせないためのコレクション。
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "process-environment";
}

// 検証用のイベントとハンドラ（Shared.Contracts へ依存させないため本テストプロジェクトに置く）。
// IADR-0129 決定 9: ハンドラ型は public sealed。
public record TypeLoadModeProbeEvent(string Symbol);

public sealed class TypeLoadModeProbeHandler
{
    public void Handle(TypeLoadModeProbeEvent message) => _ = message;
}
