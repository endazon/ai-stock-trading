using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;

namespace AuditService.Features.AuditEvents;

// FR-11, IADR-0019: 監査記録 Detail のシリアライズ方針。列挙は文字列化して人間可読・監査容易にする。
//
// 🔴 **設定そのものは契約（`AuditDetailJson`）に置いてある**（#381 供給結線・IADR-0199 決定6）。
// **読み手（報告サービス）が別アセンブリに居る**ため、ここへ設定を書き戻さないこと——
// **片側だけ変えても例外にならず、既定値で埋めた record が黙って作られる。**
internal static class AuditSerialization
{
    public static readonly JsonSerializerOptions Options = AuditDetailJson.Options;

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
