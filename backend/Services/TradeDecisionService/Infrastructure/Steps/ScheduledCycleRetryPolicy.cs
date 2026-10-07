using AiStockTrading.Shared.Contracts.Events;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace TradeDecisionService.Infrastructure.Steps;

// 🔴 FR-04, FR-02, NFR-13, #1194, IADR-0505: 定時サイクルのハンドラ（InformationCollected）にだけ、DI の ScheduledCycleRetryChain から
// 導いた試行の上限を与える（共通の失敗方針〔再試行 3 回 → _error〕を上書きする。他のハンドラは変えない）。
//
// Wolverine は失敗した例外に**チェーンの規則を共通の規則より先に**当てる（実測: ScheduledCycleRedeliveryTests の T-10-2404）。
// 試行の上限が 1 なら再試行せず _error へ送る（共通の失敗方針の終端と同じ）。打ち切られたサイクルの判断の発行は捨てられ、
// 次の巡回が判断し直す（IADR-0023: 銘柄ごとの失敗は従来どおりハンドラの中で分離される）。
// 連鎖を DI から読むのは、上限の導出（ScheduledCycleBudget）と同じ単一の出所にするため。DI の解決はホストの起動中
// （HandlerGraph.Compile）に走るので、連鎖が consumer_timeout に収まらない構成では**起動が止まる**。
public sealed class ScheduledCycleRetryPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(container);

        var retryChain = container.Services.GetRequiredService<ScheduledCycleRetryChain>();
        foreach (var chain in chains.Where(c => c.MessageType == typeof(InformationCollected)))
        {
            if (retryChain.Cooldowns.Count == 0)
            {
                chain.OnAnyException().MoveToErrorQueue();
            }
            else
            {
                chain.OnAnyException().RetryWithCooldown([.. retryChain.Cooldowns]).Then.MoveToErrorQueue();
            }
        }
    }
}
