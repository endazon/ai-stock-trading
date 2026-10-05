using AiStockTrading.Shared.Contracts.Events;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace TradeDecisionService.Infrastructure.Steps;

// FR-02, NFR-02, #1169, IADR-0490 決定1: 定時サイクルのハンドラ（InformationCollected）の実行時間の上限を、
// DI の ScheduledCycleBudget（ハンドラが銘柄ごとの締め切りに使うのと同じ値）から設定する。
//
// Wolverine は上限を明示しないハンドラに既定 60 秒（WolverineOptions.DefaultExecutionTimeout）を掛ける。
// チェーンの上限（HandlerChain.ExecutionTimeoutInSeconds）は受信の実行器を組むとき（Executor.Build）に読まれ、
// 生成コードには入らない。したがって稼働イメージの事前生成コード（TypeLoadMode.Static）でも本ポリシーは効く
// （ポリシーは HandlerGraph.Compile で読み込み方式に依らず適用される）。
// 予算を DI から読むのは、ハンドラと上限が別々の構成の読み方をして食い違わないようにするため（単一の出所）。
public sealed class ScheduledCycleTimeoutPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(container);

        var budget = container.Services.GetRequiredService<ScheduledCycleBudget>();
        foreach (var chain in chains)
        {
            if (chain.MessageType == typeof(InformationCollected))
                chain.ExecutionTimeoutInSeconds = budget.HandlerTimeoutSeconds;
        }
    }
}
