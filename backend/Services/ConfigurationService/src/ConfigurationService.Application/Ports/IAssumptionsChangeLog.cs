using ConfigurationService.Application.State;

namespace ConfigurationService.Application.Ports;

// FR-17: 全体前提条件の変更履歴の記録・照会（追記専用・新しい順）。
public interface IAssumptionsChangeLog
{
    void Record(AssumptionsChangeEntry entry);

    /// <summary>記録済みの変更履歴を新しい順で返す。</summary>
    IReadOnlyList<AssumptionsChangeEntry> GetHistory();
}
