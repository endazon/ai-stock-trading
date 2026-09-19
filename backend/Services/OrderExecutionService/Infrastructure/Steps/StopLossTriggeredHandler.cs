using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using Wolverine;

namespace OrderExecutionService.Infrastructure.Steps;

// FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #820, IADR-0344 決定4・決定9: 市場監視の損切りライン到達（StopLossTriggered）を購読し、
// **ソフトウェア逆指値（S1）の建玉だけを**成行で決済する。S0（ブローカー側逆指値）・S2（免除）の建玉には何もしない
// ——保護記録（手法）を持つのは発注執行だけであり、突き合わせは Active な S1 の行に限る（SoftwareStopExecutor）。
//
// 市場監視は価格が戻るまで毎巡回（既定 60 秒）同じ到達を発行し、メッセージは再配送され得る。二重決済の防止は
// SoftwareStopExecutor の固定 DecisionId・予約・行の完了が担い、本ハンドラは発行だけを行う。
//
// キューは ai-stock-trading.order-execution-service.StopLossTriggered（IADR-0129 の共通ヘルパ。durable）。
// 停止中に発行された到達は再開後に届く。IADR-0129 決定 9 によりハンドラ型は public sealed とする。
public sealed class StopLossTriggeredHandler(
    SoftwareStopExecutor executor,
    ILogger<StopLossTriggeredHandler> logger)
{
    public async Task Handle(StopLossTriggered message, IMessageBus bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        var result = await executor.OnTriggeredAsync(message, cancellationToken).ConfigureAwait(false);

        foreach (var evt in result.Events)
            await bus.PublishAsync(evt).ConfigureAwait(false);

        if (result.Matched > 0)
        {
            logger.LogInformation(
                "損切りライン到達を処理（ソフトウェア逆指値）: {Symbol}/{Market} 候補 {Candidates} 件・到達 {Matched} 件・据え置き {Deferred} 件・発行 {Events} 件",
                message.Symbol, message.Market, result.Candidates, result.Matched, result.Deferred, result.Events.Count);
        }
    }
}
