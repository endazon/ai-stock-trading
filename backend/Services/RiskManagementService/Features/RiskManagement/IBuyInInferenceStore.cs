using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-11, FR-06, UC-06, ADR-0016 決定4（2026-08-06 改訂）・決定15, #419, IADR-0159 決定3:
// 強制買戻しの事後推定の**追記専用台帳**。3 つの役割を持つ。
//
//   1. **禁止期限の供給**（<see cref="GetBanUntil"/>）: #374 で実装済みの `BuyInBanned` を実際に発動させる。
//   2. **重複推定の抑止**（<see cref="GetLatestPerSymbol"/>）: 台帳は自動では是正されないため、同じ乖離が
//      毎巡回で観測され続ける。帰属数量を持たないと 30 日の禁止が事実上の恒久禁止になる（IADR-0159 決定2）。
//   3. **発生回数の集計元**（<see cref="CountInferredBetween"/>）: ADR-0016 決定15。
//      **`RejectionReason.BuyInBanned` の拒否件数を集計元にしてはならない**——1 回の強制買戻しに対して
//      禁止期間 30 日のあいだ何度でも拒否は起こり得るため、実際より大きな数字が月報に載る。
//
// **永続でなければならない。** 30 日の禁止をプロセス内に持つと、再起動で禁止が消える（fail-open）。
public interface IBuyInInferenceStore
{
    /// <summary>推定（またはリセット）を 1 行追記する。</summary>
    void Append(BuyInInferenceRecord record);

    /// <summary>
    /// 銘柄ごとの**最新行**。帰属数量（<c>UnexplainedQuantity</c>）の突き合わせに用いる。
    /// </summary>
    IReadOnlyList<BuyInInferenceRecord> GetLatestPerSymbol();

    /// <summary>
    /// 当該銘柄の禁止期限（記録された <c>BanUntil</c> の**最大値**）。禁止が無ければ <c>null</c>。
    /// **期限は日付でのみ失効する**——観測が届かない・照会が失敗したことを理由に解けてはならない
    /// （IADR-0159 決定4）。
    /// </summary>
    DateOnly? GetBanUntil(string symbol, Market market);

    /// <summary>
    /// 期間 [fromInclusive, toInclusive]（推定日）に**新たに推定した**件数。リセット行は数えない。
    /// **ADR-0016 決定15 の「強制買戻しの発生回数」の集計元はこれである。**
    /// </summary>
    int CountInferredBetween(DateOnly fromInclusive, DateOnly toInclusive);

    /// <summary>
    /// 期間 [fromInclusive, toInclusive]（推定日）の推定行。日報の「発生有無」・月報の明細に用いる。
    /// </summary>
    IReadOnlyList<BuyInInferenceRecord> GetInferredBetween(DateOnly fromInclusive, DateOnly toInclusive);
}
