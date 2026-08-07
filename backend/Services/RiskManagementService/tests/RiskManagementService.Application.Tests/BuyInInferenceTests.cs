using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）・決定15, #419, IADR-0159:
// 強制買戻し（buy-in）の**事後推定**。
//
// **これは検知ではなく推定である。** イベント検知の供給元が無いため、建玉の消失を自らの決済指示
//（約定履歴・処理中の決済承認）と突合して推定する。したがって本テスト群の中心は**否定形**である——
// 「自らの決済指示に**対応する**建玉消失を強制買戻しと誤認しないこと」（#419 の退行防止の最重要項目）。
public class BuyInInferenceTests
{
    private const string Symbol = "GME";
    private static readonly DateTimeOffset At = new(2026, 8, 7, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 8, 7);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => At;

        public DateOnly Today => BuyInInferenceTests.Today;
    }

    // --- 1. 純関数（突合そのもの）------------------------------------------------------------

    private static OpenPosition Short(int quantity) =>
        new(Symbol, Market.UnitedStates, TradeSide.Sell, quantity, 30m);

    private static BrokerPositionSnapshot BrokerShort(int quantity) =>
        new(Symbol, Market.UnitedStates, -quantity, 30m);

    private static LedgerFill Fill(TradeSide side, int quantity, PositionEffect effect, int minuteOffset) =>
        new(Symbol, Market.UnitedStates, side, effect, quantity, 30m, At.AddMinutes(minuteOffset));

    private static IReadOnlyList<BuyInInference.Outcome> Infer(
        IReadOnlyList<OpenPosition> ledger,
        IReadOnlyList<BrokerPositionSnapshot> broker,
        IReadOnlyList<LedgerFill>? fills = null,
        int inFlightClose = 0,
        IReadOnlyList<BuyInInferenceRecord>? latest = null) =>
        BuyInInference.Infer(
            ledger,
            broker,
            fills ?? [],
            new Dictionary<(string Symbol, Market Market), int>
            {
                [(Symbol, Market.UnitedStates)] = inFlightClose,
            },
            latest ?? []);

    // T-10-215（**否定形・最重要**）: FR-10, ADR-0016 決定4 —— **自らの決済指示に対応する建玉消失を
    // 強制買戻しと誤認しない。** 自分で手仕舞い・損切りをすれば約定が台帳に入り、台帳の空売り建玉も減るため、
    // 台帳とブローカは一致する。これが正常な決済と強制買戻しを区別する唯一の手段である。
    [Fact]
    public void 自らの決済約定で説明できる建玉の消失は強制買戻しと推定しない()
    {
        // 100 株の空売りのうち 40 株を自分で買い戻した（台帳・ブローカとも 60 株）。
        var outcomes = Infer(
            [Short(60)],
            [BrokerShort(60)],
            [Fill(TradeSide.Sell, 100, PositionEffect.Open, -120), Fill(TradeSide.Buy, 40, PositionEffect.Close, -10)]);

        outcomes.Should().BeEmpty();
    }

    // T-10-216: FR-10, ADR-0016 決定4 —— 自らの決済指示が**無い**消失を強制買戻しと推定する。
    [Fact]
    public void 自らの決済指示で説明できない建玉の消失を強制買戻しと推定する()
    {
        // 台帳は 100 株の空売りを保持しているのに、ブローカ側には 1 株も無い。
        var outcomes = Infer([Short(100)], []);

        outcomes.Should().ContainSingle();
        outcomes[0].NewlyInferredQuantity.Should().Be(100);
        outcomes[0].UnexplainedQuantity.Should().Be(100);
        outcomes[0].BrokerShortQuantity.Should().Be(0);
    }

    // T-10-217（**部分約定・分割決済の境界**）: FR-10, IADR-0159 決定2 —— ブローカが**一部だけ**買い戻した場合も
    // 推定する。「建玉が 0 になった時点」を待つと分割買戻しで永久に発火せず、過少推定になる。
    [Fact]
    public void ブローカが一部だけ買い戻した場合も数量差で推定する()
    {
        var outcomes = Infer([Short(100)], [BrokerShort(60)]);

        outcomes.Should().ContainSingle();
        outcomes[0].NewlyInferredQuantity.Should().Be(40);
    }

    // T-10-218（**部分約定・分割決済の境界**）: FR-10 —— 自らの決済が**部分約定**した場合、約定した分は台帳にも
    // 反映されるため説明済みであり、**残りの説明できない分だけ**を推定する。
    [Fact]
    public void 自らの決済の部分約定は説明済みとして扱い残りだけを推定する()
    {
        // 100 株の空売り。自分の決済指示 60 株のうち 60 株が約定済み（台帳 40 株）。
        // ブローカは 0 株＝さらに 40 株が説明できずに消えている。
        var outcomes = Infer(
            [Short(40)],
            [],
            [Fill(TradeSide.Sell, 100, PositionEffect.Open, -120), Fill(TradeSide.Buy, 60, PositionEffect.Close, -10)]);

        outcomes.Should().ContainSingle();
        outcomes[0].NewlyInferredQuantity.Should().Be(40);
    }

    // T-10-219（**否定形**）: FR-10, IADR-0159 決定1 —— **処理中の決済**（承認済みだが約定が台帳へ届いていない）は
    // 自らの決済指示そのものである。これを引かないと、約定の反映遅れという**通常運行の事象**が毎回
    // 強制買戻しとして推定される。
    [Fact]
    public void 処理中の決済で説明できる消失は強制買戻しと推定しない()
    {
        // 台帳 100 株・ブローカ 0 株だが、100 株の決済を発注済みで約定が台帳へ未着。
        var outcomes = Infer([Short(100)], [], inFlightClose: 100);

        outcomes.Should().BeEmpty();
    }

    // T-10-220（**否定形**）: FR-10 —— ブローカのほうが空売り建玉が**多い**乖離は強制買戻しではない
    //（建玉が増える方向であり、IADR-0118 の建玉突合が別途報告する）。
    [Fact]
    public void ブローカ側の空売りが台帳より多い乖離は推定しない()
    {
        Infer([Short(60)], [BrokerShort(100)]).Should().BeEmpty();
    }

    // T-10-221（**否定形**）: FR-10, ADR-0016 —— 強制買戻しは**空売り建玉にしか起こらない**。
    // ロング建玉の消失を推定対象にしない。
    [Fact]
    public void ロング建玉の消失は強制買戻しと推定しない()
    {
        var longPosition = new OpenPosition(Symbol, Market.UnitedStates, TradeSide.Buy, 100, 30m);

        Infer([longPosition], []).Should().BeEmpty();
    }

    // T-10-222: FR-10, IADR-0159 決定2 —— 同じ乖離を毎巡回で観測しても**推定は 1 回**である。
    // 台帳は自動では是正されないため乖離は解消せず、冪等でなければ 30 日の禁止が恒久禁止になる。
    [Fact]
    public void 同じ乖離を再観測しても新たな推定は生まれない()
    {
        var already = Record(unexplained: 100, newlyInferred: 100);

        Infer([Short(100)], [], latest: [already]).Should().BeEmpty();
    }

    // T-10-223: FR-10, IADR-0159 決定2 —— 段階的に消失した場合は**増分だけ**を新たな推定とする。
    [Fact]
    public void 段階的な追加の消失は増分だけを新たに推定する()
    {
        var already = Record(unexplained: 40, newlyInferred: 40);

        var outcomes = Infer([Short(100)], [], latest: [already]);

        outcomes.Should().ContainSingle();
        outcomes[0].NewlyInferredQuantity.Should().Be(60);   // 累計 100 − 既推定 40
        outcomes[0].UnexplainedQuantity.Should().Be(100);
    }

    // T-10-224: FR-11, ADR-0016 決定4 —— **推定の根拠**として、当該建玉に対する自らの決済約定を残す。
    // 建玉が最後に 0 になった時点より後の約定だけを載せる（別の建玉の決済を根拠に混ぜない）。
    [Fact]
    public void 突合した自らの決済約定を推定の根拠として残す()
    {
        var fills = new List<LedgerFill>
        {
            // 過去に完結した別の建玉（ショート 50 → 買い戻し 50 でフラット）。
            Fill(TradeSide.Sell, 50, PositionEffect.Open, -500),
            Fill(TradeSide.Buy, 50, PositionEffect.Close, -400),
            // 現在の建玉（ショート 100 のうち 40 を自分で買い戻し）。
            Fill(TradeSide.Sell, 100, PositionEffect.Open, -120),
            Fill(TradeSide.Buy, 40, PositionEffect.Close, -10),
        };

        var outcomes = Infer([Short(60)], [], fills);

        outcomes.Should().ContainSingle();
        outcomes[0].NewlyInferredQuantity.Should().Be(60);
        outcomes[0].CoveringFills.Should().ContainSingle("現在の建玉に対応する決済約定だけを根拠に載せる")
            .Which.Quantity.Should().Be(40);
    }

    // --- 2. 束ね（記録・禁止期限・イベント）----------------------------------------------------

    // T-10-225: FR-10, ADR-0016 決定4 —— 推定した銘柄を **30 日間**の空売り禁止へ登録する。
    [Fact]
    public void 推定した銘柄を30日間の空売り禁止へ登録する()
    {
        var store = new InMemoryBuyInInferenceStore();
        var service = NewService(LedgerWithShort(100, closedQuantity: 0), store);

        var events = service.Observe([], At);

        events.Should().ContainSingle();
        events[0].BanUntil.Should().Be(Today.AddDays(30), "ADR-0016 決定4 の禁止期間は 30 日である");
        store.GetBanUntil(Symbol, Market.UnitedStates).Should().Be(Today.AddDays(30));
        BuyInBanPolicy.IsBanned(Today, store.GetBanUntil(Symbol, Market.UnitedStates)).Should().BeTrue();
        BuyInBanPolicy.IsBanned(Today.AddDays(30), store.GetBanUntil(Symbol, Market.UnitedStates))
            .Should().BeFalse("解除日当日は禁止しない");
    }

    // T-10-226（**否定形**）: FR-10 —— 自らの決済指示で説明できる消失では、禁止も記録も生まれない。
    [Fact]
    public void 自らの決済で説明できる消失では禁止も記録も生まれない()
    {
        var store = new InMemoryBuyInInferenceStore();
        // 100 株の空売りのうち 100 株を自分で買い戻した（台帳もブローカも建玉なし）。
        var service = NewService(LedgerWithShort(100, closedQuantity: 100), store);

        service.Observe([], At).Should().BeEmpty();
        store.GetBanUntil(Symbol, Market.UnitedStates).Should().BeNull();
        store.CountInferredBetween(Today, Today).Should().Be(0);
    }

    // T-10-227: FR-10, IADR-0159 決定2/決定4 —— 乖離が解消しても**禁止期限は戻らない**
    //（30 日は経過でしか解けない・ADR-0016 決定10 の「期間の経過で解除される禁止状態」）。
    [Fact]
    public void 乖離が解消しても禁止期限は戻らない()
    {
        var store = new InMemoryBuyInInferenceStore();
        var ledger = LedgerWithShort(100, closedQuantity: 0);
        var service = NewService(ledger, store);

        service.Observe([], At).Should().ContainSingle();

        // 建玉がブローカ側に戻った（台帳の是正・建て直し）→ リセット行が入るが禁止は続く。
        service.Observe([BrokerShort(100)], At).Should().BeEmpty();

        store.GetBanUntil(Symbol, Market.UnitedStates).Should().Be(Today.AddDays(30));
        store.CountInferredBetween(Today, Today).Should().Be(1, "リセットは発生回数に数えない");
    }

    // T-10-228: FR-10, IADR-0159 決定4 —— **観測が届かない間も禁止は有効**である。
    // ブローカ照会が失敗すると観測自体が発行されず推定は動かないが、**期限は日付でのみ失効する**
    //（観測の不在で禁止が解けると fail-open になる）。
    [Fact]
    public void 観測が届かなくても既存の禁止は有効なまま残る()
    {
        var store = new InMemoryBuyInInferenceStore();
        var service = NewService(LedgerWithShort(100, closedQuantity: 0), store);
        service.Observe([], At).Should().ContainSingle();

        // 以後、観測が 1 件も届かない（照会失敗）。Observe は呼ばれない。
        store.GetBanUntil(Symbol, Market.UnitedStates).Should().Be(Today.AddDays(30));
        BuyInBanPolicy.IsBanned(Today.AddDays(29), store.GetBanUntil(Symbol, Market.UnitedStates))
            .Should().BeTrue();
    }

    // T-10-229（**決定15 の集計元**）: FR-06, ADR-0016 決定15（2026-08-06 追記）——
    // 「強制買戻しの発生回数」の集計元は**推定の件数**であり、`BuyInBanned`（拒否理由）の件数ではない。
    // **1 回の強制買戻しに対し、禁止期間 30 日のあいだ拒否は何度でも起こり得る。**
    // 拒否件数を集計元にすると実際より大きな数字が月報に載る。
    [Fact]
    public void 発生回数の集計元は推定の件数であり拒否や再観測の件数ではない()
    {
        var store = new InMemoryBuyInInferenceStore();
        var ledger = LedgerWithShort(100, closedQuantity: 0);
        var service = NewService(ledger, store);
        var violations = new InMemoryControlViolationObservationStore();

        // 1 回の強制買戻し（推定）。
        service.Observe([], At).Should().ContainSingle();

        // 同じ乖離を 11 回再観測し（台帳は自動では是正されない）、
        // その間に 12 回の空売り発注が BuyInBanned で拒否された。
        for (var i = 0; i < 11; i++)
        {
            service.Observe([], At).Should().BeEmpty();
        }

        for (var i = 0; i < 12; i++)
        {
            violations.Record(new OrderScreeningObservation(
                Guid.NewGuid(), BrokerProvider.MoomooSimulate, [RejectionReason.BuyInBanned]));
        }

        store.CountInferredBetween(Today, Today).Should().Be(
            1,
            "発生回数は推定した強制買戻しの件数である（拒否件数でも再観測の回数でもない・ADR-0016 決定15）");
        store.GetInferredBetween(Today, Today).Should().ContainSingle();
    }

    // --- 3. 供給経路の結線（推定 → 30 日禁止 → 発注審査の拒否理由）--------------------------------

    // T-10-244: FR-10, ADR-0016 決定4, #419, IADR-0159 決定5 —— **推定した禁止が発注審査に効く。**
    // #374 で実装した `BuyInBanned` は供給経路が無く一度も発動し得なかった（blocked-tasks の
    //「実装済みだが発動しない機能」）。推定台帳を審査へ結線したことで、推定した銘柄の新規空売りが止まる。
    [Fact]
    public void 推定した銘柄の新規空売りは発注審査で拒否される()
    {
        var store = new InMemoryBuyInInferenceStore();
        var service = NewService(LedgerWithShort(100, closedQuantity: 0), store);
        service.Observe([], At).Should().ContainSingle();

        var screening = NewScreeningService(store);
        var outcome = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), ShortEntryIntent(), "テスト判断", At));

        outcome.IsApproved.Should().BeFalse();
        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.BuyInBanned);
        // **クラス C（統制違反 0 件の計上対象）へ混ぜない**（ADR-0016 決定4/決定10）。
        outcome.Rejected.Reasons.Should().NotContain(RejectionReason.BannedSymbol);
        RejectionReasonClassification.CountsAsControlViolation(outcome.Rejected.Reasons).Should().BeFalse();
    }

    // T-10-245（**否定形**）: 推定が無い銘柄の新規空売りに `BuyInBanned` は立たない。
    // 立てば、日報・月報の「強制買戻しの発生有無」（決定15）が起きていない事象で水増しされる。
    [Fact]
    public void 推定が無い銘柄には強制買戻しの拒否理由が立たない()
    {
        var screening = NewScreeningService(new InMemoryBuyInInferenceStore());

        var outcome = screening.Screen(new TradeDecisionMade(Guid.NewGuid(), ShortEntryIntent(), "テスト判断", At));

        outcome.Rejected!.Reasons.Should().NotContain(RejectionReason.BuyInBanned);
        outcome.Rejected.Reasons.Should().Contain(
            RejectionReason.BorrowUnavailable, "借株照会の供給元が無い間はいずれにせよ通らない（フェイルクローズ）");
    }

    private static OrderScreeningService NewScreeningService(IBuyInInferenceStore store)
    {
        var settings = TradingDefaults.CreateSettings();
        var enabled = new HashSet<ProductType> { ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell };
        var settingsStore = new InMemoryRiskSettingsStore(
            settings with { Guard = settings.Guard with { EnabledProductTypes = enabled } });
        var portfolio = new FakePortfolioStateProvider(new PortfolioState { Capital = 100_000m });
        var builder = new PortfolioSnapshotBuilder(
            portfolio, new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            FakeBrokerAccountObservations.Margin());

        return new OrderScreeningService(
            settingsStore, builder, new InMemoryLockoutStore(), new FixedClock(),
            new WeekendBusinessCalendar(), patternDetector: null, buyInInferences: store);
    }

    private static OrderIntent ShortEntryIntent() =>
        new(Symbol, Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, BrokerProvider.MoomooSimulate,
            Quantity: 10, Price: 30m, PositionEffect.Open, StopLossPrice: 33m);

    // --- 補助 --------------------------------------------------------------------------------

    private static BuyInInferenceRecord Record(int unexplained, int newlyInferred) => new(
        Guid.NewGuid(), Symbol, Market.UnitedStates,
        LedgerShortQuantity: 100, BrokerShortQuantity: 0, InFlightCloseQuantity: 0,
        UnexplainedQuantity: unexplained, NewlyInferredQuantity: newlyInferred,
        BanUntil: newlyInferred > 0 ? Today.AddDays(30) : null,
        InferredOn: Today, ObservedAt: At, InferredAt: At);

    private static BuyInInferenceService NewService(
        InMemoryPortfolioLedgerStore ledger, IBuyInInferenceStore store) =>
        new(ledger, store, new InMemoryRiskSettingsStore(), new FixedClock());

    // 空売り <paramref name="quantity"/> 株を建て、そのうち <paramref name="closedQuantity"/> 株を自分で買い戻した台帳。
    private static InMemoryPortfolioLedgerStore LedgerWithShort(int quantity, int closedQuantity)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var openId = Guid.NewGuid();
        ledger.AppendApproval(openId, Intent(TradeSide.Sell, quantity, PositionEffect.Open), At.AddDays(-2));
        ledger.AppendFill(openId, "open-1", quantity, 30m, At.AddDays(-2));

        if (closedQuantity > 0)
        {
            var closeId = Guid.NewGuid();
            ledger.AppendApproval(closeId, Intent(TradeSide.Buy, closedQuantity, PositionEffect.Close), At.AddDays(-1));
            ledger.AppendFill(closeId, "close-1", closedQuantity, 30m, At.AddDays(-1));
        }

        return ledger;
    }

    private static OrderIntent Intent(TradeSide side, int quantity, PositionEffect effect) =>
        new(Symbol, Market.UnitedStates, side, ProductType.ShortSell, BrokerProvider.MoomooSimulate,
            quantity, 30m, effect, StopLossPrice: 33m, FxRateToBase: 1m);
}
