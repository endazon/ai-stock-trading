using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Common.Abstractions;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Tests;

// テスト用の固定クロック。ロックアウトの翌営業日解除を決定的に検証するため時刻・当日を明示制御する。
internal sealed class FakeClock(DateTimeOffset utcNow, DateOnly today) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public DateOnly Today { get; set; } = today;
}

// テスト用のポートフォリオ状態プロバイダ。判定入力を明示的に組み立てる。
internal sealed class FakePortfolioStateProvider(PortfolioState state) : IPortfolioStateProvider
{
    public PortfolioState State { get; set; } = state;

    public PortfolioState GetCurrent() => State;
}

// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: テスト用の口座種別観測ストア。
//
// **既定引数を持たせない**（呼び出し側が必ず状態を選ぶ）。口座種別は統制の適用可否を決める最上位の条件であり、
// 「書き忘れたら既定で信用口座」になる形は、現金口座で GFV 回避ガードが無効のまま回る事故そのものである。
// 本番の依存注入も同じ理由で必須コンストラクタ引数にしてある（供給を忘れるとコンパイルが落ちる）。
internal sealed class FakeBrokerAccountObservations : IBrokerAccountObservationStore
{
    private FakeBrokerAccountObservations(BrokerAccountState? account) => Account = account;

    public BrokerAccountState? Account { get; set; }

    public DateTimeOffset? LastObservedAt { get; private set; }

    /// <summary>
    /// 口座種別を確認できていない状態（未観測・照会失敗・種別不明・失効）。
    /// <b>moomoo 発注先の新規建ては <c>BrokerAccountTypeUnverified</c> で止まる。</b>
    /// 内蔵 paper の注文だけを扱うテストはこれを使う（口座種別を要求しないため。IADR-0153 決定2）。
    /// </summary>
    public static FakeBrokerAccountObservations NotObserved() => new(null);

    /// <summary>信用口座（ADR-0021 決定1 の既定）を照会できている状態。</summary>
    public static FakeBrokerAccountObservations Margin() => new(new BrokerAccountState(AccountType.Margin));

    /// <summary>
    /// 現金口座を照会できている状態。決済済み資金は既定で<b>未供給</b>（＝買付が止まる側）。
    /// <para>
    /// #425, ADR-0025 決定2, IADR-0165: <b>GFV 発生回数は本型に載らない</b>——ブローカーが供給できず、
    /// 自前計数（<c>IGoodFaithViolationStore</c>）が別経路で供給する。
    /// </para>
    /// </summary>
    public static FakeBrokerAccountObservations Cash(decimal? settledCashInBase = null) =>
        new(new BrokerAccountState(AccountType.Cash, settledCashInBase));

    public void Record(BrokerAccountState account, DateTimeOffset observedAt)
    {
        Account = account;
        LastObservedAt = observedAt;
    }

    public BrokerAccountState? GetCurrent() => Account;
}
