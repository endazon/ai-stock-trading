namespace RiskManagementService.Features.RiskManagement;

// 🔴 FR-11, FR-10, UC-06, ADR-0007, #1291, IADR-0531: 設定の保存と変更履歴の記録を 1 つの単位で確定する。
// 履歴の書き込みだけが失敗したとき「設定は変わったが履歴が無い」状態を残さない（失敗なら設定も変えない＝安全側）。
// 🔴 kill switch と pause には使わない —— 停止を履歴の失敗で巻き戻すと、止めたいときに止まらない（IADR-0531 決定2）。
public interface ISettingsWriteTransaction
{
    /// <summary><paramref name="body"/> を 1 つのトランザクションで実行する。例外なら本体の書き込みをすべて取り消して再送出する。</summary>
    void Execute(Action body);
}
