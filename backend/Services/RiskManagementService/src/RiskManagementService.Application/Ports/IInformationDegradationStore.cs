namespace RiskManagementService.Application.Ports;

// FR-01, FR-02, FR-10, ADR-0020 決定2/決定3, #337, IADR-0249: 情報収集の縮退状態（新規建て停止）の保持。
//
// 収集サービスは欠測の**遷移**でのみ `InformationSourceDegraded` / `InformationSourceRecovered` を発行する
// （#336・洪水抑止）。リスク管理はカテゴリ単位でその状態を畳み、**BlocksNewEntries=true のカテゴリが
// 1 つでも残っていれば新規建てを止める**（判定は RiskEvaluator・拒否理由 InformationSourceDegraded）。
//
// 🔴 **手仕舞い・損切りを止める表現を持たない。** 本ポートが供給するのは「新規建てを止めるか」の
// 1 ビットだけであり、決済側の停止は型として表現できない（CollectionDegradation と同じ構造防御）。
public interface IInformationDegradationStore
{
    /// <summary>新規建てを停止すべき縮退カテゴリを登録する（BlocksNewEntries=true の Degraded 遷移）。</summary>
    void MarkDegraded(string category);

    /// <summary>カテゴリの回復（Recovered 遷移）。未登録のカテゴリは無視する（冪等）。</summary>
    void MarkRecovered(string category);

    /// <summary>新規建てを停止すべき縮退が 1 つでも残っているか。</summary>
    bool BlocksNewEntries { get; }
}
