namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: ブローカーへ**照会して得た**口座の状態。
/// <para>
/// <b>この型の値が存在すること自体が「照会に成功した」ことを意味する。</b> 照会失敗・応答異常・
/// 種別不明（<c>TrdAccType_Unknown</c>）はいずれも <c>null</c> であり、
/// 「信用口座だった」と同じ形で表してはならない（IADR-0148 の「未供給と 0 件を区別する」と同じ規律）。
/// </para>
/// </summary>
/// <param name="AccountType">
/// 照会で得た口座種別。<b>これが正であり、利用者の設定値と食い違えば発注を止める</b>（ADR-0021 決定3）。
/// </param>
/// <param name="SettledCashInBase">
/// 決済済み資金（基準通貨・USD）。**T+1 が未到来の売却代金を含めない**残高であり、GFV 回避ガードの分母である
/// （ADR-0021 決定4-2）。
/// <para>
/// <b><c>null</c> は「取得できなかった」を意味し、現金口座では買付を止める</b>（fail-closed）。
/// <b>moomoo API には決済済み資金の専用フィールドが存在しない</b>ことを実測済みであり
/// （<c>TrdCommon.Funds</c> の全フィールドを走査。IADR-0153 決定4）、現時点で本値の供給元は無い。
/// </para>
/// </param>
/// <param name="GoodFaithViolationCount">
/// Good Faith Violation の発生回数。**2 回で警告し、3 回目の手前で新規建てを停止する**（ADR-0021 決定4-3）。
/// <para>
/// <b><c>null</c> は「供給されていない」を意味し、現金口座では新規建てを止める</b>——2 回に達していないことを
/// 確認できないためである。**3 回で 90 日の口座制限**という不可逆な結果に対して、
/// 「分からないから通す」は統制にならない。
/// </para>
/// </param>
public sealed record BrokerAccountState(
    AccountType AccountType,
    decimal? SettledCashInBase = null,
    int? GoodFaithViolationCount = null);
