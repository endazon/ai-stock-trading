using Microsoft.Extensions.Logging;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// 🔴 FR-20, ADR-0008, #1533, IADR-0616 決定3: 審査・サイジングが読む運用段階（RiskManagementSettings.Stage）の正を段階ゲートの台帳へ寄せる読み取りの継ぎ目。
//   - 段 1（Report・既定）: 設定の値をそのまま返し、台帳の現段階の定義（StageGatePolicy.SettingsFor）と食い違えば Warning を残す
//     （同じ食い違いは 1 回だけ。審査のたびにログを埋めない）。
//   - 段 2（Enforce）: 段階を台帳の現段階の定義で置き換えて返す（審査は台帳の段階で判定する）。
//     保存（Save）は段階を保存値のまま戻して書く —— 段階以外の設定の保存（GetCurrent → Save）が、置き換わった段階を履歴なしに保存値へ
//     書き込まないため。段階を書くのは SaveWithStage（遷移の受理）だけ。段階の変更の Before は GetStored（保存値）から取る。
//   - 「1 回だけ」: 同じ組（設定の段階・台帳の定義）の食い違いは、続けて出る間は 1 回だけ残す（プロセス内・全スコープで共有）。
//     組が変わると残し、元の組へ戻るとまた残す（直前に残した組だけを覚える）。再起動でも残し直す。
// 書き込みの側の正は段階ゲートの遷移（StageGateService.RequestTransition）であり、設定の口（PUT /settings/stage）は段階を変えない（決定1）。
public sealed class StageGateBoundRiskSettingsStore(
    IRiskSettingsStore inner,
    IStageGateStore ledger,
    StageGatePolicy policy,
    StageSourceMode mode,
    ILogger<StageGateBoundRiskSettingsStore> logger) : IRiskSettingsStore
{
    private static readonly Lock Gate = new();
    private static string? s_lastReported;

    public RiskManagementSettings GetCurrent()
    {
        var current = inner.GetCurrent();
        var gateStage = ledger.Load().CurrentStage;
        var defined = policy.SettingsFor(gateStage);
        if (current.Stage == defined)
            return current;

        if (mode == StageSourceMode.Enforce)
            return current with { Stage = defined };

        var key = $"{current.Stage}|{defined}";
        lock (Gate)
        {
            if (s_lastReported == key)
                return current;
            s_lastReported = key;
        }

        logger.LogWarning(
            "審査が読む運用段階（設定）と段階ゲートの台帳の現段階の定義が食い違っています（段 1 のため設定の値で審査します）: 設定={Settings} 台帳={Gate}",
            current.Stage, defined);
        return current;
    }

    public void Save(RiskManagementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        inner.Save(mode == StageSourceMode.Enforce ? settings with { Stage = inner.GetStored().Stage } : settings);
    }

    public RiskManagementSettings GetStored() => inner.GetStored();

    public void SaveWithStage(RiskManagementSettings settings) => inner.SaveWithStage(settings);

    public long? GetProductTypesRevision() => inner.GetProductTypesRevision();

    public long EnsureProductTypesRevision() => inner.EnsureProductTypesRevision();
}
