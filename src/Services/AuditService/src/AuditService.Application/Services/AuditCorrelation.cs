using System.Security.Cryptography;
using System.Text;

namespace AiStockTrading.Audit.Application.Services;

// FR-11, IADR-0019: 注文チェーンの Guid 相関（DecisionId/EventId）を持たないイベント（設定変更・報告書確定）向けに、
// 自然キーから決定的な CorrelationId（Guid）を導出する。同一キーは同一相関となり、監査照会でまとめて辿れる。
internal static class AuditCorrelation
{
    public static Guid From(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}
