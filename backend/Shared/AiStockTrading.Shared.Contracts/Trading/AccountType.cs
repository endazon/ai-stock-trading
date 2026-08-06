namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-19, FR-10, ADR-0021: ブローカー口座の種別。
/// <para>
/// **同時に有効なのは 1 種別である**（ADR-0021 決定2）。両口座を同時に保有して注文ごとに選ぶ形は採らない——
/// 統制値の基準（equity）が口座ごとに二重になり、割合系の統制（equity の 10% / 25% / 150%）が判定できなくなる。
/// </para>
/// <para>
/// **既定は信用口座である**（ADR-0021 決定1。現金口座は拡張として対応する）。ただし**既定値は
/// 「照会できなかったときに当てはめてよい値」ではない**——口座種別の不明は
/// <see cref="RejectionReason.BrokerAccountTypeUnverified"/> として発注を止める（決定3）。
/// </para>
/// <para>
/// 序数は永続化（リスク管理設定 JSON）と結びついている。**値の並べ替え・挿入を行わないこと**
/// （<see cref="ProductType"/> と同じ規律）。
/// </para>
/// </summary>
public enum AccountType
{
    /// <summary>
    /// 信用口座（margin account）。**既定**。信用買い・空売りが可能で、売却代金を決済前に再利用できるため
    /// Good Faith Violation は発生しない（05_trading-assumptions §5・ADR-0021 の比較表）。
    /// </summary>
    Margin = 0,

    /// <summary>
    /// 現金口座（cash account）。**信用買い・空売りができず**（株を借りられない）、
    /// **日本の差金決済規制と Good Faith Violation の双方が適用される**（ADR-0021 決定4）。
    /// GFV は 3 回で 90 日の口座制限という**不可逆**な結果を招く。
    /// </summary>
    Cash = 1,
}
