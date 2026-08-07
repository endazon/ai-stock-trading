using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-20, FR-13, FR-11, UC-06, SC-02, INDEX 決定 46, #334, IADR-0140 / IADR-0141:
// 発注先（Broker Provider）の変更と、運用段階との**独立性**の検証。
//
// 計画の受け入れ基準（02_requirements）:
//   「運用段階と発注先が独立して保持される（FR-20）。**段階を変更しても発注先は自動で変わらず、
//     発注先を変更しても段階は自動で変わらない**」
//   「発注先の変更が、**日時・変更前後・理由**とともに変更履歴と監査ログに残る（FR-13）。
//     変更理由が空欄では保存できず、版が競合した場合は保存されない」
public class BrokerProviderSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 6, 0, 0, TimeSpan.Zero);

    private static (RiskSettingsService Service, InMemoryRiskSettingsStore Store, InMemorySettingsChangeLog Log)
        Create()
    {
        var store = new InMemoryRiskSettingsStore();
        var log = new InMemorySettingsChangeLog();
        var clock = new FakeClock(Now, new DateOnly(2026, 8, 5));
        // #375, ADR-0021 決定4-4: 発注先の設定は口座種別に依存しない。観測は供給しない。
        return (new RiskSettingsService(store, log, clock, FakeBrokerAccountObservations.NotObserved()), store, log);
    }

    // IADR-0140 決定4: 計画は初期値を述べていない。実装は**外部へ一度も発注しない値**を選ぶ。
    [Fact]
    public void 発注先の初期値は内蔵paperである()
    {
        var (_, store, _) = Create();

        store.GetCurrent().BrokerProvider.Should().Be(BrokerProvider.InternalPaper);
        store.GetCurrent().BrokerProvider.Should().NotBe(
            BrokerProvider.MoomooReal,
            "初期状態が実弾を指してはならない（設定を一度も触っていない状態が最も危険であってはならない）");
    }

    // FR-13, FR-20 (2): 日時・変更前後・理由が履歴に残る。
    [Fact]
    public void 発注先の変更は現行値を更新し日時と前後値と理由つきで履歴に残す()
    {
        var (service, store, log) = Create();

        var assessment = service.UpdateBrokerProvider(
            new BrokerProviderChangeRequest(BrokerProvider.MoomooSimulate, "Stage 1 の検証を開始する"),
            actor: "owner");

        assessment.Accepted.Should().BeTrue();
        store.GetCurrent().BrokerProvider.Should().Be(BrokerProvider.MoomooSimulate);

        var entry = log.GetHistory().Should().ContainSingle().Subject;
        entry.ChangeType.Should().Be(SettingsChangeType.BrokerProviderChanged);
        entry.Actor.Should().Be("owner");
        entry.Reason.Should().Be("Stage 1 の検証を開始する");
        entry.ChangedAt.Should().Be(Now);
        entry.Before.Should().Be(BrokerProvider.InternalPaper.ToString());
        entry.After.Should().Be(BrokerProvider.MoomooSimulate.ToString());
    }

    // 否定形（受け入れ基準「変更理由が空欄では保存できず」）: 拒否された要求は
    // **設定も履歴も変えない**。拒否を履歴へ積むと、起きていない変更が監査上の事実になる。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 理由が空の発注先変更は設定も履歴も変えない(string reason)
    {
        var (service, store, log) = Create();

        var assessment = service.UpdateBrokerProvider(
            new BrokerProviderChangeRequest(BrokerProvider.MoomooSimulate, reason),
            actor: "owner");

        assessment.Accepted.Should().BeFalse();
        store.GetCurrent().BrokerProvider.Should().Be(BrokerProvider.InternalPaper);
        log.GetHistory().Should().BeEmpty();
    }

    // 否定形（本 issue の中核）: 確認操作を欠いた実弾切替は**設定を変えない**。
    [Fact]
    public void 確認操作を欠いた実弾への切替は設定も履歴も変えない()
    {
        var (service, store, log) = Create();

        var assessment = service.UpdateBrokerProvider(
            new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "実弾へ移行する"),
            actor: "owner");

        assessment.Accepted.Should().BeFalse();
        store.GetCurrent().BrokerProvider.Should().Be(BrokerProvider.InternalPaper);
        log.GetHistory().Should().BeEmpty();
    }

    [Fact]
    public void 同意と文字入力が揃った実弾への切替は受理され履歴に残る()
    {
        var (service, store, log) = Create();

        var assessment = service.UpdateBrokerProvider(
            new BrokerProviderChangeRequest(
                BrokerProvider.MoomooReal,
                "Stage 2 へ移行したため",
                AcknowledgedLiveTrading: true,
                Acknowledgement: BrokerProviderChange.LiveAcknowledgementPhrase),
            actor: "owner");

        assessment.Accepted.Should().BeTrue();
        store.GetCurrent().BrokerProvider.Should().Be(BrokerProvider.MoomooReal);
        log.GetHistory().Should().ContainSingle(e => e.ChangeType == SettingsChangeType.BrokerProviderChanged);
    }

    // 受け入れ基準（2 軸の独立・その1）: 発注先を変更しても段階は自動で変わらない。
    [Fact]
    public void 発注先の変更は段階を変えない()
    {
        var (service, store, _) = Create();
        var stageBefore = store.GetCurrent().Stage;

        service.UpdateBrokerProvider(
            new BrokerProviderChangeRequest(BrokerProvider.MoomooSimulate, "検証開始"),
            actor: "owner");

        store.GetCurrent().Stage.Should().Be(stageBefore);
    }

    // 受け入れ基準（2 軸の独立・その2）: 段階を変更しても発注先は自動で変わらない。
    // **段階の既定発注先へ勝手に追随させない**——追随させると 2 軸である意味が消え、
    // 「段階を上げた瞬間に実弾になる」という利用者の意図しない切替が成立する。
    [Fact]
    public void 段階の変更は発注先を変えない()
    {
        var (service, store, _) = Create();
        service.UpdateBrokerProvider(
            new BrokerProviderChangeRequest(BrokerProvider.InternalPaper, "デバッグ中"),
            actor: "owner");

        // 段階を実弾段階（Stage 2・既定発注先は moomoo REAL）へ変更する。
        service.UpdateStage(
            new StageSettings(
                TradingStage.Stage2MinimalLive,
                BrokerProvider.MoomooReal,
                TradingDefaults.Stage2MinimalLiveCapitalCapRatio),
            "owner",
            "Stage 2 へ昇格");

        store.GetCurrent().Stage.Stage.Should().Be(TradingStage.Stage2MinimalLive);
        store.GetCurrent().BrokerProvider.Should().Be(
            BrokerProvider.InternalPaper,
            "段階の既定発注先へ自動追随させると、段階を上げた瞬間に実弾へ切り替わる（FR-20 の 2 軸独立に反する）");
    }

    // 05_screens SC-02 ②: Stage 1 のまま実弾を選ぶ操作は保存できるが、段階ゲート飛ばしの警告を返す。
    [Fact]
    public void Stage1のまま実弾を選ぶ操作は保存できるが段階ゲート飛ばしを警告する()
    {
        var (service, store, _) = Create();
        service.UpdateStage(
            new StageSettings(
                TradingStage.Stage1Simulate,
                BrokerProvider.MoomooSimulate,
                TradingDefaults.FullCapitalCapRatio),
            "owner",
            "Stage 1 へ昇格");

        var assessment = service.UpdateBrokerProvider(
            new BrokerProviderChangeRequest(BrokerProvider.MoomooReal, "検証を打ち切る", true, "REAL"),
            actor: "owner");

        assessment.Accepted.Should().BeTrue();
        assessment.SkipsStageGate.Should().BeTrue();
        store.GetCurrent().BrokerProvider.Should().Be(BrokerProvider.MoomooReal);
    }

    // アクターは監査の要（FR-11）。空アクターでの変更は例外で止める（既存の上限・ガード変更と同じ規律）。
    [Fact]
    public void アクターが空の発注先変更は拒否される()
    {
        var (service, _, _) = Create();

        var act = () => service.UpdateBrokerProvider(
            new BrokerProviderChangeRequest(BrokerProvider.MoomooSimulate, "理由"),
            actor: "  ");

        act.Should().Throw<ArgumentException>();
    }

    // ---- 段階の既定発注先（Stage.Mode）の書き込み経路 allow-list（#434・IADR-0161 決定3 / IADR-0163） ----
    //
    // IADR-0161 は読み書きを**意図的に非対称**に設計した。読み取りは黙って内蔵 paper へ倒すが（例外に
    // すると設定行全体が失われる）、書き込みは拒否する（黙って倒すと「実弾を選んだのに paper になった」
    // という説明のつかない状態遷移が生まれる）。`Stage.Mode` はこの非対称が破れていた。

    // T-105（**最重要**）: 未知の序数を書き込もうとしたら拒否し、**設定も履歴も変えない**。
    // 補正して受理してはならない——「保存できた」と思っているのに読み戻すと別の値、が本 issue の主題である。
    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void 段階の既定発注先が未知の値なら拒否し設定も履歴も変えない(int unknownMode)
    {
        var (service, store, log) = Create();
        var before = store.GetCurrent();

        var act = () => service.UpdateStage(
            new StageSettings(
                TradingStage.Stage1Simulate,
                (BrokerProvider)unknownMode,
                TradingDefaults.FullCapitalCapRatio),
            "owner",
            "未知の発注先を送る");

        act.Should().Throw<ArgumentException>();
        store.GetCurrent().Should().BeEquivalentTo(
            before,
            "拒否時は部分適用しない（設定が一切変わらない）");
        log.GetHistory().Should().BeEmpty(
            "拒否された要求を履歴に積むと、実際には起きていない変更が監査上の事実になる");
    }

    // T-106（回帰）: 既知の 3 値は従来どおり受理される。allow-list を足したことで正当な変更まで
    // 止まっていないことを固定する（**塞ぎすぎ**は塞ぎ漏れと同じくらい危険である）。
    [Theory]
    [InlineData(BrokerProvider.InternalPaper)]
    [InlineData(BrokerProvider.MoomooReal)]
    [InlineData(BrokerProvider.MoomooSimulate)]
    public void 段階の既定発注先が既知の3値なら受理される(BrokerProvider mode)
    {
        var (service, store, log) = Create();

        service.UpdateStage(
            new StageSettings(TradingStage.Stage1Simulate, mode, TradingDefaults.FullCapitalCapRatio),
            "owner",
            "段階を設定する");

        store.GetCurrent().Stage.Mode.Should().Be(mode);
        log.GetHistory().Should().ContainSingle(e => e.ChangeType == SettingsChangeType.Stage);
    }

    // T-107（**否定形**・IADR-0161 決定3 の非対称を守る）: 書き込みを塞いだことで
    // **読み取り側まで例外へ倒していない**こと。読み取りを例外にすると設定行全体が失われ、
    // `GetCurrent` が 500 になってリスク判定そのものが止まる（#430 が採らないと明記した挙動）。
    [Fact]
    public void 書き込みを塞いでも読み取りは内蔵paperへ倒れたままである()
    {
        BrokerProviderResolution.Resolve((BrokerProvider?)(BrokerProvider)7).Should().Be(BrokerProvider.InternalPaper);
        BrokerProviderResolution.Resolve((BrokerProvider?)null).Should().Be(BrokerProvider.InternalPaper);
        BrokerProviderResolution.IsKnown((BrokerProvider)7).Should().BeFalse();
    }

    // T-108（**否定形**）: 新しい拒否コードを作っていないこと。#434 は「既存の `UnknownProvider` の
    // 意味に揃える／新しい拒否コードを追加しない」と定めており、列挙子が増えると拒否理由の分類が
    // 二重化する（画面・Discord・監査の写像先がすべてずれる）。
    [Fact]
    public void 拒否理由の列挙子を増やしていない()
    {
        Enum.GetNames<BrokerProviderChangeRejection>().Should().BeEquivalentTo(
            ["ReasonRequired", "LiveAcknowledgementMissing", "LivePhraseMismatch", "UnknownProvider"]);
    }
}
