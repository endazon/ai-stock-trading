namespace AiStockTrading.Shared.Contracts.Trading;

// FR-05, FR-10, FR-11, #292, IADR-0118: 取引台帳とブローカ実ポジションの乖離 1 件。
// 数量はいずれも符号付き（+ ロング / − ショート）。

/// <summary>乖離の種類。どちらが正しいかは断定しない（双方の数量を並べて人の判断に委ねる）。</summary>
public enum PositionDriftKind
{
    /// <summary>ブローカにだけある建玉（手動売買・外部約定など AST が知らない取引）。</summary>
    BrokerOnly = 0,

    /// <summary>取引台帳にだけある建玉（約定の誤計上・ブローカ側での決済など）。</summary>
    LedgerOnly,

    /// <summary>双方に建玉があるが数量が一致しない。</summary>
    QuantityMismatch,
}

public sealed record PositionDriftItem(
    string Symbol,
    Market Market,
    int LedgerQuantity,
    int BrokerQuantity,
    PositionDriftKind Kind);
