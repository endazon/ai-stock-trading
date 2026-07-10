using System.Security.Cryptography;
using System.Text;

namespace AiStockTrading.Audit.Application.Services;

// FR-11, IADR-0019/0026: 注文チェーンの Guid 相関（DecisionId/EventId）を持たないイベント（設定変更・報告書確定）向けに、
// 自然キーから決定的な CorrelationId（Guid）を導出する。同一キーは同一相関となり、監査照会でまとめて辿れる。
// RFC 4122 名前ベース UUID（バージョン5）を SHA1（名前空間＋名前）から生成し、バージョン/バリアントビットを設定する
// （BCL に名前ベース v5 生成 API が無いため自前実装。CreateVersion7 は時間ベースで用途が異なる・IADR-0026）。
internal static class AuditCorrelation
{
    // 監査相関の名前空間 ID（本用途に固定・一度限りの乱数由来）。
    private static readonly byte[] NamespaceBytes = new Guid("7b1e9c4a-2d63-4f18-9a5e-3c0b8f6d21e4").ToByteArray();

    public static Guid From(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var nameBytes = Encoding.UTF8.GetBytes(key);
        var input = new byte[NamespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(NamespaceBytes, 0, input, 0, NamespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, NamespaceBytes.Length, nameBytes.Length);

        var hash = SHA1.HashData(input);
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);

        // バージョン=5（名前ベース SHA1）・バリアント=RFC 4122 のビットを設定する。
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }
}
