using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-05, FR-10, #292, IADR-0118: ブローカが保持する建玉の照会ポート。
//
// 建玉照会を <see cref="IBrokerAdapter"/> に足さないのは、全アダプタ（ペーパーを含む）へ実装を強いないため。
// 本ポートを実装しないアダプタでは DI に本ポートが現れず、突合の常駐が起動時に自己停止する（構造的な非干渉）。
public interface IBrokerPositionSource
{
    /// <summary>
    /// 現在の建玉一覧を返す。
    ///
    /// 契約（fail-safe の要）:
    ///   - 全対応市場を**成功裏に列挙**できた → その一覧（建玉が無ければ**空列**）。
    ///   - 照会不能（不達・応答異常・部分列挙）→ **null（＝不明）**。
    ///
    /// 空列（建玉ゼロ）と null（不明）を取り違えてはならない。取り違えると「ブローカは何も持っていない」と
    /// 誤断定し、台帳の全建玉が乖離として報告される（IADR-0092 が null と例外を区別したのと同型の要請）。
    /// </summary>
    Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken cancellationToken = default);
}
