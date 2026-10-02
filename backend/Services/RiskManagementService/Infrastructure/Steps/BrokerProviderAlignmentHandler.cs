using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace RiskManagementService.Infrastructure.Steps;

// 🔴 FR-10, FR-20, ADR-0040 決定1, #1048（利用者裁定 2026-10-02・Q1）, IADR-0481 決定4: 口座照会の観測（BrokerAccountObserved）が
// 運ぶ**実際の発注先**と、**設定上の発注先**（リスク管理の設定値）が揃っているかを観測のたびに確かめ、揃っていなければ知らせる。
//
// - 観測は発注執行の定期 probe（既定 5 分）で届くので、起動の直後に最初の判定が走り、以後も食い違いが続く限り知らせ続ける
//   （**正常な見え方では 1 件も出ない**。続いていること自体が「揃えていない」という事実である）。
// - 観測の記録（BrokerAccountObservedHandler）とは別のハンドラにする——判定の失敗で口座種別・基準資金の供給を止めない。
// - **設定値を書き換えない。** 揃えるのは利用者の操作（PUT /risk-controls/settings/broker-provider。理由・前後値つきで履歴に残る）
//   であり、観測から黙って書き換えると、実弾への切り替えに求める確認操作（FR-20）を迂回する経路になる（IADR-0413 決定1）。
public sealed class BrokerProviderAlignmentHandler(
    IRiskSettingsStore settings,
    ILogger<BrokerProviderAlignmentHandler> logger)
{
    public void Handle(BrokerAccountObserved message)
    {
        ArgumentNullException.ThrowIfNull(message);

        BrokerProviderMisalignment? misalignment;
        try
        {
            misalignment = BrokerProviderAlignment.Check(settings.GetCurrent().BrokerProvider, message.Provider);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "設定上の発注先を読めなかったため、実際の発注先と揃っているかを確かめられませんでした（次の観測で確かめ直します）。");
            return;
        }

        if (misalignment is null)
            return;

        logger.Log(
            misalignment.ActualIsLive ? LogLevel.Error : LogLevel.Warning,
            "設定上の発注先（{Configured}）が実際の発注先（{Actual}・口座照会 {ObservedAt}）と揃っていません。"
                + "損切りの実行機構の選択の関門と画面の表示は設定上の発注先で判定されます。"
                + "PUT /risk-controls/settings/broker-provider で {Actual} に揃えてください（設定は自動では書き換えません）。",
            misalignment.Configured, misalignment.Actual, message.ObservedAt, misalignment.Actual);
    }
}
