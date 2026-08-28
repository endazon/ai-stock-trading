namespace AiStockTrading.Shared.Contracts.Operations;

/// <summary>
/// NFR-08 / NFR-09 / NFR-10 / NFR-11, FR-11, #339: **どのストアをパージしてよいか**の単一情報源。
/// <para>
/// 非機能要件は 2 つの保持区分を定める。
/// ① <b>重複排除メタデータ</b>（既定 90 日・下限 7 日でパージする。NFR-08）
/// ② <b>業務台帳・監査証跡</b>（費用台帳・発注履歴・監査ログ。<b>7 年保持で自動パージの対象外</b>。NFR-10）
/// </para>
/// <para>
/// 🔴 <b>列挙するのはパージ「してよい」側だけである。</b> 7 年側を列挙すると、
/// テーブルが増えるたびに表が腐り、**漏れたストアが黙ってパージ可へ倒れる**（fail-open）。
/// 閉世界にしておけば、未知のストアは必ず**パージされない側**へ倒れる（fail-safe）。
/// パージは不可逆な DELETE であり、倒れる向きは「消さない」でなければならない。
/// </para>
/// <para>
/// 🔴 <b>本クラスは飾りではない。</b> パージ経路は削除の前に <see cref="EnsurePurgeable"/> を呼ぶ。
/// 宣言だけを置いて誰も呼ばなければ、新しいパージ経路を足しても何も赤くならない。
/// </para>
/// </summary>
public static class RetentionScope
{
    /// <summary>
    /// **自動パージしてよいストア**（閉じた列挙）。
    /// <para>
    /// <c>processed_messages</c> … 費用統制の重複排除メタデータ（NFR-08）。
    /// <c>order_dispatch_reservations</c> … 発注執行の重複排除メタデータ。
    /// <b>パージ対象は終端行だけである</b> —— 発注済みか不明な予約は期限を過ぎても削除しない（NFR-09）。
    /// この区別はストア実装（<c>PurgeCompletedBefore</c>）が持ち、本列挙はテーブル単位の可否だけを定める。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> PurgeableStores =
    [
        "processed_messages",
        "order_dispatch_reservations",
    ];

    /// <summary>
    /// そのストアを自動パージしてよいか。**列挙に無いものはすべて 7 年保持**（NFR-10）である。
    /// </summary>
    public static bool IsPurgeable(string store) =>
        PurgeableStores.Contains(store, StringComparer.Ordinal);

    /// <summary>
    /// パージ対象として妥当であることを確かめる。**業務台帳・監査証跡なら例外で止める。**
    /// <para>
    /// 監査ログ（<c>audit_events</c>）・費用台帳・発注履歴・借株料台帳などは 7 年保持であり、
    /// パージ経路がこれらを指した瞬間に落ちる。**「消してから気づく」経路を作らない。**
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="store"/> が null または空。</exception>
    /// <exception cref="InvalidOperationException">7 年保持のストア（＝列挙に無いストア）。</exception>
    public static void EnsurePurgeable(string store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(store);

        if (!IsPurgeable(store))
        {
            throw new InvalidOperationException(
                $"'{store}' は自動パージの対象外である（業務台帳・監査証跡は 7 年保持・NFR-10）。"
                + $"パージしてよいのは {string.Join(" / ", PurgeableStores)} だけである。");
        }
    }
}
