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

// TimeProvider の最小の偽装（FakeTimeProvider は中央パッケージ管理に未登録。IADR-0064/0066 と同じ理由）。
// 観測の鮮度を扱うテスト（#564）と口座種別の失効テストが同じ形を使う。
internal sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

// FR-01, FR-02, FR-10, #337, #564, IADR-0249, IADR-0267: テスト用の情報収集の縮退ストア。
//
// **既定引数を持たせない**（呼び出し側が必ず状態を選ぶ）。#564 以降、実物のストアは
// **有効な現況観測が無いかぎり新規建てを止める**（不明は止める側）。縮退を関心に持たないテストが
// 実物をそのまま使うと「観測が無いので止まる」で落ちるため、**「観測できていて健全」を明示する**
// 本ダブルを使う——「既定でなんとなく通る」形に戻さないための選択である。
internal sealed class FakeInformationDegradation : IInformationDegradationStore
{
    private FakeInformationDegradation(bool blocksNewEntries) => BlocksNewEntries = blocksNewEntries;

    public bool BlocksNewEntries { get; private set; }

    /// <summary>収集の現況を観測できており、<b>新規建てを止めるものが無い</b>状態。</summary>
    public static FakeInformationDegradation Affirmed() => new(false);

    /// <summary>新規建てを止めるべき状態（縮退中、または現況が不明）。</summary>
    public static FakeInformationDegradation Blocking() => new(true);

    public void MarkDegraded(string category) => BlocksNewEntries = true;

    public void MarkRecovered(string category) => BlocksNewEntries = false;

    public void ApplyObservation(
        IReadOnlyCollection<string> blockingCategories, TimeSpan validFor, DateTimeOffset observedAt) =>
        BlocksNewEntries = blockingCategories.Count > 0;
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
