using AiStockTrading.Shared.Contracts.Events;
using OrderExecutionService.Domain;

namespace OrderExecutionService.Features.OrderExecution;

// 🔴 FR-10, UC-02, #1422, IADR-0569 決定4: **保護逆指値が未受理で取消を送ったが、取消の結果を確かめられなかったエントリー**
// （保護記録が AwaitingEntryCancel）の後始末の口。約定追跡（OrderFillPoller）はエントリーの終端化を観測できる唯一の点だが、
// 承認の文脈（損切りライン・建て方向）と成行手仕舞いの 3 相を持たないため、それを持つ発注執行（OrderExecutionAppService）が実装する。
public interface IUnconfirmedEntryCancelResolution
{
    /// <summary>
    /// 終端化した新規建ての記録 <paramref name="terminalEntry"/>（状態・約定数は終端化の後の値）について、
    /// 保護記録が「取消の確認待ち」なら閉じ、約定 0 なら取消の確定（<see cref="ProtectiveStopRemediation.EntryCancelled"/>）、
    /// 約定 &gt; 0 なら約定数量を成行で手仕舞った結果の保護喪失を返す。確認待ちでない・非終端・並行に処理済みなら <c>null</c>。
    /// 例外は投げ得る（呼び出し側は巡回を止めずに Critical を残す）。
    /// </summary>
    Task<ProtectiveStopCoverageLost?> ResolveUnconfirmedEntryCancelAsync(
        ExecutionRecord terminalEntry, CancellationToken cancellationToken);
}
