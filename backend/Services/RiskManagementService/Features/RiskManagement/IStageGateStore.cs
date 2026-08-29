using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement;

// FR-20, UC-06, IADR-0041/0070: 段階遷移台帳（追記専用）の永続化ポート。現在段階・次シーケンスは
// 履歴の畳み込み（StageGateLedger）で導出するため、可変の「現在段階」は持たず履歴のみを保存・追記する。
public interface IStageGateStore
{
    /// <summary>永続化済みの遷移履歴から段階ゲート台帳を復元する（空なら Stage 0 起点）。</summary>
    StageGateLedger Load();

    /// <summary>受理された遷移を 1 件追記する（追記専用・監査対象）。</summary>
    void Append(StageTransition transition);
}
