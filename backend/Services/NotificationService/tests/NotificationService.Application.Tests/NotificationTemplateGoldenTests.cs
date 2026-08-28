using System.Globalization;
using System.Reflection;
using NotificationService.Application.Services;
using NotificationService.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Application.Tests;

// FR-09, #341, IADR-0242: **通知テンプレートのゴールデンテスト（必須項目の欠落検知）。**
//
// 🔴 既存の `NotificationFormatterTests` は部分一致（`Should().Contain(...)`）であり、
// **テンプレートを 1 本足しても検査は 1 件も増えなかった**。「必須項目の欠落検知」になっていない。
//
// 本ファイルは 2 つを同時に成立させる。
//   ① **全文ゴールデン**（`Title` / `Content` / `Severity` の完全一致）——欠落は差分として必ず現れる。
//   ② **母集合をリフレクションで引く網羅**——ゴールデン表に載っていない `From` オーバーロードがあれば落ちる。
//
// 🔴 **数を書かない**（`NotificationConsumerCoverageTests` と同じ規律）。母集合はコードから引く。
// テンプレートを足した人がゴールデンを足し忘れれば**赤になる**——「ゴールデンテストがある」という
// 記録だけが残る状態にしない。
public class NotificationTemplateGoldenTests
{
    // IADR-0242 決定5: ゴールデンは決定論的でなければならない。時刻・数値はすべて固定値、
    // 実行環境のロケールに依らないよう文化を固定する（テンプレート側にも文化依存の書式は残していない）。
    public NotificationTemplateGoldenTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }

    private static readonly Guid Id = new("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset T = new(2026, 8, 28, 3, 0, 0, TimeSpan.Zero);

    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m);

    // ゴールデン表。1 行 = 1 テンプレート（重大度が分岐するものは分岐ごとに 1 行）。
    // Key は表示名、Value は (イベント, 期待メッセージ)。**期待値は完全一致で突き合わせる。**
    private static readonly Dictionary<string, (object Event, NotificationMessage Expected)> Golden = new()
    {
        ["OrderExecuted/Filled"] = (
            new OrderExecuted(Id, "ORD-1", OrderStatus.Filled, 10, 1050m, T, BrokerProvider.MoomooSimulate),
            new NotificationMessage(
                "取引実行",
                "約定 Filled 数量10@1050（OrderId=ORD-1・DecisionId=11111111-1111-1111-1111-111111111111）",
                NotificationSeverity.Info)),

        ["OrderExecuted/Rejected"] = (
            new OrderExecuted(Id, "ORD-2", OrderStatus.Rejected, 0, 0m, T, BrokerProvider.MoomooSimulate),
            new NotificationMessage(
                "取引実行",
                "約定 Rejected 数量0@0（OrderId=ORD-2・DecisionId=11111111-1111-1111-1111-111111111111）",
                NotificationSeverity.Warning)),

        ["OrderRejected"] = (
            new OrderRejected(
                Id, Intent(), [RejectionReason.KillSwitchActive, RejectionReason.DailyLossLimitReached], T),
            new NotificationMessage(
                "リスク統制: 発注拒否",
                "AAPL 拒否: KillSwitchActive,DailyLossLimitReached（DecisionId=11111111-1111-1111-1111-111111111111）",
                NotificationSeverity.Warning)),

        // 🔴 #331, IADR-0210: 損切りはブローカー側逆指値へ一本化した。**「システムは決済注文を発行しない」
        // ことを本文に書く**——書かないと、通知を見た利用者が「システムが決済を出した」と読み、
        // 実際にはブローカー側の逆指値が執行した約定を二重に説明してしまう。
        ["StopLossTriggered"] = (
            new StopLossTriggered(Id, "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, T),
            new NotificationMessage(
                "リスク統制: 損切りライン到達",
                "7203 損切り SL=940（現在 950・数量 5・建玉 Buy）。"
                    + "決済はブローカー側の逆指値が実行します（システムは決済注文を発行しません）。",
                NotificationSeverity.Critical)),

        ["FxRateSourceFellBack"] = (
            new FxRateSourceFellBack("USD", "fred", 2, 2, T),
            new NotificationMessage(
                "為替: 情報源がフォールバックへ切替",
                "USD の為替レートを fred（優先度 2/2）から取得しています。第一の情報源が使えていません。"
                    + "**鮮度が日次から週次へ悪化し得ます**（新規建ては止まっていません・ADR-0022 決定2）。",
                NotificationSeverity.Warning)),

        ["FxRateSourcePrimaryRestored"] = (
            new FxRateSourcePrimaryRestored("USD", "boj", T.AddHours(-6), T),
            new NotificationMessage(
                "為替: 第一の情報源へ復帰",
                "USD の為替レートは第一の情報源（boj）へ戻りました。フォールバックしていた期間: 6 時間。",
                NotificationSeverity.Info)),

        ["FxRateStale/警告"] = (
            new FxRateStale("USD", T.AddDays(-7), 7, 5, 30, T),
            new NotificationMessage(
                "為替: レートの鮮度警告",
                "USD の為替レートの観測が 7 日前です（観測日 2026-08-21・警告 5 日超）。"
                    + "**直近レートで続行しており新規建ては止まっていません**。"
                    + "30 日を超えると新規建てを停止します（手仕舞いは止めません・ADR-0022 決定5）。",
                NotificationSeverity.Warning)),

        // 🔴 同じイベント型でも「止まった」なら件名も本文も変わる（#381 停止側）。
        ["FxRateStale/停止"] = (
            new FxRateStale("USD", T.AddDays(-31), 31, 5, 30, T, EntryBlocked: true),
            new NotificationMessage(
                "為替: レートが上限超のため新規建てを停止",
                "USD の為替レートの観測が 31 日前です（観測日 2026-07-28・上限 30 日）。"
                    + "**新規建てを停止しました。手仕舞い・損切りは止めていません**（ADR-0022 決定5）。",
                NotificationSeverity.Critical)),

        ["PositionClosedWithStaleFxRate"] = (
            new PositionClosedWithStaleFxRate("7203", Market.Japan, "JPY", 300, 0.0067m, T.AddDays(-31), 31, T),
            new NotificationMessage(
                "為替: 鮮度切れのレートで決済した",
                "7203/Japan を数量 300 で決済しました。換算率 0.0067（観測日 2026-07-28・31 日前）。"
                    + "**計画どおり手仕舞いは止めていません**が、**円換算額は実勢から乖離し得ます**。",
                NotificationSeverity.Warning)),

        ["AssumptionsChanged"] = (
            new AssumptionsChanged(3, "endazon", "上限の見直し", T),
            new NotificationMessage(
                "設定変更: 全体前提条件",
                "前提条件が更新されました（v3・endazon）: 上限の見直し",
                NotificationSeverity.Info)),

        ["ReportConfirmed"] = (
            new ReportConfirmed("daily-2026-08-28", "Daily", "endazon", 3, T),
            new NotificationMessage(
                "報告書確定",
                "Daily 報告書 daily-2026-08-28 が確定しました（endazon・前提条件 v3）。",
                NotificationSeverity.Info)),

        ["ReportDraftPresented"] = (
            new ReportDraftPresented("daily-2026-08-28", "Daily", "2026-08-28", "日報の要約", 2, T),
            new NotificationMessage(
                "報告書ドラフト（承認待ち）",
                "日報の要約\n\n内容を確認のうえ確定してください（daily-2026-08-28・版 2）。"
                    + "確定するまで取引方針は変わりません。",
                NotificationSeverity.Info)),

        ["CostThresholdReached/Throttled"] = (
            new CostThresholdReached("2026-08", "Llm", 80m, "Throttled", T),
            new NotificationMessage(
                "費用統制: Throttled",
                "Llm 費用が月次上限の 80% に到達しました（2026-08・Throttled）。",
                NotificationSeverity.Warning)),

        ["CostThresholdReached/Halted"] = (
            new CostThresholdReached("2026-08", "Llm", 100m, "Halted", T),
            new NotificationMessage(
                "費用統制: Halted",
                "Llm 費用が月次上限の 100% に到達しました（2026-08・Halted）。",
                NotificationSeverity.Critical)),

        ["DailyPolicyUnconfirmed"] = (
            new DailyPolicyUnconfirmed(new DateOnly(2026, 8, 28), T),
            new NotificationMessage(
                "取引スキップ: 日報未確定",
                "確定済みの日報がないため取引を見送りました（営業日 2026-08-28）。日報を確定してください。",
                NotificationSeverity.Warning)),

        ["WithdrawalTriggered"] = (
            new WithdrawalTriggered(1, "DD 上限到達", true, T),
            new NotificationMessage(
                "リスク統制: 撤退基準到達",
                "撤退基準に到達しました（DD 上限到達）。新規建てを自動停止しました。"
                    + "Stage 1 への差し戻しを提案します（確定は利用者承認が必要）。",
                NotificationSeverity.Critical)),

        ["PositionReconciliationDrift"] = (
            new PositionReconciliationDrift(
                [
                    new PositionDriftItem("AAPL", Market.UnitedStates, 0, 4072, PositionDriftKind.BrokerOnly),
                    new PositionDriftItem("7203", Market.Japan, 100, 80, PositionDriftKind.QuantityMismatch),
                ],
                T,
                T),
            new NotificationMessage(
                "リスク統制: 建玉の乖離を検知",
                "取引台帳とブローカの建玉が一致しません（2 件・観測 2026-08-28 03:00:00Z）。"
                    + "AAPL/UnitedStates: 台帳に無い建玉がブローカに 4072、7203/Japan: 台帳 100 ≠ ブローカ 80。"
                    + "自動是正は行いません。内容を確認し、必要なら決済または証券会社側で調整してください。",
                NotificationSeverity.Critical)),

        ["MaintenanceMarginReductionExecuted"] = (
            new MaintenanceMarginReductionExecuted(
                Id, 0.40m, 0.40m, 0.45m, 0.4504m,
                [new MaintenanceMarginReductionItem(
                    "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, 112, 100m, 3_360m)],
                T),
            new NotificationMessage(
                "リスク統制: 維持率割れによる建玉の自動縮小",
                "維持率 40.0% が閾値 40.0% に達したため、回復目標 45.0%（閾値+5pt）まで建玉を縮小しました"
                    + "（決済後 45.0%）。決済した建玉: AAPL/UnitedStates ショート 112 株（必要証拠金 3,360.00 USD）。"
                    + "利用者の承認と AI の判断は介在していません（機械的規則）。",
                NotificationSeverity.Critical)),

        ["BuyInInferred"] = (
            new BuyInInferred(
                Id, "GME", Market.UnitedStates,
                LedgerShortQuantity: 100, BrokerShortQuantity: 0, InFlightCloseQuantity: 0,
                UnexplainedQuantity: 100, NewlyInferredQuantity: 100,
                CoveringFills: [],
                BanUntil: new DateOnly(2026, 9, 6),
                ObservedAt: T,
                InferredAt: T),
            new NotificationMessage(
                "リスク統制: 強制買戻しと推定（空売り 30 日禁止）",
                "GME/UnitedStates で**強制買戻し（buy-in）と推定**しました。"
                    + "台帳（自らの約定履歴）の空売り 100 株に対し、ブローカの空売りは 0 株（処理中の決済 0 株）であり、"
                    + "自らの決済指示で説明できない消失 100 株を検出しました。"
                    + "2026-09-06 まで当該銘柄の新規空売りを禁止します。"
                    + "これは**イベントとしての検知ではなく推定**です（確定した事実として扱わないでください）。"
                    + "手動売買・外部要因による建玉の消失を取り違えている可能性があります。",
                NotificationSeverity.Critical)),

        // #341, IADR-0241 決定2〜4: GFV 違反の計上。**「停止した」と断定せず**、限界（自らのガードの
        // 失敗回数であってブローカのカウンタの写しではない）と、**解除の窓口が Discord だけ**であることを書く。
        ["GoodFaithViolationRecorded/未供給"] = (
            new GoodFaithViolationRecorded(
                Id, Id, "ORD-9", "AAPL", Market.UnitedStates, 12_345.67m, null,
                new DateOnly(2026, 8, 27), T, T),
            new NotificationMessage(
                "リスク統制: GFV 違反を計上（発注前ガードのすり抜け）",
                "AAPL/UnitedStates の買付（注文 ORD-9・12,345.67 USD）を GFV 発生 1 件として計上しました"
                    + "（取引日 2026-08-27・判定に用いた決済済み資金 未供給）。"
                    + "**発注前の GFV 回避ガードをすり抜けた買付が約定しています**（ガードの不具合または口座観測の欠落）。"
                    + "これは**自らのガードの失敗回数**であり、ブローカ側の GFV カウンタの写しではありません（ADR-0025）。"
                    + "違反が積み上がると新規取引が停止します。停止の解除は Discord の `/gfv clear` のみです"
                    + "（違反記録そのものは消えません）。",
                NotificationSeverity.Critical)),

        // #424 の表示規約: **null は「未供給」であって 0 ではない。** 0 が供給された場合と読み分けられること。
        ["GoodFaithViolationRecorded/残高0"] = (
            new GoodFaithViolationRecorded(
                Id, Id, "ORD-9", "AAPL", Market.UnitedStates, 12_345.67m, 0m,
                new DateOnly(2026, 8, 27), T, T),
            new NotificationMessage(
                "リスク統制: GFV 違反を計上（発注前ガードのすり抜け）",
                "AAPL/UnitedStates の買付（注文 ORD-9・12,345.67 USD）を GFV 発生 1 件として計上しました"
                    + "（取引日 2026-08-27・判定に用いた決済済み資金 0.00 USD）。"
                    + "**発注前の GFV 回避ガードをすり抜けた買付が約定しています**（ガードの不具合または口座観測の欠落）。"
                    + "これは**自らのガードの失敗回数**であり、ブローカ側の GFV カウンタの写しではありません（ADR-0025）。"
                    + "違反が積み上がると新規取引が停止します。停止の解除は Discord の `/gfv clear` のみです"
                    + "（違反記録そのものは消えません）。",
                NotificationSeverity.Critical)),

        // #335, IADR-0217: LLM 割当逸脱。develop 側（#335）が `NotificationFormatter.From` へ
        // 足したテンプレートであり、母集合をリフレクションで引く網羅検査が合流時に要求する。
        // Warning——沈黙させないことが目的であり、Critical にすると本当に止まった事象が埋もれる。
        ["LlmFallbackFired"] = (
            new LlmFallbackFired("report-monthly", "claude-opus-5", "claude-sonnet-5", "FallbackFired", T),
            new NotificationMessage(
                "LLM 割当逸脱: report-monthly",
                "用途 report-monthly が割当（claude-opus-5）ではなく claude-sonnet-5 で応答しました"
                    + "（FallbackFired）。恒常的に発火している場合は割当設定を確認してください。",
                NotificationSeverity.Warning)),

        // #335, IADR-0216: 取引判断の見送り。**「設計上の正常な結果」が本文から欠けると
        // 運用が障害として扱い、善意のフォールバック追加を招く**（ADR-0017 決定2）。
        ["TradeDecisionSkipped"] = (
            new TradeDecisionSkipped("trade-decision", "model-mismatch", "claude-opus-5", "claude-haiku-4-5", T),
            new NotificationMessage(
                "取引判断の見送り: 割当モデルが利用できません",
                "用途 trade-decision の割当モデル（claude-opus-5）が使えないため取引判断を実行せず、"
                    + "発注も行いませんでした（理由 model-mismatch・実際 claude-haiku-4-5）。"
                    + "**設計上の正常な結果**です（フォールバック禁止）。",
                NotificationSeverity.Warning)),

        // #331, IADR-0211: 発注の見送り。**「再試行されません」が本文から欠けると、利用者は
        // 「あとで自動的に発注される」と誤解して次の取引判断まで放置する。**
        ["OrderDispatchForgone"] = (
            new OrderDispatchForgone(Id, Intent(), OrderDispatchForgoneReason.BrokerUnavailable, T),
            new NotificationMessage(
                "発注見送り: ブローカー（OpenD）へ接続できません",
                "AAPL/UnitedStates Buy 数量10 の発注を見送りました"
                    + "（理由: ブローカー（OpenD）へ接続できません"
                    + "・DecisionId=11111111-1111-1111-1111-111111111111）。"
                    + "**この注文は再試行されません**（キューイングしない・再発注は次の取引判断から）。",
                NotificationSeverity.Warning)),

        // #331, IADR-0210: 保護逆指値の発注。統制が設計どおり働いた記録であり Info
        // （Critical にすると実際に止まる事象が埋もれる）。
        ["ProtectiveStopPlaced"] = (
            new ProtectiveStopPlaced(
                Id, Id, "stop-1",
                new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                    BrokerProvider.InternalPaper, 10, 950m, PositionEffect.Close),
                950m, 1, T),
            new NotificationMessage(
                "保護逆指値を発注",
                "AAPL/UnitedStates Sell 数量10 トリガー 950（試行 1・StopOrderId=stop-1）。",
                NotificationSeverity.Info)),

        // 🔴 同じイベント型でも**対処の結末で本文が変わる**（FxRateStale と同じ扱いで分岐ごとに 1 行）。
        // 手仕舞いに成功した側。
        ["ProtectiveStopCoverageLost/建玉を手仕舞い"] = (
            new ProtectiveStopCoverageLost(
                Id, "AAPL", Market.UnitedStates, ProtectiveStopLossCause.LapsedInFlight,
                ProtectiveStopRemediation.PositionClosed, 10, Id,
                new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                    BrokerProvider.InternalPaper, 10, 1_000m, PositionEffect.Close),
                T),
            new NotificationMessage(
                "リスク統制: 保護逆指値が成立せず建玉を解消",
                "AAPL/UnitedStates 数量10: 逆指値が滞留中に失効（再発注不可）のため、"
                    + "建玉を成行で手仕舞いました（逆指値なしの建玉を持たない規律・FR-10）。",
                NotificationSeverity.Critical)),

        // 🔴 解消にも失敗した側。**逆指値なしの建玉が残り得る唯一の分岐であり、人手対応を促す
        // 文言が欠けると「対処済み」と読まれる。** 分岐ごとにゴールデンを持つ理由がここにある。
        ["ProtectiveStopCoverageLost/解消も失敗"] = (
            new ProtectiveStopCoverageLost(
                Id, "AAPL", Market.UnitedStates, ProtectiveStopLossCause.RejectedAtEntry,
                ProtectiveStopRemediation.None, 10, null, null, T),
            new NotificationMessage(
                "リスク統制: 保護逆指値が成立せず建玉を解消",
                "AAPL/UnitedStates 数量10: 逆指値がエントリー時に未受理のため、"
                    + "**建玉の解消にも失敗しました。逆指値なしの建玉が残っている可能性があります。"
                    + "直ちに確認してください。**",
                NotificationSeverity.Critical)),
    };

    public static TheoryData<string> GoldenKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in Golden.Keys)
            data.Add(key);
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenKeys))]
    public void 通知テンプレートはゴールデンと完全一致する(string key)
    {
        // FR-09: 件名・本文・重大度のいずれが欠けても差分として現れる（部分一致にしない）。
        var (evt, expected) = Golden[key];

        var actual = Format(evt);

        actual.Title.Should().Be(expected.Title);
        actual.Content.Should().Be(expected.Content);
        actual.Severity.Should().Be(expected.Severity);
    }

    [Fact]
    public void すべての通知テンプレートがゴールデンで覆われている()
    {
        // IADR-0242 決定2: 母集合はコードから引く。**数を書かない。**
        // テンプレートを足してゴールデンを書かなければここで落ちる。
        var declared = FormatterOverloads().Select(m => m.GetParameters()[0].ParameterType).ToHashSet();
        declared.Should().NotBeEmpty("母集合が空なら本テストは何も守っていない");

        var covered = Golden.Values.Select(v => v.Event.GetType()).ToHashSet();

        declared.Except(covered).Select(t => t.Name).Should().BeEmpty(
            "ゴールデンの無いテンプレートは『必須項目の欠落検知』の対象外になる");
        covered.Except(declared).Select(t => t.Name).Should().BeEmpty(
            "存在しないテンプレートのゴールデンは腐った記録である");
    }

    [Fact]
    public void 通知テンプレートは件名と本文を必ず持つ()
    {
        // 「必須項目の欠落」の最小条件。ゴールデンの写し間違いで空文字を固定してしまう事故も同時に防ぐ。
        foreach (var (key, (evt, _)) in Golden)
        {
            var msg = Format(evt);
            msg.Title.Should().NotBeNullOrWhiteSpace($"{key} の件名");
            msg.Content.Should().NotBeNullOrWhiteSpace($"{key} の本文");
        }
    }

    private static IEnumerable<MethodInfo> FormatterOverloads() =>
        typeof(NotificationFormatter)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "From" && m.GetParameters().Length == 1);

    // イベント型に対応する From オーバーロードを解決して呼ぶ（表の Key ではなく型で引く）。
    private static NotificationMessage Format(object evt)
    {
        var method = FormatterOverloads().Single(m => m.GetParameters()[0].ParameterType == evt.GetType());
        return (NotificationMessage)method.Invoke(null, [evt])!;
    }
}
