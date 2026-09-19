using NotificationService.Features.Notifications;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Tests;

// FR-09, UC-01, UC-02, UC-06: イベント→通知メッセージの整形（種別・銘柄・拒否理由・重大度）を検証する。
public class NotificationFormatterTests
{
    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m);

    [Fact]
    public void 約定は取引実行として_Info_で整形される()
    {
        var e = new OrderExecuted(Guid.NewGuid(), "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate);

        var msg = NotificationFormatter.From(e);

        msg.Title.Should().Be("取引実行");
        msg.Severity.Should().Be(NotificationSeverity.Info);
        msg.Content.Should().Contain("ORD-1");
    }

    [Fact]
    public void 約定以外の終端状態は_Warning_になる()
    {
        var e = new OrderExecuted(Guid.NewGuid(), "ORD-1", OrderStatus.Rejected, 0, 0m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate);

        NotificationFormatter.From(e).Severity.Should().Be(NotificationSeverity.Warning);
    }

    [Fact]
    public void 発注拒否はリスク統制として理由つきで整形される()
    {
        var reasons = new[] { RejectionReason.KillSwitchActive, RejectionReason.DailyLossLimitReached };
        var e = new OrderRejected(Guid.NewGuid(), Intent(), reasons, DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Title.Should().Contain("リスク統制");
        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Content.Should().Contain(nameof(RejectionReason.KillSwitchActive));
        msg.Content.Should().Contain("AAPL");
    }

    [Fact]
    public void 損切りライン到達はリスク統制として_Critical_で整形される()
    {
        var e = new StopLossTriggered(Guid.NewGuid(), "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Title.Should().Contain("損切り");
        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("7203");
    }

    // UC-01, FR-09, FR-07, #210: 日報未確定による取引スキップは確定を促す Warning として整形される。
    [Fact]
    public void 日報未確定は確定を促す_Warning_で整形される()
    {
        var e = new DailyPolicyUnconfirmed(new DateOnly(2026, 7, 20), DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Title.Should().Contain("日報未確定");
        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Content.Should().Contain("2026-07-20");
        msg.Content.Should().Contain("確定");
    }

    // FR-06/07/09, UC-03〜05, IADR-0116, #280: 報告書ドラフトの提示は確定依頼として整形される。
    [Fact]
    public void 報告書ドラフトの提示は要約と確定依頼を含む_Info_で整形される()
    {
        var e = new ReportDraftPresented(
            "daily-2026-07-29", "Daily", "2026-07-29",
            "日報 2026-07-29（承認待ち）\n実現損益（税引後・費用込み）: +12,300 円", 3, DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Title.Should().Contain("承認待ち");
        msg.Severity.Should().Be(NotificationSeverity.Info);
        msg.Content.Should().Contain("+12,300 円");        // 発行側で組み立てた要約をそのまま載せる
        msg.Content.Should().Contain("daily-2026-07-29");
        msg.Content.Should().Contain("版 3");               // 確定 API の expectedVersion（IADR-0024）
        msg.Content.Should().Contain("確定");
    }

    // FR-06/09, #840, #866, IADR-0352 決定 5: **未供給の入力があるドラフトの提示は Warning で出す。**
    // 警告文は本文に入っているが、重大度が Info のままでは定常通知に埋もれる（埋もれない経路で出す、は
    // LlmFallbackFired（ADR-0017 決定4-(2)）で既に採っている形）。
    [Fact]
    public void 未供給の入力があるドラフトの提示は_Warning_で通知する()
    {
        var e = new ReportDraftPresented(
            "monthly-2026-07", "Monthly", "2026-07",
            "月報 2026-07（承認待ち）\n実現損益（税引後・費用込み）: +12,300 円"
            + $"\n{ReportSummaryMarkers.UnsuppliedWarningPrefix}（確定の前に本文を確認してください）: 散文（LLM）",
            1, DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Severity.Should().Be(NotificationSeverity.Warning);
        // 要約はそのまま載せる（警告行は発行側が組み立てている）。
        msg.Content.Should().Contain(ReportSummaryMarkers.UnsuppliedWarningPrefix);
        msg.Content.Should().Contain("確定するまで取引方針は変わりません");
    }

    [Fact]
    public void 未供給が無いドラフトの提示は_Info_のまま通知する()
    {
        // 🔴 否定形: 常に Warning にはしない（毎回警告だと警告の意味が消える）。
        var e = new ReportDraftPresented(
            "daily-2026-07-29", "Daily", "2026-07-29",
            "日報 2026-07-29（承認待ち）\n実現損益（税引後・費用込み）: +12,300 円", 3, DateTimeOffset.UtcNow);

        NotificationFormatter.From(e).Severity.Should().Be(NotificationSeverity.Info);
    }

    [Fact]
    public void 報告書ドラフトの提示は確定前に方針が変わらないことを明示する()
    {
        // ADR-0003: 確定するまで取引方針は変わらない。通知を読んだ利用者が「もう適用された」と誤解しないようにする。
        var e = new ReportDraftPresented("weekly-2026-W31", "Weekly", "2026-W31", "週報の要約", 1, DateTimeOffset.UtcNow);

        NotificationFormatter.From(e).Content.Should().Contain("確定するまで取引方針は変わりません");
    }

    [Fact]
    public void 建玉の乖離は双方の数量と是正しないことを明示する()
    {
        // FR-05/FR-09/FR-10, #292, IADR-0118: 自動是正しないため、通知が検知の唯一の出口になる。
        // どちらが正しいかを断定せず双方の数量を並べ、利用者が判断できるようにする。
        var e = new PositionReconciliationDrift(
            [
                new PositionDriftItem("AAPL", Market.UnitedStates, 0, 4072, PositionDriftKind.BrokerOnly),
                new PositionDriftItem("7203", Market.Japan, 100, 80, PositionDriftKind.QuantityMismatch),
            ],
            new DateTimeOffset(2026, 7, 30, 6, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        // 台帳が誤っていれば統制上限の判定そのものが狂うため Critical。
        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("AAPL").And.Contain("4072");
        msg.Content.Should().Contain("7203").And.Contain("100").And.Contain("80");
        msg.Content.Should().Contain("自動是正は行いません");
    }

    // FR-09, FR-10, UC-06, #330, IADR-0133 決定7: 維持率割れの自動縮小（**記録先 2: Discord 通知**）。
    // 利用者の承認を待たずに決済するため、通知が「建玉が減ったこと」を利用者が知る最初の経路になる。
    // 決済前後の維持率・閾値・回復目標・決済した建玉を本文に出す（数値が無ければ規則どおりの作動を確かめられない）。
    [Fact]
    public void 維持率割れの自動縮小は決済前後の維持率と建玉をCriticalで通知する()
    {
        var e = new MaintenanceMarginReductionExecuted(
            Guid.NewGuid(), RatioBefore: 0.40m, Threshold: 0.40m, RecoveryTarget: 0.45m, RatioAfter: 0.4504m,
            [new MaintenanceMarginReductionItem(
                "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, 112, 100m, 3_360m)],
            new DateTimeOffset(2026, 8, 4, 14, 30, 0, TimeSpan.Zero));

        var msg = NotificationFormatter.From(e);

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("40.0%").And.Contain("45.0%").And.Contain("45.0%");
        msg.Content.Should().Contain("AAPL").And.Contain("ショート").And.Contain("112").And.Contain("3,360.00");
        msg.Content.Should().Contain("承認と AI の判断は介在していません");
    }

    // T-10-242: FR-09, FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159 ——
    // 強制買戻しの通知は**必ず「推定」と明示する**。決定4 の改訂は「イベントとしての検知は供給元が無い」ため
    // 事後の突合による推定へ切り替え、「**推定であることを運用者へ示す**（『強制買戻しと推定』と明示し、
    // **確定事実として扱わない**）」と定めた。断定した通知は運用者に誤った確信を与える。
    [Fact]
    public void 強制買戻しの通知は推定であることを明示する()
    {
        var e = new BuyInInferred(
            Guid.NewGuid(), "GME", Market.UnitedStates,
            LedgerShortQuantity: 100, BrokerShortQuantity: 0, InFlightCloseQuantity: 0,
            UnexplainedQuantity: 100, NewlyInferredQuantity: 100,
            CoveringFills: [new BuyInCoveringFill(TradeSide.Buy, 40, 30m, new DateTimeOffset(2026, 8, 6, 1, 0, 0, TimeSpan.Zero))],
            BanUntil: new DateOnly(2026, 9, 6),
            ObservedAt: new DateTimeOffset(2026, 8, 7, 6, 0, 0, TimeSpan.Zero),
            InferredAt: new DateTimeOffset(2026, 8, 7, 6, 0, 1, TimeSpan.Zero));

        var msg = NotificationFormatter.From(e);

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Title.Should().Contain("推定");
        msg.Content.Should().Contain("推定");
        msg.Content.Should().Contain("確定した事実として扱わないでください");
        msg.Content.Should().Contain("GME").And.Contain("2026-09-06");
        msg.Content.Should().Contain("100").And.Contain("説明できない消失");
    }

    // --- FR-10, FR-17, #381, ADR-0022 決定2・決定5, IADR-0196: 為替の情報源の劣化 ---

    private static readonly DateTimeOffset FxT0 = new(2026, 8, 15, 3, 0, 0, TimeSpan.Zero);

    // 🔴 **Warning であって Critical ではない。** Critical にすると損切り到達（実際に止まる事象）と
    // 同じ重みになり、**本当に止まったときの通知が埋もれる**。
    [Fact]
    public void 為替のフォールバック切替は_Warning_で劣化の内容まで書く()
    {
        var msg = NotificationFormatter.From(new FxRateSourceFellBack("USD", "fred", 2, 2, FxT0));

        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Content.Should().Contain("fred");
        // 「フォールバックした」だけでは受け手が影響を判断できない。
        msg.Content.Should().Contain("週次");
        msg.Content.Should().Contain("新規建ては止まっていません");
    }

    // ADR-0022 決定2 は事実だけでなく**期間**の記録を求めている。
    [Fact]
    public void 為替の復帰は_Info_でフォールバック期間を書く()
    {
        var msg = NotificationFormatter.From(
            new FxRateSourcePrimaryRestored("USD", "boj", FxT0.AddHours(-6), FxT0));

        msg.Severity.Should().Be(NotificationSeverity.Info);
        msg.Content.Should().Contain("6 時間");
    }

    // 🔴 **「止まった」と読ませない。** 警告域は続行する（ADR-0022 決定5）。
    [Fact]
    public void 為替の鮮度警告は_止まっていないことと停止の上限を併記する()
    {
        var msg = NotificationFormatter.From(new FxRateStale("USD", FxT0.AddDays(-7), 7, 5, 30, FxT0));

        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Content.Should().Contain("新規建ては止まっていません");
        // 受け手が緊急度を判断できること。
        msg.Content.Should().Contain("30");
    }

    // --- #381 停止側 / IADR-0198 決定1 ------------------------------------------------------------

    // 🔴 **同じイベント型でも、止まったなら文面を変える。**
    // 警告と同じ件名・同じ重大度で流すと、**統制が発動した通知が日々の警告に埋もれる。**
    [Fact]
    public void 為替の鮮度切れは_Critical_で停止したことを書く()
    {
        var msg = NotificationFormatter.From(
            new FxRateStale("USD", FxT0.AddDays(-31), 31, 5, 30, FxT0, EntryBlocked: true));

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Title.Should().Contain("新規建てを停止");
        msg.Content.Should().Contain("新規建てを停止しました");
        // 手仕舞いまで止まったと読ませない（ADR-0022 決定5）。
        msg.Content.Should().Contain("手仕舞い・損切りは止めていません");
    }

    // 🔴 **否定形。** 警告域が停止側の文面へ引きずられていないこと（読み分けが壊れると意味が無い）。
    [Fact]
    public void 為替の鮮度警告は_停止したとは書かない()
    {
        var msg = NotificationFormatter.From(new FxRateStale("USD", FxT0.AddDays(-7), 7, 5, 30, FxT0));

        msg.Severity.Should().NotBe(NotificationSeverity.Critical);
        msg.Content.Should().NotContain("新規建てを停止しました");
    }

    // 🔴 **取引そのものの通知**（決定3）。状態の通知とは別に、**1 件ずつ**飛ぶ。
    [Fact]
    public void 鮮度切れでの決済は_観測日と乖離の可能性を書く()
    {
        var msg = NotificationFormatter.From(
            new PositionClosedWithStaleFxRate("7203", Market.Japan, "JPY", 300, 0.0067m, FxT0.AddDays(-31), 31, FxT0));

        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Content.Should().Contain("7203");
        msg.Content.Should().Contain("観測日");
        msg.Content.Should().Contain("乖離し得ます");
    }

    // ===== FR-05, FR-09, FR-10, #331, IADR-0210/0211: 見送り・保護逆指値の通知 =====

    private static readonly DateTimeOffset StopT0 = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    private static OrderIntent StopIntent(PositionEffect effect = PositionEffect.Open) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 1_000m, effect, StopLossPrice: 950m);

    [Fact]
    public void 発注見送りは_再試行されないことを明記したWarningになる()
    {
        var msg = NotificationFormatter.From(new OrderDispatchForgone(
            Guid.NewGuid(), StopIntent(), OrderDispatchForgoneReason.BrokerUnavailable, StopT0));

        msg.Severity.Should().Be(NotificationSeverity.Warning,
            "建玉は増えておらずリスクは未発生（Critical の埋没を防ぐ・IADR-0211）");
        msg.Title.Should().Contain("見送り");
        msg.Content.Should().Contain("再試行されません");
    }

    [Fact]
    public void 保護逆指値の発注はInfoで通知される()
    {
        var msg = NotificationFormatter.From(new ProtectiveStopPlaced(
            Guid.NewGuid(), Guid.NewGuid(), "stop-1", StopIntent(PositionEffect.Close), 950m, 1, StopT0));

        msg.Severity.Should().Be(NotificationSeverity.Info);
        msg.Content.Should().Contain("950");
    }

    // FR-10, ADR-0040 決定1（S2）, #819, IADR-0342 決定6: 免除は Warning（保護喪失の Critical と分ける）で、
    // 手法 S2・ペーパーで免除・「決済しない」が読める。
    [Fact]
    public void 保護逆指値の免除は手法と決済しないことが読めるWarningになる()
    {
        var msg = NotificationFormatter.From(new ProtectiveStopWaived(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, 10, 950m,
            StopLossExecutionMethod.NoProtectiveStop, BrokerProvider.MoomooSimulate, StopT0));

        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Title.Should().Contain("ペーパーで免除").And.Contain("S2");
        msg.Content.Should().Contain("決済しません").And.Contain("950").And.Contain("MoomooSimulate");
    }

    [Fact]
    public void 保護喪失のNoneは直ちに確認を求めるCriticalになる()
    {
        var msg = NotificationFormatter.From(new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.None, 10, null, null, StopT0));

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("直ちに確認");
    }

    // T-10-409, FR-10, #848, IADR-0117（2026-09-19 追記・改定 7）: 成行手仕舞いの結果が未確認のとき、
    // 「解消に失敗した」と伝えてはならない（読んだ人が手で成行を重ね、二重決済でショート化する）。
    [Fact]
    public void 保護喪失の成行手仕舞いが未確認なら_失敗とは言わず重ねる前の確認を求めるCriticalになる()
    {
        var closeDecisionId = Guid.NewGuid();
        var msg = NotificationFormatter.From(new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.CloseDispatchIndeterminate,
            10, closeDecisionId, StopIntent(PositionEffect.Close), StopT0));

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Title.Should().Contain("未確認").And.NotContain("建玉を解消");
        msg.Content.Should().Contain("届いたか不明").And.Contain("重ねません").And.Contain("証券会社の画面")
            .And.Contain(closeDecisionId.ToString());
        msg.Content.Should().NotContain("解消にも失敗", "失敗と読めると手で成行を重ねてしまう");
        msg.Content.Should().NotContain("手仕舞いました", "手仕舞い済みも主張しない");
        // T-10-451, IADR-0117（改定 9）: 据え置きが続くあいだ約 1 時間ごとに再通知する。
        // 再通知を「もう 1 本送った」と読ませない（同じ CloseDecisionId＝同じ 1 本の成行）。
        msg.Content.Should().Contain("約 1 時間ごと").And.Contain("新しい発注ではありません");
    }

    [Fact]
    public void 保護喪失の建玉解消は解消内容が読めるCriticalになる()
    {
        var msg = NotificationFormatter.From(new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates,
            ProtectiveStopLossCause.RejectedAtEntry, ProtectiveStopRemediation.PositionClosed,
            10, Guid.NewGuid(), StopIntent(PositionEffect.Close), StopT0));

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("手仕舞いました");
    }

    [Fact]
    public void 保護喪失のエントリー取消は建玉が生じていないことを伝える()
    {
        // 3 つの対処（取消・手仕舞い・None）は利用者の受け取り方がまったく違う。
        // 取消は**建玉が生じていない**＝損益に影響しないことを読み取れなければ、不要な確認作業を招く。
        var msg = NotificationFormatter.From(new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates,
            ProtectiveStopLossCause.RejectedAtEntry, ProtectiveStopRemediation.EntryCancelled,
            10, null, null, StopT0));

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("建玉は生じていません");
    }

    [Theory]
    [InlineData(OrderDispatchForgoneReason.BrokerUnavailable, "接続")]
    [InlineData(OrderDispatchForgoneReason.StopLossPriceMissing, "損切り価格")]
    [InlineData(OrderDispatchForgoneReason.StopOrderUnsupported, "逆指値に対応")]
    // FR-10, ADR-0040 決定1, #819, IADR-0342 決定4: 手法による見送りは「S0 へ戻す」が対処である。
    [InlineData(OrderDispatchForgoneReason.StopLossMethodNotPermitted, "S0 へ戻して")]
    // 🔴 T-10-504, FR-10, FR-05, ADR-0016, #864, IADR-0355: 決済を実建玉と突き合わせて止めた 2 つ。対処は再発注ではなく
    // 「台帳とブローカーのどちらが正しいかを確かめる」であり、他の 4 つと読み分けられなければならない。
    [InlineData(OrderDispatchForgoneReason.BrokerPositionAbsent, "裸のショート")]
    [InlineData(OrderDispatchForgoneReason.BrokerPositionsIndeterminate, "建玉を照会できません")]
    public void 見送りの理由は日本語で読み分けられる(OrderDispatchForgoneReason reason, string expected)
    {
        // 見送りは 3 つの原因で起こり、**対処がそれぞれ違う**（OpenD の復旧／判断側の損切り価格の欠落／
        // ブローカー構成の誤り）。列挙子名だけを出すと利用者は何をすべきか判断できない。
        var msg = NotificationFormatter.From(new OrderDispatchForgone(
            Guid.NewGuid(), StopIntent(), reason, StopT0));

        msg.Title.Should().Contain(expected);
        msg.Content.Should().Contain(expected);
    }

    // 🔴 T-10-504, FR-10, #864, IADR-0355 決定3: **建玉を照会できずに見送った決済だけは Critical** である
    // （建玉が残ったまま手仕舞いが出ておらず、他に鳴る通知が 1 本も無い）。建玉が無いことを**確認して**
    // 見送った側は Warning のまま —— 同時に乖離の Critical が鳴るため、二重に立てると本当に止まった事象が埋もれる。
    [Theory]
    [InlineData(OrderDispatchForgoneReason.BrokerPositionsIndeterminate, NotificationSeverity.Critical)]
    [InlineData(OrderDispatchForgoneReason.BrokerPositionAbsent, NotificationSeverity.Warning)]
    [InlineData(OrderDispatchForgoneReason.BrokerUnavailable, NotificationSeverity.Warning)]
    public void 建玉を照会できずに見送った決済だけ重大度が上がる(
        OrderDispatchForgoneReason reason, NotificationSeverity expected)
    {
        var msg = NotificationFormatter.From(new OrderDispatchForgone(
            Guid.NewGuid(), StopIntent(), reason, StopT0));

        msg.Severity.Should().Be(expected);
    }

    // 🔴 否定形（#331）: 損切り到達の通知は「システムが決済した」と読ませない。
    // FR-10, ADR-0040 決定1, #820（#826 項目 2）, IADR-0344 決定7: 決済するかは建玉ごとの手法で決まり、検知側は手法を知らない。
    // **ブローカーの逆指値が決済すると断定しない**（S1 / S2 の建玉で誤りになる）——3 手法の帰結を列挙する。
    [Fact]
    public void 損切り到達の通知は手法ごとの帰結を列挙しブローカーが決済すると断定しない()
    {
        var msg = NotificationFormatter.From(new StopLossTriggered(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 940m, 950m, StopT0));

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("S0＝ブローカー側の逆指値が実行（システムは発注しない）")
            .And.Contain("S1＝システムが成行で決済")
            .And.Contain("S2＝**システムもブローカーも決済しない（手動で決済してください）**");
        msg.Content.Should().NotContain("決済はブローカー側の逆指値が実行します");
    }

    // FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定8: 配置は Warning で「システム停止中は決済されない」が読める。
    [Fact]
    public void ソフトウェア逆指値の配置はシステム停止中は決済されないことが読めるWarningになる()
    {
        var msg = NotificationFormatter.From(new SoftwareStopArmed(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, 10, 950m,
            BrokerProvider.MoomooSimulate, StopT0));

        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Title.Should().Contain("S1");
        msg.Content.Should().Contain("システム停止中は決済されません").And.Contain("950").And.Contain("MoomooSimulate");
    }

    // FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定5・決定8: 決済の発注・取消は Warning、拒否の打ち切りだけが Critical。
    [Theory]
    [InlineData(SoftwareStopOutcome.ClosePlaced, NotificationSeverity.Warning, "成行の決済注文を発注しました")]
    [InlineData(SoftwareStopOutcome.EntryCancelled, NotificationSeverity.Warning, "建玉は生じていません")]
    [InlineData(SoftwareStopOutcome.CloseRejected, NotificationSeverity.Critical, "建玉が無保護で残っています")]
    [InlineData(SoftwareStopOutcome.EntryMissing, NotificationSeverity.Critical, "決済は出していません")]
    // #820 の 4 巡目監査, IADR-0344 追記(4) 決定9: 据え置きが猶予を過ぎた（無音の失敗を残さない）。
    [InlineData(SoftwareStopOutcome.CloseStalled, NotificationSeverity.Critical, "猶予を過ぎても成行の決済を発注できていません")]
    // #820 の 5 巡目監査, IADR-0344 追記(5): 外部要因で保護対象を減らした（無音にしない・決済は出していない）。
    [InlineData(SoftwareStopOutcome.ProtectionReduced, NotificationSeverity.Warning, "保護記録が守る株数をその分だけ減らしました")]
    // #820 の 8 巡目監査, IADR-0344 追記(8) 決定3: 帳簿では守っているのに 1 株も動かせない状態が猶予を過ぎた。
    [InlineData(SoftwareStopOutcome.ProtectionSuspended, NotificationSeverity.Critical, "1 株も決済できない状態**が続いています")]
    // T-10-492（#820 の 10 巡目監査, IADR-0344 追記(9) 決定3）: どの保護記録も主張していない建玉の**検知**。
    // 🔴 是正ではないので Critical ではなく Warning であり、「決済しません」と明記する。
    [InlineData(SoftwareStopOutcome.UnattributedPosition, NotificationSeverity.Warning, "どの保護記録も主張していません")]
    public void ソフトウェア逆指値の発動結果は結末ごとの重みと文言になる(
        SoftwareStopOutcome outcome, NotificationSeverity severity, string expected)
    {
        var msg = NotificationFormatter.From(new SoftwareStopExecuted(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, outcome, 10, 950m, 940m, 1,
            Guid.NewGuid(), "close-1", null, StopT0));

        msg.Severity.Should().Be(severity);
        msg.Content.Should().Contain(expected).And.Contain("950");
    }

    // ---- FR-09, UC-03, ADR-0003, IADR-0240 決定11, #774: 報告書確定の確定者の表示 ----

    private static readonly DateTimeOffset ConfirmedAt = new(2026, 9, 11, 4, 36, 0, TimeSpan.Zero);

    [Fact]
    public void Bot経由の確定は_操作した利用者と認可の主体の両方を表示する()
    {
        var e = new ReportConfirmed(
            "daily-2026-09-10", "Daily", "developer", 1, ConfirmedAt, AuthorizedBy: "ai-stock-trading-owner");

        var msg = NotificationFormatter.From(e);

        msg.Content.Should().Be(
            "Daily 報告書 daily-2026-09-10 が確定しました（developer・ai-stock-trading-owner 経由・前提条件 v1）。");
        msg.Content.Should().NotContain("unknown");
    }

    [Fact]
    public void 利用者本人の確定は従来どおり確定者だけを表示する()
    {
        var e = new ReportConfirmed("daily-2026-09-10", "Daily", "owner", 1, ConfirmedAt);

        NotificationFormatter.From(e).Content.Should().Be(
            "Daily 報告書 daily-2026-09-10 が確定しました（owner・前提条件 v1）。");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("  ")]
    public void 確定者が分からないときは_unknown_ではなく確定者不明と表示する(string actor)
    {
        var e = new ReportConfirmed("daily-2026-09-10", "Daily", actor, 1, ConfirmedAt);

        var msg = NotificationFormatter.From(e);

        msg.Content.Should().Be("Daily 報告書 daily-2026-09-10 が確定しました（確定者不明・前提条件 v1）。");
        msg.Content.Should().NotContain("unknown");
    }

    [Fact]
    public void 操作者を添えない旧版_Bot_の確定は_誰の資格で確定されたかを表示する()
    {
        // 報告書サービスは unknown へ倒す前に client:<azp> を確定者にする。表示はそのまま出す。
        var e = new ReportConfirmed("daily-2026-09-10", "Daily", "client:ai-stock-trading-owner", 1, ConfirmedAt);

        NotificationFormatter.From(e).Content.Should().Be(
            "Daily 報告書 daily-2026-09-10 が確定しました（client:ai-stock-trading-owner・前提条件 v1）。");
    }
}
