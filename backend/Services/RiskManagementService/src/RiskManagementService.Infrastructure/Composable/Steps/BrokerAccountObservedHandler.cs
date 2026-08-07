using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: 口座種別の観測（BrokerAccountObserved）を購読し、
// 判定コアが読むストアへ記録する。
//
// **本ハンドラは判定をしない。** 設定値との突き合わせ（決定3 の食い違い検知）も、現金口座で加わる統制も、
// すべて RiskEvaluator（純関数）が行う。ここは供給経路だけを担う。
//
// ADR-0013, IADR-0129 決定10: 再試行で同じ観測が再配送され得るが、ストアが逆行する観測を無視するため冪等である。
public sealed class BrokerAccountObservedHandler(
    IBrokerAccountObservationStore observations,
    ILogger<BrokerAccountObservedHandler> logger)
{
    public void Handle(BrokerAccountObserved message)
    {
        ArgumentNullException.ThrowIfNull(message);

        observations.Record(message.Account, message.ObservedAt);

        // #425, ADR-0025 決定2: GFV 発生回数は本観測に**含まれない**（ブローカーが供給できない）。
        // 自前計数（IGoodFaithViolationStore）が別経路で供給する。
        logger.LogInformation(
            "口座種別を観測しました: 発注先={Provider} 種別={AccountType} 決済済み資金={SettledCash} 観測時刻={ObservedAt}",
            message.Provider,
            message.Account.AccountType,
            message.Account.SettledCashInBase,
            message.ObservedAt);
    }
}
