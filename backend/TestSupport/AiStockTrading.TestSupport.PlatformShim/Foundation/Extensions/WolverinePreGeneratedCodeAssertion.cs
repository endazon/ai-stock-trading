using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// NFR-01, ADR-0006, IADR-0129（2026-09-17 追記）, #811: TypeLoadMode.Static のとき、**起動時に**全ハンドラの生成型が
// application assembly に在ることを表明する hosted service。
//
// Wolverine のチェーン組み立ては遅延（HandlerGraph.HandlerFor → 1 通目の受信時に InitializeSynchronously）であり、
// Static で型が無いと StaticTypeLoader は ExpectedTypeMissingException を投げるが、それは起動時ではなく 1 通目である。
// つまり Static だけでは、codegen を通していないイメージが「起動・readiness・キュー宣言・consumer 接続はすべて成功
// したまま、メッセージだけを処理しない」形で静かに壊れる（IADR-0129 決定 11 と同型）。本サービスはそれを
// **Pod の起動失敗（CrashLoopBackOff・欠けたファイル名つき）**へ変える。
//
// 登録は WolverineExtensions.UseAiStockTradingRabbitMq が Static に決めたときだけ行う。UseWolverine は configure
// より前に WolverineRuntime を hosted service 登録するため、本サービスは runtime の StartAsync（HandlerGraph.Compile）
// の後に走る。副作用として全ハンドラの型が起動時に attach され、1 通目の組み立て待ちも消える。
internal sealed class WolverinePreGeneratedCodeAssertion(
    IServiceProvider services,
    ILogger<WolverinePreGeneratedCodeAssertion> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collections = services.GetServices<ICodeFileCollection>().ToArray();
        foreach (var collection in collections)
        {
            // 欠けていれば MissingTypeException（欠けたコードファイル名の一覧つき）で起動が失敗する。
            collection.AssertPreBuildTypesExist(services);
        }

        logger.LogInformation(
            "Wolverine の生成コードは {CollectionCount} 集合すべてが application assembly から静的に読み込まれた（実行時コンパイルなし）",
            collections.Length);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
