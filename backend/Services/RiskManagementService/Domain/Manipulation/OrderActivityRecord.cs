using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain.Manipulation;

// FR-19, IADR-0040/0067: 相場操縦検知の入力＝直近窓内の 1 注文のライフサイクル要約。
// 自口座の発注統計（見せ玉・過剰訂正取消・自己レイヤリング）から相場操縦とみなされ得る型を近似検知するための素データ。
// 本番は注文履歴テレメトリ（注文系イベントの Risk 専有 DB への射影・#154）を読む EfOrderActivitySource から供給される。
public sealed record OrderActivityRecord
{
    /// <summary>発注時刻（基準時刻）。窓判定・生存時間の起点。</summary>
    public required DateTimeOffset PlacedAt { get; init; }

    /// <summary>売買方向。レイヤリングは同一方向の同時生存本数で判定する。</summary>
    public required TradeSide Side { get; init; }

    /// <summary>発注数量。</summary>
    public required int Quantity { get; init; }

    /// <summary>約定済み数量（0＝未約定）。</summary>
    public int FilledQuantity { get; init; }

    /// <summary>注文状態（Accepted/Filled/Cancelled/Expired 等）。約定意思・取消の判定に用いる。</summary>
    public required OrderStatus Status { get; init; }

    /// <summary>発注後の訂正（価格/数量変更）回数。過剰訂正の判定に用いる。</summary>
    public int AmendmentCount { get; init; }

    /// <summary>終端時刻（取消/失効/約定などで確定した時刻）。未確定なら null。生存時間の終点。</summary>
    public DateTimeOffset? TerminalAt { get; init; }

    /// <summary>
    /// 約定ゼロで取消/失効した注文か（見せ玉・過剰取消・レイヤリングの母集団）。
    /// ブローカー拒否（<see cref="OrderStatus.Rejected"/>）は板に載らず約定意思の指標にならないため含めない。
    /// 拒否注文を窓の母集団に含めるか（各比率の分母）は供給側（IOrderActivitySource・#154 の射影）の判断とする。
    /// </summary>
    public bool IsCancelledWithoutFill =>
        FilledQuantity == 0 && Status is OrderStatus.Cancelled or OrderStatus.Expired;

    /// <summary>約定または一部約定した注文か（約定意思の有無の判定）。</summary>
    public bool IsFilledOrPartial => FilledQuantity > 0;

    /// <summary>発注→終端の生存時間。終端未確定なら null。短命取消の判定に用いる。</summary>
    public TimeSpan? Lifetime =>
        TerminalAt is { } terminal ? terminal - PlacedAt : null;
}
