using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-20, FR-15, UC-06, ADR-0008, IADR-0070: 段階ゲート遷移サービスの結線を検証する。
// 承認ゲート・履歴永続化・fail-safe 昇格・撤退の自動安全側（kill switch 起動）を受け入れ基準へ写像する。
public class StageGateServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    private static (StageGateService svc, InMemoryStageGateStore ledger, InMemoryStagePerformanceStore perf,
        InMemoryControlViolationObservationStore violations, KillSwitchService kill,
        InMemoryStage1FillObservationStore fills, InMemoryStage1TradingDayObservationStore uptime)
        Build(TradingStage initial = TradingStage.Stage0Verification)
    {
        var ledger = new InMemoryStageGateStore(initial);
        var perf = new InMemoryStagePerformanceStore();
        // FR-20, #387, IADR-0148: 統制違反件数の供給元（観測ログ）。**未記録＝未供給**であり、
        // Stage 1 → 2 の昇格はこのストアに観測が入るまで通らない。
        var violations = new InMemoryControlViolationObservationStore();
        // FR-20, #386, IADR-0149: 取引件数の供給元（約定の観測ログ）。**未記録＝0 件**であり、
        // Stage 1 → 2 の昇格はこのストアに SIMULATE の新規建て約定が 100 件入るまで通らない。
        var fills = new InMemoryStage1FillObservationStore();
        // FR-20, #385, IADR-0150: 営業日数の供給元（稼働の観測ログ）。**未記録＝0 日**であり、
        // Stage 1 → 2 の昇格はこのストアに 60 営業日ぶんの稼働が積まれるまで通らない。
        var uptime = new InMemoryStage1TradingDayObservationStore();
        var killStore = new InMemoryKillSwitchStore();
        var clock = new FakeClock(Now, DateOnly.FromDateTime(Now.DateTime));
        var kill = new KillSwitchService(killStore, new InMemorySettingsChangeLog(), clock);
        var svc = new StageGateService(
            ledger, perf, violations, fills, uptime, TradingDefaults.CreateStagePolicy(), kill, clock);
        return (svc, ledger, perf, violations, kill, fills, uptime);
    }

    /// <summary>算入対象（moomoo SIMULATE）の審査 1 件＝クラス C 違反 0 件の供給を作る。</summary>
    private static OrderScreeningObservation CleanScreening() =>
        new(Guid.NewGuid(), BrokerProvider.MoomooSimulate, []);

    /// <summary>
    /// FR-20, #386, §4.1 条件3, IADR-0149: 取引件数の供給を作る。
    /// **件数は段階別実績の行ではなく約定の観測ログから来る**ため、テストも観測を積む。
    /// </summary>
    private static void RecordFills(
        InMemoryStage1FillObservationStore store,
        int count,
        BrokerProvider provider = BrokerProvider.MoomooSimulate,
        PositionEffect effect = PositionEffect.Open)
    {
        for (var i = 0; i < count; i++)
        {
            store.Record(new Stage1FillObservation(
                Guid.NewGuid(), new DateOnly(2026, 7, 1).AddDays(i % 28), provider, effect));
        }
    }

    /// <summary>
    /// FR-20, #385, §4.2, IADR-0150: 営業日数の供給を作る。
    /// **営業日数は段階別実績の行ではなく稼働の観測ログから来る**ため、テストも観測を積む
    /// （probe が 30 分ごとに成功した平日を <paramref name="days"/> 日ぶん作る）。
    /// </summary>
    private static void RecordUptimeDays(
        InMemoryStage1TradingDayObservationStore store,
        int days,
        BrokerProvider provider = BrokerProvider.MoomooSimulate)
    {
        var date = new DateOnly(2026, 3, 2); // 月曜
        for (var d = 0; d < days; d++)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                date = date.AddDays(1);
            }

            // 9:30〜16:00 ET を 30 分刻みで満たす（両仮説とも 100%）。
            for (var minute = Stage1SessionHypotheses.RegularSessionOpenMinuteOfDayEasternTime;
                 minute <= Stage1SessionHypotheses.RegularCloseMinuteOfDayEasternTime;
                 minute += 30)
            {
                store.CreditUptime(date, provider, minute, 30);
            }

            date = date.AddDays(1);
        }
    }

    [Fact]
    public void 承認者が空の遷移は拒否され台帳へ追記されない()
    {
        // 受け入れ基準: 承認なしに段階が遷移しない。
        var (svc, ledger, perf, _, _, _, _) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true });

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "  ");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.NoUserApproval);
        ledger.Load().History.Should().BeEmpty();
    }

    [Fact]
    public void バックテスト未合格では_Stage0から1へ昇格できない_fail_safe既定()
    {
        // 受け入れ基準（fail-safe）: 段階別実績未記録（BacktestPassed=false）では昇格を許可しない。
        var (svc, ledger, _, _, _, _, _) = Build();

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage0Verification);
    }

    [Fact]
    public void バックテスト合格を記録すれば承認で昇格し履歴に残る()
    {
        // 受け入れ基準: バックテスト合格した戦略のみ Stage 1 へ進める／遷移履歴が監査できる。
        var (svc, ledger, perf, _, _, _, _) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true });

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");

        result.Accepted.Should().BeTrue();
        result.ResultingSettings!.Mode.Should().Be(BrokerProvider.MoomooSimulate);
        var history = ledger.Load().History;
        history.Should().HaveCount(1);
        history[0].FromStage.Should().Be(TradingStage.Stage0Verification);
        history[0].ToStage.Should().Be(TradingStage.Stage1Simulate);
        history[0].Kind.Should().Be(StageTransitionKind.Promotion);
        history[0].ApprovedBy.Should().Be("owner");
        history[0].Sequence.Should().Be(1);
    }

    [Fact]
    public void 飛び級昇格は拒否される()
    {
        // Stage 0 → Stage 2 の飛び級は不可（1 段ずつ）。
        var (svc, _, perf, _, _, _, _) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true });

        var result = svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.PromotionMustBeSequential);
    }

    [Fact]
    public void 差し戻し_降格_は承認のみで受理される()
    {
        // 差し戻し（段階を下げる方向）は安全側のため合格基準不問で承認受理（ADR-0008）。
        var (svc, ledger, _, _, _, _, _) = Build(TradingStage.Stage1Simulate);

        var result = svc.RequestTransition(TradingStage.Stage0Verification, approver: "owner");

        result.Accepted.Should().BeTrue();
        result.Transition!.Kind.Should().Be(StageTransitionKind.Demotion);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage0Verification);
    }

    [Fact]
    public void 差し戻し受理で実DDの観測窓をリセットする()
    {
        // FR-20, ADR-0008, IADR-0103, #164: 実DD は単調非減少で累積するため、差し戻し（再検証のやり直し）で
        // 観測窓を区切らないと「撤退 → 降格 → 再昇格」の直後に過去の実DD で撤退が恒久的に再発火する。
        // 実DD 以外（backtest 由来・他の運用系）は温存する。
        var (svc, _, perf, _, _, _, _) = Build(TradingStage.Stage2MinimalLive);
        perf.Save(new StagePerformance
        {
            BacktestPassed = true,
            BacktestMaxDrawdownRatio = 0.10m,
            ObservedMaxDrawdownRatio = 0.20m,
        });

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");

        result.Accepted.Should().BeTrue();
        result.Transition!.Kind.Should().Be(StageTransitionKind.Demotion);
        var after = perf.GetCurrent();
        after.ObservedMaxDrawdownRatio.Should().Be(0m);
        after.BacktestPassed.Should().BeTrue();
        after.BacktestMaxDrawdownRatio.Should().Be(0.10m);
    }

    [Fact]
    public void 昇格受理では実DDの観測窓を保持する()
    {
        // IADR-0103: 昇格側でリセットすると撤退の証拠を消して緩む。安全側（厳しい側）に倒し、観測は維持する。
        var (svc, _, perf, _, _, _, _) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true, ObservedMaxDrawdownRatio = 0.20m });

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");

        result.Accepted.Should().BeTrue();
        perf.GetCurrent().ObservedMaxDrawdownRatio.Should().Be(0.20m);
    }

    [Fact]
    public void 受理されない遷移では実DDの観測窓を変更しない()
    {
        // IADR-0103: リセットは「受理された差し戻し」のみ。承認欠如で拒否された要求で観測が消えてはならない。
        var (svc, _, perf, _, _, _, _) = Build(TradingStage.Stage2MinimalLive);
        perf.Save(new StagePerformance { ObservedMaxDrawdownRatio = 0.20m });

        var result = svc.RequestTransition(TradingStage.Stage1Simulate, approver: "  ");

        result.Accepted.Should().BeFalse();
        perf.GetCurrent().ObservedMaxDrawdownRatio.Should().Be(0.20m);
    }

    [Fact]
    public void 実DDがバックテスト最大DDの倍率を超えると撤退で自動停止し降格提案する()
    {
        // 受け入れ基準: 差し戻し基準到達時に自動で安全側（停止・降格提案）に倒れる。
        var (svc, _, perf, _, kill, _, _) = Build(TradingStage.Stage2MinimalLive);
        perf.Save(new StagePerformance
        {
            BacktestMaxDrawdownRatio = 0.10m,
            ObservedMaxDrawdownRatio = 0.20m, // 0.10 × 1.5 = 0.15 を超過
        });

        var outcome = svc.EvaluateWithdrawal();

        outcome.Assessment.Triggered.Should().BeTrue();
        outcome.Assessment.HaltNewEntries.Should().BeTrue();
        outcome.Assessment.ProposedStage.Should().Be(TradingStage.Stage0Verification);
        // 自動で安全側＝kill switch が起動される（段階の実降格は行わない）。今回の呼び出しで新規に起動した。
        outcome.NewlyEngaged.Should().BeTrue();
        kill.GetState().Engaged.Should().BeTrue();
    }

    [Fact]
    public void 既に停止済みなら撤退評価は新規起動と判定しない_冪等()
    {
        // IADR-0083: 撤退が継続していても、既に起動済みなら NewlyEngaged=false（＝再通知の起点にならない）。
        var (svc, _, perf, _, _, _, _) = Build(TradingStage.Stage2MinimalLive);
        perf.Save(new StagePerformance
        {
            BacktestMaxDrawdownRatio = 0.10m,
            ObservedMaxDrawdownRatio = 0.20m,
        });

        svc.EvaluateWithdrawal().NewlyEngaged.Should().BeTrue(); // 1 回目: 新規起動
        svc.EvaluateWithdrawal().NewlyEngaged.Should().BeFalse(); // 2 回目: 起動済み
    }

    [Fact]
    public void 撤退基準に達していなければ自動停止しない()
    {
        // fail-safe 既定（実績なし・実DD 0）では Stage 2 の撤退は非発火＝kill switch は起動しない。
        var (svc, _, _, _, kill, _, _) = Build(TradingStage.Stage2MinimalLive);

        var outcome = svc.EvaluateWithdrawal();

        outcome.Assessment.Triggered.Should().BeFalse();
        outcome.NewlyEngaged.Should().BeFalse();
        kill.GetState().Engaged.Should().BeFalse();
    }

    [Fact]
    public void 現況は現段階の設定と昇格_撤退評価を返す()
    {
        var (svc, _, _, _, _, _, _) = Build();

        var status = svc.GetStatus();

        status.CurrentStage.Should().Be(TradingStage.Stage0Verification);
        status.CurrentSettings.Mode.Should().Be(BrokerProvider.InternalPaper);
        status.Promotion.TargetStage.Should().Be(TradingStage.Stage1Simulate);
        status.Promotion.Eligible.Should().BeFalse(); // 既定はバックテスト未合格
        status.History.Should().BeEmpty();
    }

    // FR-20, #387, §4.1 条件1, IADR-0148: **統制違反件数の集計が未供給なら Stage 1 → 2 は昇格しない。**
    // #385 / #386 の供給（60 営業日・100 件）が揃った実績を与えても止まることを固定する
    // ——#333 時点では ControlViolationCount が非 nullable の 0 であり、この状態で条件1 が通っていた。
    [Fact]
    public void 統制違反の観測が無ければ期間と件数が揃っていても昇格しない()
    {
        var (svc, ledger, perf, _, _, fills, uptime) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        RecordUptimeDays(uptime, 60);
        RecordFills(fills, 100);

        var result = svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.ControlViolationCountUnavailable);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage1Simulate);
    }

    // 正の確認: 算入対象（moomoo SIMULATE）の審査が観測されていれば「違反 0 件」を主張でき、昇格できる。
    // 未供給を止める判定が、正常な合格まで止めていないこと（過剰に厳しくないこと）の確認である。
    [Fact]
    public void 審査の観測があれば統制違反0件として昇格できる()
    {
        var (svc, ledger, perf, violations, _, fills, uptime) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        RecordUptimeDays(uptime, 60);
        RecordFills(fills, 100);
        violations.Record(CleanScreening());

        var result = svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner");

        result.Accepted.Should().BeTrue();
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage2MinimalLive);
    }

    // FR-20, #387, §4.1 条件1: クラス C の拒否が観測されていれば昇格は止まる（供給が効いていることの確認）。
    [Fact]
    public void クラスCの拒否が観測されていれば昇格は止まる()
    {
        var (svc, _, perf, violations, _, fills, uptime) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        RecordUptimeDays(uptime, 60);
        RecordFills(fills, 100);
        violations.Record(new OrderScreeningObservation(
            Guid.NewGuid(), BrokerProvider.MoomooSimulate, [RejectionReason.BannedSymbol]));

        var result = svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.ControlViolationsPresent);
        result.RejectionReasons.Should().NotContain(StageGateCriterion.ControlViolationCountUnavailable);
    }

    // FR-20, #387, §4.1「集計期間は Stage 1 の全期間」, IADR-0148 決定4:
    // 受理された遷移で観測窓を区切る。**前段階（Stage 0 等）の観測を Stage 1 の合格証跡へ持ち込まない。**
    // 区切った直後は未供給＝昇格しない（fail-safe）。
    [Fact]
    public void 受理された遷移は統制違反の観測窓を区切る()
    {
        var (svc, _, perf, violations, _, _, _) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true });
        violations.Record(new OrderScreeningObservation(
            Guid.NewGuid(), BrokerProvider.MoomooSimulate, [RejectionReason.BannedSymbol]));
        violations.GetTally()!.Count.Should().Be(1);

        svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner").Accepted.Should().BeTrue();

        violations.GetTally().Should().BeNull("段階が変わった時点で集計期間は仕切り直される（未供給へ戻る）");
    }

    // **否定形**: 受理されなかった遷移要求は観測窓を区切らない
    // （拒否された要求で違反の記録を消せるなら、拒否を繰り返して証跡を洗える）。
    [Fact]
    public void 受理されない遷移要求は観測窓を区切らない()
    {
        var (svc, _, _, violations, _, _, _) = Build(TradingStage.Stage1Simulate);
        violations.Record(new OrderScreeningObservation(
            Guid.NewGuid(), BrokerProvider.MoomooSimulate, [RejectionReason.BannedSymbol]));

        // 承認者が空＝受理されない要求。
        svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: " ").Accepted.Should().BeFalse();

        violations.GetTally()!.Count.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // FR-20, #386, §4.1 条件3 / §4.3, IADR-0149: 取引件数の供給
    // ------------------------------------------------------------------

    // 正: 約定の観測が段階ゲートの進捗（Stage1Progress.TradeCount）へ届く。
    // #334 までは集計関数が呼ばれておらず、進捗は恒久的に 0 だった（IADR-0142 残余リスク）。
    [Fact]
    public void 約定の観測が段階ゲートの取引件数へ供給される()
    {
        var (svc, _, perf, _, _, fills, uptime) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        RecordUptimeDays(uptime, 42);
        RecordFills(fills, 7);

        svc.GetStatus().Stage1Progress.TradeCount.Should().Be(7);
    }

    // **否定形（本 issue の最重要）**: 内蔵 paper の約定を混ぜても件数が汚染されない。
    // 100 件ぶんの paper 約定を積んでも進捗は 0 のままであり、昇格しない。
    [Fact]
    public void 内蔵paperの約定を積んでも取引件数は増えず昇格しない()
    {
        var (svc, ledger, perf, violations, _, fills, uptime) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        RecordUptimeDays(uptime, 60);
        violations.Record(CleanScreening());
        RecordFills(fills, 100, BrokerProvider.InternalPaper);

        svc.GetStatus().Stage1Progress.TradeCount.Should().Be(0);

        var result = svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.Stage1TradeCountInsufficient);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage1Simulate);
    }

    // 対照（肯定形）: 同じ量を SIMULATE で積めば昇格できる。
    // 否定形だけだと「常に 0 を返す」実装でも緑になるため、対照を置く。
    [Fact]
    public void SIMULATEの約定を100件積めば昇格できる()
    {
        var (svc, ledger, perf, violations, _, fills, uptime) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        RecordUptimeDays(uptime, 60);
        violations.Record(CleanScreening());
        RecordFills(fills, 100);

        svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner").Accepted.Should().BeTrue();
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage2MinimalLive);
    }

    // 否定形: 供給が無ければ 0 件＝昇格しない（水増しされない）。
    [Fact]
    public void 約定の観測が無ければ取引件数は0であり昇格しない()
    {
        var (svc, _, perf, violations, _, _, uptime) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        RecordUptimeDays(uptime, 60);
        violations.Record(CleanScreening());

        var result = svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.Stage1TradeCountInsufficient);
    }

    // FR-20, §4.2「起算点＝Stage 1 遷移日」, IADR-0149 決定3: 受理された遷移で約定の観測窓も区切る。
    // 前段階で積んだ件数を Stage 1 の合格証跡へ持ち込まない。
    [Fact]
    public void 受理された遷移は約定の観測窓を区切る()
    {
        var (svc, _, perf, _, _, fills, _) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true });
        RecordFills(fills, 3);
        fills.GetTradeCount().Should().Be(3);

        svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner").Accepted.Should().BeTrue();

        fills.GetTradeCount().Should().Be(0, "段階が変わった時点で件数は仕切り直される（起算点＝遷移日）");
    }

    // **否定形**: 受理されなかった遷移要求は約定の観測窓を区切らない。
    [Fact]
    public void 受理されない遷移要求は約定の観測窓を区切らない()
    {
        var (svc, _, _, _, _, fills, _) = Build(TradingStage.Stage1Simulate);
        RecordFills(fills, 3);

        svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: " ").Accepted.Should().BeFalse();

        fills.GetTradeCount().Should().Be(3);
    }

    // ------------------------------------------------------------------
    // FR-20, #385, §4.2, IADR-0150: 稼働営業日（期間カウント）の供給
    // ------------------------------------------------------------------

    // 正: 稼働の観測が段階ゲートの進捗（Stage1Progress.QualifiedTradingDays）へ届く。
    // #333 以来、この値は供給元が無く恒久的に 0 だった（IADR-0137 残余リスク）。
    [Fact]
    public void 稼働の観測が段階ゲートの営業日数へ供給される()
    {
        var (svc, _, perf, _, _, _, uptime) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        RecordUptimeDays(uptime, 12);

        svc.GetStatus().Stage1Progress.QualifiedTradingDays.Should().Be(12);
    }

    // **否定形**: 供給が途絶えれば 0 日＝期間条件が未充足＝昇格しない（水増しされない）。
    [Fact]
    public void 稼働の観測が無ければ営業日数は0であり昇格しない()
    {
        var (svc, _, perf, violations, _, fills, _) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        violations.Record(CleanScreening());
        RecordFills(fills, 100);

        svc.GetStatus().Stage1Progress.QualifiedTradingDays.Should().Be(0);

        var result = svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.Stage1TradingDaysInsufficient);
    }

    // **否定形（最重要）**: 内蔵 paper で 60 営業日ぶん稼働しても営業日数は増えず昇格しない。
    // 外部へ一度も発注していない稼働で合格証跡が積み上がらないこと（FR-12 / IADR-0142 決定2 の許可制）。
    [Fact]
    public void 内蔵paperで稼働しても営業日数は増えず除外日として別掲される()
    {
        var (svc, ledger, perf, violations, _, fills, uptime) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        violations.Record(CleanScreening());
        RecordFills(fills, 100);
        RecordUptimeDays(uptime, 60, BrokerProvider.InternalPaper);

        var progress = svc.GetStatus().Stage1Progress;
        progress.QualifiedTradingDays.Should().Be(0);
        progress.ExcludedInternalPaperDays.Should().Be(60, "SC-03 は「paper 稼働により N 日を除外」と併記する");

        var result = svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner");

        result.Accepted.Should().BeFalse();
        result.RejectionReasons.Should().Contain(StageGateCriterion.Stage1TradingDaysInsufficient);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage1Simulate);
    }

    // 対照（肯定形）: 同じ量を SIMULATE で稼働すれば期間条件は満たされ、件数・統制と揃えば昇格できる。
    // 否定形だけだと「常に 0 を返す」実装でも緑になるため、対照を置く。
    [Fact]
    public void SIMULATEで60営業日稼働すれば期間条件が満たされ昇格できる()
    {
        var (svc, ledger, perf, violations, _, fills, uptime) = Build(TradingStage.Stage1Simulate);
        perf.Save(new StagePerformance { BacktestPassed = true });
        violations.Record(CleanScreening());
        RecordFills(fills, 100);
        RecordUptimeDays(uptime, 60);

        svc.GetStatus().Stage1Progress.QualifiedTradingDays.Should().Be(60);
        svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: "owner").Accepted.Should().BeTrue();
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage2MinimalLive);
    }

    // FR-20, §4.2「起算点＝Stage 1 遷移日」, IADR-0150 決定4: 受理された遷移で稼働の観測窓も区切る。
    // 前段階で積んだ稼働を Stage 1 の合格証跡へ持ち込まない（統制違反・取引件数と同条件）。
    [Fact]
    public void 受理された遷移は稼働の観測窓を区切る()
    {
        var (svc, _, perf, _, _, _, uptime) = Build();
        perf.Save(new StagePerformance { BacktestPassed = true });
        RecordUptimeDays(uptime, 3);
        uptime.GetQualifiedTradingDayCount().Should().Be(3);

        svc.RequestTransition(TradingStage.Stage1Simulate, approver: "owner").Accepted.Should().BeTrue();

        uptime.GetQualifiedTradingDayCount().Should().Be(0, "段階が変わった時点で期間は仕切り直される（起算点＝遷移日）");
    }

    // **否定形**: 受理されなかった遷移要求は稼働の観測窓を区切らない。
    [Fact]
    public void 受理されない遷移要求は稼働の観測窓を区切らない()
    {
        var (svc, _, _, _, _, _, uptime) = Build(TradingStage.Stage1Simulate);
        RecordUptimeDays(uptime, 3);

        svc.RequestTransition(TradingStage.Stage2MinimalLive, approver: " ").Accepted.Should().BeFalse();

        uptime.GetQualifiedTradingDayCount().Should().Be(3);
    }
}
