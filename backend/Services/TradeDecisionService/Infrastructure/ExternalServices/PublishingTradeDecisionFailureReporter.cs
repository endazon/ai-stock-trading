using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// 🔴 NFR, FR-04, FR-11, #1111, IADR-0483 決定1〜3: 取引判断の最中の例外の最終の失敗を TradeDecisionFailed として発行する。
//
// 🔴 **発行はランタイムの MessageBus から行う**（Program.cs が `new MessageBus(IWolverineRuntime)` を渡す）。価格変動の経路は
// この報告の直後に例外を投げ直してハンドラを失敗で終える。ハンドラの IMessageBus（scoped）で出すと、その処理中に発行した
// メッセージとして捨てられる（IADR-0129）。発行の口は委譲で受け取る（PositionQueryHealthReporter と同じ形。試験で差し替える）。
//
// 🔴 **例外から取るのは型名だけ**（裁定 2026-10-02）。メッセージ・スタック・内側の例外は秘密情報や口座 ID を含み得るので、
// 事実にもログにも載せない（発行の失敗のログにも元の例外を渡さない）。
// 🔴 **例外を投げない**（呼び出し元の挙動を変えない）。発行の失敗は警告のログに残すだけで、出し直さない（再送の状態を持たない）。
public sealed class PublishingTradeDecisionFailureReporter(
    Func<TradeDecisionFailed, ValueTask> publish,
    IClock clock,
    ILogger<PublishingTradeDecisionFailureReporter> logger) : ITradeDecisionFailureReporter
{
    public async Task ReportFinalFailureAsync(string cycleTrigger, string symbol, Market market, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var failed = new TradeDecisionFailed(
            Guid.NewGuid(), symbol, market, cycleTrigger, ExceptionTypeName(exception), clock.UtcNow);
        try
        {
            await publish(failed).ConfigureAwait(false);
            logger.LogInformation(
                "取引判断の最中の例外の最終の失敗を発行（監査台帳へ記録）: {Symbol}/{Market} 起点={CycleTrigger} 型={ExceptionType}",
                failed.Symbol, failed.Market, failed.CycleTrigger, failed.ExceptionType);
        }
        catch (Exception publishError)
        {
            logger.LogWarning(
                "取引判断の最中の例外の最終の失敗を発行できませんでした（台帳に残りません）: {Symbol}/{Market} 起点={CycleTrigger} 型={ExceptionType}"
                + " 発行の失敗の型={PublishErrorType}",
                failed.Symbol, failed.Market, failed.CycleTrigger, failed.ExceptionType, ExceptionTypeName(publishError));
        }
    }

    /// <summary>
    /// #1111, IADR-0483 決定1: 台帳へ載せる例外の型名。名前空間つきの型名で、総称型は定義の名前（<c>Ns.Foo`1</c>）にする
    /// （閉じた総称型の <c>FullName</c> は型引数のアセンブリ名・版まで含むため）。メッセージ・スタックは見ない。
    /// </summary>
    public static string ExceptionTypeName(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var type = exception.GetType();
        if (type.IsGenericType)
            type = type.GetGenericTypeDefinition();
        return type.FullName ?? type.Name;
    }
}
