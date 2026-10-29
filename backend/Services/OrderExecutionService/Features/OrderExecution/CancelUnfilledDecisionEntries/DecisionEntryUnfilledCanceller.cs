using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionCloses;

namespace OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionEntries;

// 🔴 FR-10, FR-19, #1486, IADR-0594 決定1: 取引判断（LLM）由来の**新規建て**の指値が、発注から N 分たっても約定しなければ、**系が取り消すだけ**にする。
//
//   - 形は決済の取消（DecisionCloseUnfilledCanceller・ADR-0067）に倣う: **置き直し・訂正・成行への切り替えはしない**（次の判断の巡回が現在値で決める）、
//     印を先に書く、1 建玉（銘柄・市場・新規建ての方向）・1 市場の現地取引日あたりの回数の上限、当日に発注した注文だけ、取消は利用者の取消と同じ口。
//   - 対象は**判断由来の新規建てだけ**（ApprovalOrigin=TradeDecision かつ PositionEffect=Open）。一部約定なら残りだけが取り消される（約定した建玉は残り、
//     保護の機構がその建玉を守る）。取り消した新規建ての保護の記録は、約定追跡が終端を確かめた後に保護の巡回が閉じる（約定 0 で終端なら従来どおり完了）。
//   - 🔴 **計画の裁定待ち**（planning#852）。計画の取消は決済だけで（ADR-0067 の射程）、新規建てには規則も値も無い。**既定は無効**で、有効化は裁定の後とする。
//     有効時の N・回数の上限の初期値は決済側の値を共用する（暫定。DecisionEntryUnfilledCancelDefaults）。
//
// 🔴 **無効（既定）なら何もしない**——ストアも読まず、ブローカーへ何も送らない。
public sealed class DecisionEntryUnfilledCanceller(
    IExecutedOrderStore store,
    OrderAmendmentService amendments,
    IClock clock,
    IOptions<DecisionEntryUnfilledCancelOptions> options,
    ILogger<DecisionEntryUnfilledCanceller>? logger = null)
{
    /// <summary>ライフサイクル台帳・OrderCancelled に残す取消の理由（系による取消であることを識別できる固定の接頭辞）。</summary>
    public const string ReasonPrefix = "判断由来の新規建ての指値が未約定のため系が取消（IADR-0594）";

    private readonly ILogger _logger = logger ?? NullLogger<DecisionEntryUnfilledCanceller>.Instance;

    /// <summary>1 巡回。無効なら何もせず空を返す。<paramref name="cappedOrderIds"/> は前の巡回までに回数の上限で取り消さないと決めた注文。</summary>
    public Task<DecisionCloseUnfilledCancelResult> RunOnceAsync(
        IReadOnlyCollection<string>? cappedOrderIds = null, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return Task.FromResult(DecisionCloseUnfilledCancelResult.Empty);

        return UnfilledDecisionOrderCancellation.RunAsync(
            store, amendments, clock, settings, PositionEffect.Open, ReasonPrefix, "新規建て", _logger, cappedOrderIds, cancellationToken);
    }
}

/// <summary>
/// 🔴 FR-10, FR-19, #1486, IADR-0594 決定1: 判断由来の新規建ての指値の未約定時の取消の構成（節 <c>DecisionEntryUnfilledCancel</c>）。
/// **既定は無効**（計画の裁定待ち。planning#852）。有効時の範囲の検証は決済の取消と同じ（N は相場操縦の検知の窓より長く 390 分以下、回数は 1〜3）。
/// </summary>
public sealed class DecisionEntryUnfilledCancelOptions : UnfilledDecisionOrderCancelOptions
{
    public const string SectionName = "DecisionEntryUnfilledCancel";

    public DecisionEntryUnfilledCancelOptions()
    {
        Enabled = DecisionEntryUnfilledCancelDefaults.Enabled;
        UnfilledMinutes = DecisionEntryUnfilledCancelDefaults.UnfilledMinutes;
        MaxCancelsPerPositionPerDay = DecisionEntryUnfilledCancelDefaults.MaxCancelsPerPositionPerDay;
    }

    protected override string Section => SectionName;
}

/// <summary>#1486, IADR-0594 決定1: 新規建ての取消の構成を起動で検証する（誤った統制値のまま常駐を始めない）。</summary>
public sealed class DecisionEntryUnfilledCancelOptionsValidator : UnfilledDecisionOrderCancelOptionsValidator<DecisionEntryUnfilledCancelOptions>;
