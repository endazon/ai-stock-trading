using AiStockTrading.Shared.Contracts.Events;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 T-10-410, FR-05, FR-10, #852, #873, IADR-0356: 「まだ誰も分類していない見送り理由」の番兵。
//
// 否定形テスト（allowlist の既定が「解放しない」側であること）に与える値であり、
// **どの時点でも `OrderDispatchForgoneReason` に実在してはならない**。
//
// 🔴 **literal で書かない。** 初版は `[InlineData(4)]` と書いていたが、
// #873 が `BrokerPositionAbsent` / `BrokerPositionsIndeterminate` を足すと**序数 4 は実在の理由になり**、
// 番兵が「未分類の値」ではなく「allowlist へ `true` で入れたばかりの値」を指してしまう。
// そうなると否定形テストは
// 「`BrokerPositionAbsent` を解放するな」という**事実と逆のメッセージ**で赤くなり、
// 読んだ人を「allowlist への追加が誤りだった」＝**#852 の実害（30 分ロック）の再導入**へ誘導する。
// （#873 の段取りを実際になぞった監査が、この形の赤を 2 件踏んだ。）
//
// **列挙の最大序数の 1 つ先**を実行時に計算することで、採番が動いても腐らず、
// **要素数テスト（`見送り理由の要素数を固定する`）だけが唯一のトリップワイヤとして残る**。
public static class UndefinedForgoneReasons
{
    /// <summary>
    /// 現に定義されている最大の序数の 1 つ先。列挙に値が足されると**自動的にずれる**ので、
    /// 番兵が実在の理由へ化けることがない。
    /// </summary>
    public static int Next =>
        Enum.GetValues<OrderDispatchForgoneReason>().Cast<int>().Max() + 1;

    public static TheoryData<int> 未定義の見送り理由 =>
        new()
        {
            // 「次に足される序数」——分類を忘れたまま値だけ増えた状態を模す。
            Next,
            // もっと先の序数（飛び番で足された場合）。
            Next + 1,
            // 列挙が絶対に到達しない値。
            9_999,
            // 負の序数（キャストで作れてしまう値）。
            -1,
        };
}
