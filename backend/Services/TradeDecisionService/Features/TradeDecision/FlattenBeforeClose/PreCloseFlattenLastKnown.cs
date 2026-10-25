extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;

namespace TradeDecisionService.Features.TradeDecision.FlattenBeforeClose;

// 🔴 FR-10, FR-19, SC-02, #1407, IADR-0564 決定 1（PR #1408 のレビュー）: 直近にサイジング文脈から受け取った閉場前の手仕舞いの効いている値。
// サイジング文脈の照会が失敗した巡回（タイムアウト・非 2xx・例外・契約の食い違いの安全既定）や、項目を持たない旧応答では、既定（無効・15 分）へ倒さず
// **直近に取れた値**で判定する（手仕舞いは持ち越しを減らす統制であり、照会の一過性の失敗で無効や短い M へ倒すと統制が緩む）。
// プロセスの起動から一度も取れていなければ既定で判定する（残る制約。IADR-0564）。常駐の巡回をまたいで保つため singleton にする。
public sealed class PreCloseFlattenLastKnown
{
    private PreCloseFlattenSettings? _value;

    /// <summary>直近に受け取った値（起動から一度も受け取っていなければ null）。</summary>
    public PreCloseFlattenSettings? Value => Volatile.Read(ref _value);

    /// <summary>受け取った値を覚える（null は覚えない＝直近の値を消さない）。</summary>
    public void Remember(PreCloseFlattenSettings? value)
    {
        if (value is not null)
            Volatile.Write(ref _value, value);
    }
}
