using System.Security.Cryptography;
using System.Text;

namespace OrderExecutionService.Domain;

// FR-10, #331, IADR-0210: 保護レグ（逆指値・手仕舞い）の DecisionId をエントリー DecisionId から
// **決定的に**導出する。再送・再巡回で同じレグは同じ DecisionId になり、発注結果ストアの
// DecisionId 1:1（IADR-0057 相1）と台帳の AppendApproval 冪等がそのまま二重計上を防ぐ
// （StopLossTriggered.EventId から決定的に採っていた旧 IADR-0015 と同じ規律）。
public static class ProtectiveStopIds
{
    /// <summary>逆指値レグの DecisionId（試行番号つき。1=同時発注、2 以降=失効後の再発注）。</summary>
    public static Guid StopDecisionId(Guid entryDecisionId, int attempt) =>
        Derive($"protective-stop:{entryDecisionId:N}:{attempt}");

    /// <summary>逆指値が成立しないときの成行手仕舞いレグの DecisionId。</summary>
    public static Guid CloseDecisionId(Guid entryDecisionId, int attempt) =>
        Derive($"protective-close:{entryDecisionId:N}:{attempt}");

    // 名前ベースの決定的 GUID（SHA-256 の先頭 16 バイト）。暗号用途ではなく相関キーの導出であり、
    // 必要な性質は「同じ入力 → 同じ GUID・異なる入力 → 衝突が実用上起きない」のみ。
    private static Guid Derive(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return new Guid(hash.AsSpan(0, 16));
    }
}
