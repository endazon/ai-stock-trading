using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace RiskManagementService.Infrastructure.Steps;

// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: 口座種別の観測（BrokerAccountObserved）を購読し、
// 判定コアが読むストアへ記録する。
//
// **本ハンドラは判定をしない。** 設定値との突き合わせ（決定3 の食い違い検知）も、現金口座で加わる統制も、
// すべて RiskEvaluator（純関数）が行う。ここは供給経路だけを担う。
//
// ADR-0013, IADR-0129 決定10: 再試行で同じ観測が再配送され得るが、ストアが逆行する観測を無視するため冪等である。
public sealed class BrokerAccountObservedHandler(
    IBrokerAccountObservationStore observations,
    ICapitalBaselineStore capitalBaseline,
    ILogger<BrokerAccountObservedHandler> logger)
{
    public void Handle(BrokerAccountObserved message)
    {
        ArgumentNullException.ThrowIfNull(message);

        observations.Record(message.Account, message.ObservedAt);

        // FR-10, #869, ADR-0041 決定2, IADR-0354: 同じ観測から**基準資金（equity）**も取引日ごとに畳む。
        // 🔴 **取れなかった（null）ときは書かない。** 直前の値を書き直すと「今日も照会できた」という
        // 起きていない事実を記録することになり、鮮度の検査が効かなくなる（沈黙が安全側に倒れる向きを保つ）。
        if (message.Account.EquityInBase is { } equityInBase)
        {
            capitalBaseline.Record(equityInBase, message.ObservedAt);
        }
        else
        {
            // 🔴 FR-10, #889, IADR-0372 決定B: **書かないことの帰結を名指しする。**
            // 行が書かれない＝読み出しは前取引日の正の値を返し続ける＝**鮮度（既定 4 日）が切れるまで
            // 新規建ては止まらない**。従来はこの事実が下の Information（評価額=空）に埋もれていた。
            // 🔴 **「残高 0 を観測した」と「照会できなかった」はここでは区別できない**
            // ——供給側（アダプタ）が両方を null へ畳むためである。区別するには供給側の契約を変える必要があり、
            // それは #889 の裁定の対象である（本ハンドラでは区別しないことを明記して警告する）。
            logger.LogWarning(
                "口座照会が評価額を返さなかったため、この取引日の基準資金の行は書きません "
                    + "（発注先={Provider} 観測時刻={ObservedAt}）。"
                    + "前取引日の値が鮮度切れまで使われ続けます（残高 0 と照会不能はここでは区別できません）。",
                message.Provider,
                message.ObservedAt);
        }

        // #425, ADR-0025 決定2: GFV 発生回数は本観測に**含まれない**（ブローカーが供給できない）。
        // 自前計数（IGoodFaithViolationStore）が別経路で供給する。
        logger.LogInformation(
            "口座種別を観測しました: 発注先={Provider} 種別={AccountType} 決済済み資金={SettledCash}"
                + " 評価額={EquityInBase} 観測時刻={ObservedAt}",
            message.Provider,
            message.Account.AccountType,
            message.Account.SettledCashInBase,
            message.Account.EquityInBase,
            message.ObservedAt);
    }
}
