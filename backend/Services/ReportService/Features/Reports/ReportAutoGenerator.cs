using ReportService.Common.Exceptions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Features.Reports;

// FR-06/07/16, UC-03〜05, ADR-0003, 04_workflows/03_reporting-cycle, IADR-0115, #280:
// 日報/週報/月報の自動生成（1 巡回ぶん）。常駐（BackgroundService）から分離し、時刻を固定して単体テストできる単位にする。
//
// 自動化の終点は **提示（Present）** であり、確定（Confirm）はここから呼ばない。生成物は ReviewState.PendingApproval /
// ReportState.Draft で止まるため、取引が参照する「確定済み日報の方針」は利用者が確定するまで動かない
// （ADR-0003「完全無人での方針変更は行わない」・IADR-0115 決定1）。
//
// 冪等の根拠は PeriodKey の存在のみ（IADR-0115 決定3）。プロセス内に「生成済み」を持たないため、再起動・多重レプリカの
// いずれでも二重生成しない。1 期間の失敗は他の期間を巻き込まない（期間ごとに独立して捕捉する）。
//
// #840, IADR-0352: **依存先が一過性に落ちている間は、縮退した報告書を作らずに見送る。**
// 再起動の直後は Keycloak・台帳・LLM ゲートウェイがまだ立ち上がっておらず、入力が広範に未供給のまま
// 報告書が出来上がって提示・確定まで進み得た。見送った期間は行を作らないため、上の冪等の規則どおり
// 次の巡回で再び対象になる（再試行の仕組みを別に持たない）。見送りは上限つきで、上限に達したら・
// 失敗が恒常的（403 など）なら・**次に試す時刻には生成窓が閉じているなら（#866）**、従来どおり
// 縮退した報告書を出して**未供給だった入力を記録・提示する**。
public sealed class ReportAutoGenerator(
    IReportStore store,
    ReportDraftService draftService,
    IPeriodFillSource fillSource,
    IClock clock,
    ReportAutoGenerationSettings settings,
    IReportDraftPresentedNotifier? notifier = null,
    IMarginReductionRecordSource? reductionSource = null,
    IBuyInInferenceRecordSource? buyInSource = null,
    IFxSourceStatusSource? fxSourceStatusSource = null,
    ILlmUsageRecordSource? llmUsageSource = null,
    IStage0RecordingEstimateSource? stage0RecordingEstimateSource = null,
    IBorrowFeeRecordSource? borrowFeeSource = null,
    ITradeRationaleSource? rationaleSource = null,
    IOpenPositionSource? openPositionSource = null,
    IOpenDUptimeSource? uptimeSource = null,
    IStageProgressSource? stageProgressSource = null,
    IPeriodEndFxRateSource? periodEndFxRateSource = null,
    ReportDependencyProbe? dependencyProbe = null,
    ReportGenerationDeferralTracker? deferrals = null,
    // FR-06, FR-11, ADR-0041 決定 1, #870, #859, IADR-0360 決定 2: 期間の手動売買の取り込み。
    // 未注入は「供給元が構成されていない」＝常に未供給（空列へ倒さない）。
    IPeriodDriftAdoptionSource? driftAdoptionSource = null,
    // FR-06, FR-10, ADR-0040 決定1, #823, IADR-0422 決定3: 日報 §4「損切りの実行機構（当日）」（承認時点の手法の集計）。
    // 未注入は「供給元が構成されていない」＝常に未供給（「承認なし」へ倒さない）。
    IStopLossMethodUsageSource? stopLossMethodUsageSource = null,
    // FR-06, FR-10, ADR-0040 決定1, #1002, IADR-0429 決定4: 発注執行の損切りの実行機構の解決結果（日報の 2 行目・月報 §6）。
    // 未注入は「供給元が構成されていない」＝常に未供給（「記録なし」「食い違いなし」へ倒さない）。
    IStopLossMethodResolutionSource? stopLossMethodResolutionSource = null,
    // FR-06, FR-14, 計画 ADR-0052 決定 1, #1156, IADR-0491 決定 6: 月報 §7 の作り直しの回数（試行の台帳）。未注入は null＝照会できていない。
    IReportRegenerationLedger? regenerationLedger = null,
    ReportRegenerationLimit? regenerationLimit = null,
    // FR-06, FR-16, #1181, IADR-0493 決定 1・4: 期間開始時点の在庫（取引台帳が窓の市場ごとの下端まで畳んだもの）。
    // 未注入（単体テスト・旧構成）は取りに行かない＝従来どおり期間で切った在庫（IADR-0381。算定できない決済を検出した回だけ未供給）。
    // 本番は必ず注入する（所在が未構成なら UnsuppliedOpeningInventorySource ＝常に未供給）。
    IOpeningInventorySource? openingInventorySource = null)
{
    // 観測点が未注入（単体テスト・旧構成）なら誰も記録しない観測になり、見送りは起きない＝従来挙動。
    private readonly ReportDependencyProbe _probe = dependencyProbe ?? new ReportDependencyProbe();

    /// <summary>1 巡回。生成境界を過ぎていて未生成の期間だけドラフトを生成し、提示（PendingApproval）まで進める。</summary>
    public async Task<ReportAutoGenerationResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var generated = new List<TradingReport>();
        var failed = new List<ReportAutoGenerationFailure>();
        var notPresented = new List<string>();
        var notificationFailed = new List<string>();
        var deferred = new List<ReportGenerationDeferral>();
        var degraded = new List<ReportGenerationDegradation>();

        var dueNow = ReportSchedule.Due(clock.UtcNow, settings.Schedule);

        // #866: 生成窓が閉じて対象から外れた期間の見送り回数を捨てる（その PeriodKey は二度と Due に
        // 現れず、生成による解放が起きないため、捨てないとプロセス内に残り続ける）。
        deferrals?.RetainOnly([.. dueNow.Select(d => d.PeriodKey)]);

        foreach (var due in dueNow)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 冪等: 既に行があるなら生成も提示もしない（利用者が手で作った・差し戻し中のドラフトを踏まない）。
            if (store.Get(due.PeriodKey) is not null)
            {
                // #840: 見送っている間に他レプリカ・利用者の手で行が出来た期間。見送り回数は用済みなので捨てる
                // （捨てないと、生成しなかったレプリカに期間ごとの回数が残り続ける）。
                deferrals?.Clear(due.PeriodKey);
                continue;
            }

            try
            {
                var outcome = await GenerateAsync(due, cancellationToken).ConfigureAwait(false);
                if (outcome.Deferral is { } deferral)
                {
                    // #840, IADR-0352 決定 3: 見送り。行を作っていないので次の巡回で再び対象になる。
                    deferred.Add(deferral);
                    continue;
                }

                generated.Add(outcome.Report!);
                if (outcome.Report!.UnsuppliedInputs.Count > 0)
                    degraded.Add(new ReportGenerationDegradation(
                        due.PeriodKey, outcome.Report.UnsuppliedInputs, outcome.RetriesExhausted,
                        outcome.WindowClosing));
                if (!outcome.Presented)
                    notPresented.Add(due.PeriodKey);
                if (outcome.NotificationFailed)
                    notificationFailed.Add(due.PeriodKey);

            }
            catch (OperationCanceledException)
            {
                throw; // 停止要求は呼び出し側（常駐）へ伝える。
            }
            catch (ReportConcurrencyException)
            {
                // 他レプリカが同じ期間を先に作った（expectedVersion 0 の競合）。二重生成を避けた結果であり失敗ではない。
            }
            catch (InvalidOperationException)
            {
                // 直前に確定された等で upsert が拒否された。次巡回では PeriodKey 一致でスキップされる。
            }
            catch (Exception ex)
            {
                failed.Add(new ReportAutoGenerationFailure(due.PeriodKey, ex));
            }
        }

        return new ReportAutoGenerationResult(generated, failed, notPresented, notificationFailed)
        {
            Deferred = deferred,
            Degraded = degraded,
        };
    }

    private async Task<GenerationOutcome> GenerateAsync(DueReport due, CancellationToken cancellationToken)
    {
        // 方針階層（03_reporting-cycle）: BasedOn は上位種別の直近確定済み。参照できなければ null＝方針文に明記する。
        var parentKind = ReportPolicyDraft.ParentKind(due.Kind);
        var parent = store.GetLatestConfirmed(parentKind);
        // 継続案の素は「同種別」の直近確定済み（月報は上位＝前月報と同一）。
        var previous = parentKind == due.Kind ? parent : store.GetLatestConfirmed(due.Kind);

        var policy = ReportPolicyDraft.CarryOver(
            due.Kind, previous?.Report.PeriodKey, previous?.Report.PolicySummary, parent?.Report.PeriodKey);

        // #840, IADR-0352 決定 2: この生成 1 回ぶんの依存失敗を観測する。供給元は不達を null へ倒して返すため、
        // 「なぜ届かなかったか」は HTTP の鎖（ReportDependencyHandler）が本観測へ記録する。
        // 取りに行く入力を Enter で宣言してから呼ぶ（逐次に呼ぶので、失敗がどの入力のものかが定まる）。
        using var observation = _probe.Begin();
        var unsupplied = new HashSet<ReportInput>();

        // FR-07, UC-03, #839, IADR-0382: **方針の連鎖が切れていることを、確定の前に見せる。**
        // 上位方針（親の直近確定済み）・前期方針（同種別の直近確定済み）が無いまま生成された報告書は、
        // 方針文が空（注記だけ）になるが、それは本文を全部読まないと分からなかった。
        //
        // 🔴 **見送り（リトライ）の対象にしない。** 供給元は自リポジトリのストアであり HTTP を出さない
        // ——観測が無いので TryDefer は反応しない。**待っても確定済みの上位は増えない**（確定は利用者の行為である）。
        if (parent is null)
            unsupplied.Add(ReportInput.ParentPolicy);

        // 🔴 月報は上位＝前期（前月の月報）と**同一**である（ParentKind(Monthly) == Monthly）。
        // 両方を数えると、同じ事実を 2 つの表示名で 2 回警告することになる。
        if (previous is null && parentKind != due.Kind)
            unsupplied.Add(ReportInput.PreviousPolicy);

        var inputs = await CollectInputsAsync(due, observation, NoNotRestorable, cancellationToken).ConfigureAwait(false);
        unsupplied.UnionWith(inputs.Unsupplied);

        // #840, IADR-0352 決定 3: **散文（LLM）を呼ぶ前に**見送りを判定する。入力が欠けたままの回に
        // LLM 費用を出さない（見送る回の散文は捨てるしかない）。
        var retriesExhausted = false;
        var windowClosing = false;
        if (TryDefer(due, unsupplied, observation, ref retriesExhausted, ref windowClosing) is { } deferredForInputs)
            return GenerationOutcome.Deferred(deferredForInputs);

        observation.Enter(ReportInput.Narrative);

        var draft = await DraftFromInputsAsync(
            due, inputs, policy, settings.AssumptionsVersion, parent?.Report.PeriodKey,
            // FR-07, IADR-0120 決定3, #293 / IADR-0125 決定4, #310: 上位方針は**方針の実体だけ**を散文の文脈へ渡す。
            ReportPolicyDraft.Substance(parent?.Report.PolicySummary),
            unsupplied, usagePurpose: null, cancellationToken).ConfigureAwait(false);

        // 散文の未供給＝プレースホルダ散文（LLM 未接続・縮退のいずれも。数値には関与しない）。
        if (unsupplied.Contains(ReportInput.Narrative)
            && TryDefer(due, [ReportInput.Narrative], observation, ref retriesExhausted, ref windowClosing)
                is { } deferredForNarrative)
        {
            return GenerationOutcome.Deferred(deferredForNarrative);
        }

        var unsuppliedInputs = ReportInputs.Parse(ReportInputs.Serialize(unsupplied));

        var report = new TradingReport
        {
            PeriodKey = due.PeriodKey,
            Kind = due.Kind,
            PeriodStart = due.PeriodStart,
            BasedOn = parent?.Report.PeriodKey,
            AssumptionsVersion = settings.AssumptionsVersion,
            PolicySummary = policy,
            Body = draft.Markdown,
            // #840, IADR-0352 決定 5: 欠けたまま生成した入力を記録する（提示の時点で見せるため）。
            UnsuppliedInputs = unsuppliedInputs,
        };

        var version = store.UpsertDraft(report, expectedVersion: 0);
        // 生成できた（縮退の有無を問わない）ので、この期間の見送り回数は用済みである。
        deferrals?.Clear(due.PeriodKey);

        // 提示（Drafting→PendingApproval）。承認・確定は利用者の OwnerOnly 経路のみ（ADR-0003・IADR-0115 決定1）。
        // 直後の遷移のため通常は必ず受理されるが、ストア実装や状態機械の規則が変わったときに「提示が黙って失敗し
        // 承認待ちに並ばない」事故を検知できるよう、結果を捨てずに呼び出し側へ返す（常駐が警告ログに残す）。
        var decision = store.ApplyReview(due.PeriodKey, new ReviewCommand(ReviewAction.Present, settings.Actor, version));
        var presented = decision is { Accepted: true } && decision.Review.State == ReviewState.PendingApproval;

        // FR-09, IADR-0116 決定4: 提示通知の要約。数値はコード集計値のみ、散文はサニタイズ済み（Build の内側で適用）。
        // #840, IADR-0352 決定 5: 未供給だった入力を要約へ載せる（確定依頼を見た時点で欠落に気付ける）。
        // FR-04, FR-07, #1129, IADR-0470 決定 4: 日報の初稿（直近の確定済み方針の継続）に書式どおりの「利確:」行が無ければ、
        // 提示の要約で確定の前に警告する（確定は止めない。方針の本文は変えない）。要約に印が入ると、通知サービスが
        // 提示の通知を Warning へ上げる（未供給の警告と同じ経路。本クラスはロガーを持たない）。
        // ADR-0051 フォローアップ 1, #1223, IADR-0470（2026-10-08 追記）: 本文の §3 と同じ建玉で、行の掛からない保有中の銘柄を名指しする。
        // 建玉が未供給なら方針全体の判定へ戻る（未供給は要約の「建玉」の警告で見える）。
        var takeProfitWarning = PolicyTakeProfitCheck.WarningFor(due.Kind, policy, inputs.Positions);

        var summary = ReportSummary.Build(
            due.Kind, ReportPeriod.Label(due.Kind, due.PeriodStart), draft.Pnl, draft.Narrative, unsuppliedInputs,
            takeProfitWarning);

        // FR-09, IADR-0116 決定2: 提示まで到達したものだけ通知する（承認待ちに無いものを「確認してください」と言わない）。
        var notificationFailed = presented
            && !await NotifyAsync(due, summary, version, cancellationToken).ConfigureAwait(false);

        return new GenerationOutcome(
            report, presented, notificationFailed, retriesExhausted, windowClosing, Deferral: null);
    }

    private static readonly IReadOnlySet<ReportInput> NoNotRestorable = new HashSet<ReportInput>();

    /// <summary>
    /// FR-06, FR-14, 計画 ADR-0052 決定 2・4, #1156, IADR-0491 決定 4: 所有者の作り直し（<see cref="ReportRegenerationService"/>）が、
    /// 自動生成と<b>同じ供給元・同じ規則</b>で期間の入力を引く入口。<paramref name="notRestorable"/> に挙げた入力（期間の時点に復元できない
    /// 入力）は<b>取りに行かず</b>未供給として扱う（今の値を期間の値として書かない）。見送り（TryDefer）はしない——作り直しは利用者が今
    /// 求めた操作であり、中核の入力の取得失敗は呼び出し側が断る（ADR-0052 決定 4）。
    /// </summary>
    public async Task<ReportInputSnapshot> CollectInputsAsync(
        DueReport due, IReadOnlySet<ReportInput> notRestorable, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(due);
        ArgumentNullException.ThrowIfNull(notRestorable);
        using var observation = _probe.Begin();
        return await CollectInputsAsync(due, observation, notRestorable, cancellationToken).ConfigureAwait(false);
    }

    // 期間の入力を順に引く（自動生成と作り直しで 1 本）。未供給は種別が使う入力だけを数える。
    private async Task<ReportInputSnapshot> CollectInputsAsync(
        DueReport due,
        ReportDependencyObservation observation,
        IReadOnlySet<ReportInput> notRestorable,
        CancellationToken cancellationToken)
    {
        var unsupplied = new HashSet<ReportInput>();

        observation.Enter(ReportInput.Fills);
        var (fills, fillsFailed) = await SafeFillsAsync(due, cancellationToken).ConfigureAwait(false);
        // 約定だけは不達でも空列へ倒れる（IADR-0115 決定5）ため、値からは欠落が分からない。観測から判定する。
        if (fillsFailed || observation.HasFailure(ReportInput.Fills))
            unsupplied.Add(ReportInput.Fills);

        observation.Enter(ReportInput.DriftAdoptions);
        var driftAdoptions = await SafeDriftAdoptionsAsync(due, cancellationToken).ConfigureAwait(false);
        if (driftAdoptions is null)
            unsupplied.Add(ReportInput.DriftAdoptions);

        observation.Enter(ReportInput.MarginReductions);
        var reductions = await SafeReductionsAsync(due, cancellationToken).ConfigureAwait(false);
        if (reductions is null)
            unsupplied.Add(ReportInput.MarginReductions);

        observation.Enter(ReportInput.BuyInInferences);
        var buyIns = await SafeBuyInInferencesAsync(due, cancellationToken).ConfigureAwait(false);
        if (buyIns is null)
            unsupplied.Add(ReportInput.BuyInInferences);

        observation.Enter(ReportInput.FxSourceStatus);
        var fxStatus = await SafeFxSourceStatusAsync(due, cancellationToken).ConfigureAwait(false);
        if (fxStatus is null)
            unsupplied.Add(ReportInput.FxSourceStatus);

        observation.Enter(ReportInput.LlmUsage);
        var llmUsage = await SafeLlmUsageAsync(due, cancellationToken).ConfigureAwait(false);
        if (llmUsage is null)
            unsupplied.Add(ReportInput.LlmUsage);

        observation.Enter(ReportInput.BorrowFees);
        var borrowFees = await SafeBorrowFeesAsync(due, cancellationToken).ConfigureAwait(false);
        if (borrowFees is null)
            unsupplied.Add(ReportInput.BorrowFees);

        observation.Enter(ReportInput.TradeRationales);
        var rationales = await SafeRationalesAsync(due, cancellationToken).ConfigureAwait(false);
        if (rationales is null)
            unsupplied.Add(ReportInput.TradeRationales);

        // FR-06, 計画 ADR-0052 決定 2, IADR-0491 決定 4: 建玉は「今の台帳」しか引けない。期間の時点に復元できないなら取りに行かない
        // （今の建玉を「当日終了時点」と書く誤りを作らない）。
        IReadOnlyList<ReportPosition>? positions = null;
        if (!notRestorable.Contains(ReportInput.OpenPositions))
        {
            observation.Enter(ReportInput.OpenPositions);
            positions = await SafeOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (positions is null)
            unsupplied.Add(ReportInput.OpenPositions);

        observation.Enter(ReportInput.OpenDUptime);
        var uptime = await SafeUptimeAsync(due, cancellationToken).ConfigureAwait(false);
        if (uptime is null)
            unsupplied.Add(ReportInput.OpenDUptime);

        observation.Enter(ReportInput.StopLossMethods);
        var stopLossMethods = await SafeStopLossMethodsAsync(due, cancellationToken).ConfigureAwait(false);
        if (stopLossMethods is null)
            unsupplied.Add(ReportInput.StopLossMethods);

        observation.Enter(ReportInput.StopLossMethodResolutions);
        var stopLossMethodResolutions = await SafeStopLossMethodResolutionsAsync(due, cancellationToken).ConfigureAwait(false);
        if (stopLossMethodResolutions is null)
            unsupplied.Add(ReportInput.StopLossMethodResolutions);

        // FR-06, 計画 ADR-0052 決定 2, IADR-0491 決定 4: 運用段階も「今の段階」しか引けない（月報 §5 の三者比較）。
        TradingStage? currentStage = null;
        if (!notRestorable.Contains(ReportInput.CurrentStage))
        {
            observation.Enter(ReportInput.CurrentStage);
            currentStage = await SafeCurrentStageAsync(cancellationToken).ConfigureAwait(false);
        }

        if (currentStage is null)
            unsupplied.Add(ReportInput.CurrentStage);

        observation.Enter(ReportInput.PeriodEndFxRate);
        var periodEndFxRate = await SafePeriodEndFxRateAsync(due, cancellationToken).ConfigureAwait(false);
        if (periodEndFxRate is null)
            unsupplied.Add(ReportInput.PeriodEndFxRate);

        // FR-06, FR-16, #1181, IADR-0493 決定 1・4: 期間開始時点の在庫。台帳から引ける過去の時点の値であり、作り直しでも取りに行く
        // （IsPointInTime ではない）。取得に失敗したら未供給（fail-closed: 取得原価を要する値を「算出不能」にする）。
        OpeningInventorySnapshot? openingInventory = null;
        if (openingInventorySource is not null)
        {
            observation.Enter(ReportInput.OpeningInventory);
            openingInventory = await SafeOpeningInventoryAsync(due, cancellationToken).ConfigureAwait(false);
            if (openingInventory is null)
                unsupplied.Add(ReportInput.OpeningInventory);
        }

        // この種別が使わない入力の欠落は数えない（週報は建玉を描かない。警告にも見送りの判定にも混ぜない）。
        // Stage 0 の見積り承認額は構成値であり、未設定（承認が無い）が通常の状態のため、そもそも数えない。
        unsupplied.RemoveWhere(input => !ReportInputs.AppliesTo(input, due.Kind));

        return new ReportInputSnapshot
        {
            Fills = fills,
            DriftAdoptions = driftAdoptions,
            MarginReductions = reductions,
            BuyInInferences = buyIns,
            FxSourceStatus = fxStatus,
            LlmUsage = llmUsage,
            BorrowFees = borrowFees,
            TradeRationales = rationales,
            Positions = positions,
            Uptime = uptime,
            StopLossMethods = stopLossMethods,
            StopLossMethodResolutions = stopLossMethodResolutions,
            CurrentStage = currentStage,
            PeriodEndFxRate = periodEndFxRate,
            OpeningInventory = openingInventory,
            Unsupplied = ReportInputs.Parse(ReportInputs.Serialize(unsupplied)),
            NotRestorable = ReportInputs.Parse(ReportInputs.Serialize(
                notRestorable.Where(input => ReportInputs.AppliesTo(input, due.Kind)))),
        };
    }

    /// <summary>
    /// 引いた入力から本文（数値はコード集計・散文は LLM）を組み立てる（自動生成と作り直しで 1 本）。<paramref name="unsupplied"/> は
    /// 呼び出し側が持つ未供給の集合で、散文の文脈へ渡し、組み立ての結果（期間開始時点の在庫・プレースホルダ散文）を<b>足して返す</b>。
    /// <paramref name="usagePurpose"/> は散文の LLM 費用の計上区分の付け替え（作り直しは <c>report-regeneration</c>。IADR-0491 決定 2）。
    /// </summary>
    public async Task<ReportDraft> DraftFromInputsAsync(
        DueReport due,
        ReportInputSnapshot inputs,
        string policy,
        int assumptionsVersion,
        string? basedOn,
        string? parentPolicySummary,
        ISet<ReportInput> unsupplied,
        string? usagePurpose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(due);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(unsupplied);

        // 数値はコード集計・散文は LLM ドラフト（IADR-0032）。現在値は要求で指定せず、市場データ源へ委ねる（IADR-0066）。
        //
        // FR-07, IADR-0120 決定3, #293: 上位方針は **PeriodKey だけでなく本文まで**散文ドラフトへ渡す。
        // 従来は取得済みの parent から PeriodKey のみを使い PolicySummary を破棄していたため、計画
        // （04_workflows/03_reporting-cycle）が求める「上位方針の目標との差異評価」を LLM が書けなかった。
        // 渡し先は散文の文脈のみ。方針文（policy）へは混ぜない（IADR-0115 決定4・ADR-0003）。
        var draft = await draftService.BuildDraftAsync(
            new DraftRequest(
                due.Kind, due.PeriodKey, due.PeriodStart, settings.Markets, assumptionsVersion,
                basedOn, policy, inputs.Fills, CurrentPrices: null,
                ParentPolicySummary: parentPolicySummary,
                MarginReductions: inputs.MarginReductions,
                BuyInInferences: inputs.BuyInInferences,
                FxSourceStatus: inputs.FxSourceStatus,
                LlmUsage: inputs.LlmUsage,
                Stage0RecordingApprovedEstimateJpy: SafeStage0RecordingEstimate(),
                BorrowFees: inputs.BorrowFees,
                TradeRationales: inputs.TradeRationales,
                Positions: inputs.Positions,
                Uptime: inputs.Uptime,
                CurrentStage: inputs.CurrentStage,
                PeriodEndFxRate: inputs.PeriodEndFxRate,
                DriftAdoptions: inputs.DriftAdoptions,
                StopLossMethods: inputs.StopLossMethods,
                StopLossMethodResolutions: inputs.StopLossMethodResolutions,
                // FR-06, FR-16, #1156, IADR-0480 決定 1: 散文（LLM）へ「取得できなかった入力」を渡す。
                // 渡さないと、LLM は値の無さや 0 から「建玉なし」「取引なし」と推測する（#1156 の実測）。
                UnsuppliedInputs: ReportInputs.Parse(ReportInputs.Serialize(unsupplied)),
                UsagePurpose: usagePurpose,
                // FR-06, FR-14, 計画 ADR-0052 決定 1, IADR-0491 決定 6: 月報 §7 の作り直しの回数（台帳。null＝照会できていない）。
                ReportRegeneration: due.Kind == ReportKind.Monthly ? SafeRegenerationTally(due) : null,
                // FR-06, 計画 ADR-0053 決定 3, #1172, IADR-0492 決定 6: 集計したセッションの範囲を冒頭に書く
                // （約定を絞った窓と同じ SessionWindowOf から引く。作り直しも同じ経路を通る）。
                SessionRanges: ReportSchedule.SessionRangesOf(due, settings.Schedule, ReportedMarkets(settings.Markets)),
                // FR-06, #1224, IADR-0516 決定 5: 窓に揃えない入力（LLM 利用実績＝JST の暦日）を同じ行に書き足す。LLM 利用実績を使う種別だけ。
                LlmUsageCalendarDays: ReportInputs.AppliesTo(ReportInput.LlmUsage, due.Kind)
                    ? new ReportCalendarDays(due.PeriodStart, due.PeriodEnd)
                    : null,
                // FR-06, FR-16, #1181, IADR-0493 決定 3: 期間開始時点の在庫（null＝受け取っていない）。
                OpeningInventory: inputs.OpeningInventory),
            cancellationToken).ConfigureAwait(false);

        // FR-06, FR-16, #892, IADR-0381: 取得原価で賄えない決済を実際に検出したら、**期間開始時点の在庫**を未供給として記録する
        // （IADR-0352 の既存経路＝記録・提示通知の警告・`/report show`・版番号なしの `/report approve` の警告へそのまま乗る）。
        // #1181, IADR-0493 決定 4: 在庫を受け取った回でも、期間開始時点の在庫と期間の買いを超える売り（手仕舞い）はここに来る
        // （台帳と報告書の窓の食い違い。数字を騙らない）。
        // 🔴 **ここでは見送り（リトライ）に掛けない。** 照会の失敗は入力の段（CollectInputsAsync）で観測済みであり、TryDefer はそこで
        // 判定を終えている。ここで検出する食い違いは待っても変わらない（ここから先で見送りへ入る経路は散文だけである）。
        if (draft.Pnl.UnvaluedSettlementCount > 0)
            unsupplied.Add(ReportInput.OpeningInventory);

        // 散文の未供給＝プレースホルダ散文（LLM 未接続・縮退のいずれも。数値には関与しない）。
        if (string.Equals(draft.Narrative, ReportNarrativeDefaults.PlaceholderText, StringComparison.Ordinal))
            unsupplied.Add(ReportInput.Narrative);

        return draft;
    }

    // FR-06, 計画 ADR-0053 決定 3, #1172, IADR-0492 決定 6: 「集計したセッション」に書く市場。構成の対象市場（"US"/"JP"。
    // 列挙名も可・大小無視）のうち解釈できたものだけを書き、1 つも解釈できない（既定の空を含む）なら全市場を書く
    // （市場を黙って落とさない側へ倒す）。
    public static IReadOnlyCollection<Market> ReportedMarkets(IReadOnlyList<string> configured)
    {
        var markets = new HashSet<Market>();
        foreach (var value in configured)
        {
            var text = value.Trim();
            if (string.Equals(text, "US", StringComparison.OrdinalIgnoreCase))
                markets.Add(Market.UnitedStates);
            else if (string.Equals(text, "JP", StringComparison.OrdinalIgnoreCase))
                markets.Add(Market.Japan);
            else if (Enum.TryParse<Market>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                markets.Add(parsed);
        }

        return markets.Count > 0 ? markets : [.. ReportSessionWindow.Markets];
    }

    // FR-06, 計画 ADR-0052 決定 1, IADR-0491 決定 6: 月報 §7 の作り直しの回数。台帳が無い構成・読めないときは null（0 回と書かない）。
    private ReportRegenerationTally? SafeRegenerationTally(DueReport due)
    {
        if (regenerationLedger is null)
            return null;

        try
        {
            return regenerationLedger.Tally(due.PeriodStart, due.PeriodEnd, (regenerationLimit ?? ReportRegenerationLimit.Default).DailyLimit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // #840, IADR-0352 決定 3・4: 見送るかどうか。
    //
    // 見送るのは「未供給の入力のうち、取得中に**一過性**の失敗を観測したものがある」ときだけである。
    //   - 未設定（Unsupplied*）で欠けている入力は HTTP を出さない＝観測が無い＝見送らない（待っても変わらない）。
    //   - 403 などの恒常的な失敗だけなら見送らない（従来どおり縮退した報告書を出し、警告が残る）。
    //   - 途中で失敗したが最終的に供給できた入力（フォールバック成功）は、未供給に入らないので数えない。
    // 上限に達していれば見送らず、retriesExhausted を立てて呼び出し側（常駐）に警告させる。
    //
    // 🔴 #866, IADR-0352 決定 3 の 2026-09-19 追記: **見送りが生成窓の終端を跨ぐなら見送らない。**
    // ReportSchedule.Due は月報を当月内・週報を当 ISO 週内でしか due にしない（月報の窓は最短 7 時間＝
    // 最終営業日 17:00 JST 〜 月末 24:00 JST）。次に試す時刻に対象から外れていると、その期間は
    // **二度と due にならず、上限も効かないまま縮退版すら出ない**（警告も提示通知も無い＝無音の消失）。
    // 本変更前は同じ時刻に縮退した報告書が出ていたので、見送りだけを足すのは退行である。
    private ReportGenerationDeferral? TryDefer(
        DueReport due,
        IReadOnlyCollection<ReportInput> unsupplied,
        ReportDependencyObservation observation,
        ref bool retriesExhausted,
        ref bool windowClosing)
    {
        if (deferrals is null)
            return null;

        var waitingFor = ReportInputs.Parse(ReportInputs.Serialize(unsupplied.Where(observation.HasTransientFailure)));
        if (waitingFor.Count == 0)
            return null;

        // FR-06, FR-16, #1156, IADR-0480 決定 3: 中核の入力（約定・建玉・手動売買の取り込み）が一過性に欠けているなら
        // 上限を別に持ち、長く待つ。中核が欠けた報告書は「建玉なし」「取引なし」と読める主張を作るためである。
        // 窓の終端（#866）の判定は下で同じく効く（長く待っても、窓を跨いで無音で消えることはない）。
        var core = waitingFor.Any(ReportInputs.IsCore);

        // 待ち時間は**回数を消費せずに**先読みする（窓の外なら 1 回も数えない）。
        if (deferrals.NextDelay(due.PeriodKey, core) is not { } nextDelay)
        {
            // 上限 0（見送らない構成）は「使い切った」ではない。警告の文言を変えないために分ける。
            retriesExhausted = deferrals.LimitFor(core) > 0;
            return null;
        }

        if (!StillDueAfter(due, nextDelay))
        {
            windowClosing = true;
            // この期間はこの巡回を逃すと対象から消える。数えた回数も用済みである（生成による解放が起きない）。
            deferrals.Clear(due.PeriodKey);
            return null;
        }

        if (deferrals.TryDefer(due.PeriodKey, core) is not { } ticket)
        {
            retriesExhausted = deferrals.LimitFor(core) > 0;
            return null;
        }

        var causes = observation.Failures
            .Where(f => f.Transient && f.Input is { } input && waitingFor.Contains(input))
            .Select(f => $"{f.Dependency}: {f.Detail}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new ReportGenerationDeferral(
            due.PeriodKey, waitingFor, ticket.Attempt, ticket.MaxDeferrals, ticket.RetryAfter, causes);
    }

    // #866: 待ち時間の後も、この PeriodKey が生成対象（Due）に含まれているか。
    // 判定は ReportSchedule.Due そのものを使う（「窓の終端」を別式で書き直すと、境界の規則が 2 か所に割れる）。
    private bool StillDueAfter(DueReport due, TimeSpan delay) =>
        ReportSchedule.Due(clock.UtcNow + delay, settings.Schedule)
            .Any(d => string.Equals(d.PeriodKey, due.PeriodKey, StringComparison.Ordinal));

    // FR-09, IADR-0116 決定2: 提示（確定依頼）の通知。best-effort であり、失敗しても生成・提示は巻き戻さない
    // （報告書は既に永続化され承認待ちに並んでいる）。通知の不達で報告書を作り直すほうが害が大きい。
    //
    // ただし**黙って捨てない**。失敗は戻り値で呼び出し側へ伝え、常駐が警告ログに残す（Failed・NotPresented と同じ扱い）。
    // 通知が届かないまま気付けない状態は、NotifyOnDraftPresented を既定 true にした理由（有効化したのに何も届かない
    // 状態を作らない）と正面から矛盾するため。Application 層をログ基盤へ依存させないため、ここではログを出さない。
    private async Task<bool> NotifyAsync(DueReport due, string summary, int version, CancellationToken cancellationToken)
    {
        if (notifier is null)
            return true; // 通知ポート未注入＝通知しない構成。失敗ではない。

        try
        {
            await notifier.NotifyAsync(
                new PresentedReportNotice(
                    due.PeriodKey, due.Kind, ReportPeriod.Label(due.Kind, due.PeriodStart), summary, version),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 1 期間の生成結果（内部）。NotificationFailed は「提示はできたが通知の発行に失敗した」ことを表す。
    // #840: Deferral が非 null なら見送り（Report は null・保存も提示も通知もしていない）。
    private sealed record GenerationOutcome(
        TradingReport? Report,
        bool Presented,
        bool NotificationFailed,
        bool RetriesExhausted,
        bool WindowClosing,
        ReportGenerationDeferral? Deferral)
    {
        public static GenerationOutcome Deferred(ReportGenerationDeferral deferral) =>
            new(Report: null, Presented: false, NotificationFailed: false,
                RetriesExhausted: false, WindowClosing: false, deferral);
    }

    // FR-10, UC-06, #330, IADR-0133 決定7: 自動縮小の記録は**空列へ倒さない**。「発動があったのに『なし』と書く」ことは
    // 発動を隠したのと同じであり、記録の目的（「知らないうちに建玉が減っていた」状態の防止）が壊れる。
    // 取得できなければ null を返し、報告書に「照会できませんでした（要確認）」と明記させる。
    // 供給元そのものが未注入（既定構成）のときは空列＝「発動なし」（現状は発動があり得ないため事実として正しい）。
    private async Task<IReadOnlyList<MaintenanceMarginReductionExecuted>?> SafeReductionsAsync(
        DueReport due, CancellationToken cancellationToken)
    {
        if (reductionSource is null)
            return [];

        try
        {
            // FR-06, #1224, IADR-0516 決定 2・3: 窓を覆う JST の暦日の外包で引き、セッションの窓で絞る。
            var (window, from, to) = LedgerScope(due);
            var reductions = await reductionSource
                .GetReductionsAsync(from, to, cancellationToken)
                .ConfigureAwait(false);
            return reductions?.Within(window);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-10, UC-06, ADR-0016 決定4/決定15, #419, IADR-0159 決定3: 強制買戻し（推定）の記録は**空列へ倒さない**。
    // 決定15 は「推定経路が入るまで発生回数は供給されない。**供給が無い間は 0 件と表示してはならない**
    //（『強制買戻しは起きていない』に見えるため）」と明記している。
    //
    // **供給元が未注入（既定構成）のときも null＝未供給である**——自動縮小（SafeReductionsAsync が空列へ倒す）と
    // **向きが違う**。あちらは発火元が存在せず「発動なし」が事実として正しいが、**推定経路は実在し発火し得る**ため、
    // 報告書は「起きていない」ことを知らない。事実として正しくない値を既定にしない。
    // FR-06, FR-10, UC-06, #381, ADR-0022 決定2, IADR-0196 決定3, IADR-0199: 為替の情報源の状態。
    //
    // **未注入・照会失敗のいずれも null（未供給）である**——買戻し推定と同じ向きであり、自動縮小とは逆である。
    // 🔴 **為替のイベントは本番で実際に発行されている**（`PublishingFxSourceStatusNotifier` は登録済み）。
    // したがって「事象なし」を既定にすると**端的に嘘になる**。
    private async Task<FxSourceStatus?> SafeFxSourceStatusAsync(
        DueReport due, CancellationToken cancellationToken)
    {
        if (fxSourceStatusSource is null)
            return null;

        try
        {
            // FR-06, #1224, IADR-0516 決定 2・3: 窓を覆う JST の暦日の外包で引き、発生時刻（鮮度切れの決済は約定と同じ形）で絞る。
            var (window, from, to) = LedgerScope(due);
            var status = await fxSourceStatusSource
                .GetStatusAsync(from, to, cancellationToken)
                .ConfigureAwait(false);
            return status?.Within(window);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-06, FR-16, #338, #282, ADR-0017 決定2・決定4: LLM 利用実績。
    //
    // **未注入・照会失敗のいずれも null（未供給）である**——買戻し推定・為替と同じ向きであり、
    // 自動縮小（空列へ倒す）とは逆である。🔴 **LLM の呼び出しは本番で実際に行われている**
    // （報告書散文・取引判断のいずれも）。したがって「費用 0 円・発火 0 件」を既定にすると端的に嘘になる。
    // #282 は「計上されていない費用が誰にも見えなかった」事故であり、既定を空へ倒すことは同じ形の再発である。
    private async Task<LlmUsageRecord?> SafeLlmUsageAsync(DueReport due, CancellationToken cancellationToken)
    {
        if (llmUsageSource is null)
            return null;

        try
        {
            // FR-06, #1224, IADR-0516 決定 2: LLM 利用実績は**セッションの窓に揃えない**（JST の暦日 [PeriodStart, PeriodEnd]）。
            // 月次の LLM 費用上限は暦の月であり（05_trading-assumptions §6.1・月報に消費率を記載）、市場を持たない費用の集計である。
            // 窓と揃わないことは「集計したセッション」の行に暦日の範囲として書く（LlmUsageCalendarDays）。
            return await llmUsageSource
                .GetUsageAsync(due.PeriodStart, due.PeriodEnd, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-06, FR-15, ADR-0033 決定5・5.3, ADR-0037 決定3, #750: Stage 0 記録実行の**見積り承認額**。
    //
    // **未注入・読み取り失敗のいずれも null（未供給）である**——他の供給と同じ向きであり、
    // 🔴 **0 円へ倒さない**。承認が無いのに対比が成立して見えると、`ADR-0033` 決定5.3 の停止が
    // 働いたのかどうかを月報から誤って読むことになる（対比列を置いた理由そのものが失われる）。
    private decimal? SafeStage0RecordingEstimate()
    {
        if (stage0RecordingEstimateSource is null)
            return null;

        try
        {
            return stage0RecordingEstimateSource.GetApprovedEstimateJpy();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-06, #338, ADR-0016 決定15, ADR-0027 決定4: 借株料の記録。
    // **未注入・照会失敗のいずれも null（未供給）**——「借株コスト 0 USD」は費用が無かったと読める。
    private async Task<BorrowFeeRecord?> SafeBorrowFeesAsync(DueReport due, CancellationToken cancellationToken)
    {
        if (borrowFeeSource is null)
            return null;

        try
        {
            // FR-06, #1224, IADR-0516 決定 2・3: 窓を覆う JST の暦日の外包で引き、市場・記録の時刻で絞る（TradingDay は配置に使わない）。
            var (window, from, to) = LedgerScope(due);
            var record = await borrowFeeSource
                .GetBorrowFeesAsync(from, to, cancellationToken)
                .ConfigureAwait(false);
            return record?.Within(window);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-16, FR-11, #563, IADR-0269: 日報 §2 の判断根拠（記録の転記）。
    // **未注入・照会失敗のいずれも null（未供給）である**——買戻し推定・為替・LLM 実績と同じ向きであり、
    // 約定の供給（空列へ倒す）とは逆である。🔴 **判断根拠は本番で実際に記録されている**ため、
    // 「根拠なし」を既定にすると端的に嘘になり、説明責任が果たされていない状態が正常に見える。
    private async Task<IReadOnlyDictionary<Guid, string>?> SafeRationalesAsync(
        DueReport due, CancellationToken cancellationToken)
    {
        if (rationaleSource is null)
            return null;

        try
        {
            // FR-06, FR-16, #1172, IADR-0492 決定 3: 判断根拠は約定ごとの突き合わせ（DecisionId 引き）であり、期間の集計ではない。
            // 窓に入る米国の約定（ET D-1）の判断は JST D-1 の夜（22:30〜）に記録され得るため、照会は窓の始まり
            // （前の営業日の生成境界の JST 日付）から引く。広げても DecisionId 引きなので他の約定の根拠が混ざることは無い。
            var (from, to) = RationaleRange(due);
            return await rationaleSource
                .GetRationalesAsync(from, to, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-06, FR-16, #1172, IADR-0492 決定 3: 判断根拠の照会範囲（JST 取引日）。下端は期間の始まり・窓の照会範囲の始まり・
    // 窓の始まり（ClosedAfter）の JST 日付の最小、上端は期間の終わり。窓に入る約定はいずれも ClosedAfter より後に始まる
    // セッションに属するため、その判断の記録は下端以降にある。
    // #1224, IADR-0516 決定 3: 監査台帳の他の入力と同じ外包（ReportSessionWindow.JstLedgerRange。上端は期間の終わりと ClosedUntil の
    // JST 日付の最大＝期間の終わり）を使う。値は従来と同じ。
    private (DateOnly From, DateOnly To) RationaleRange(DueReport due)
    {
        var (_, from, to) = LedgerScope(due);
        return (from, to);
    }

    // FR-06, UC-03〜05, 計画 ADR-0053 決定 2, #1224, IADR-0516 決定 3: 監査台帳ほか JST の暦日で引く供給元へ渡す照会の範囲と、
    // 受け取った記録を絞る窓（自動生成と作り直しが同じ DueReport から引く）。
    private (ReportSessionWindow Window, DateOnly From, DateOnly To) LedgerScope(DueReport due)
    {
        var window = ReportSchedule.SessionWindowOf(due, settings.Schedule);
        var (from, to) = window.JstLedgerRange(due.PeriodStart, due.PeriodEnd);
        return (window, from, to);
    }

    // FR-06, FR-16, #1181, IADR-0493 決定 1・4: 期間開始時点の在庫を窓の**市場ごとの下端**（その市場の窓に入る最初の現地取引日）より前で引く。
    // 🔴 JST 0 時・期間の初日では切らない——約定の窓（IADR-0492）と同じ取引日で切らないと、窓の約定と二重に数えるか取りこぼす。
    // 市場は取引台帳が持ち得るすべて（約定の照会と同じ ReportSessionWindow.Markets）。1 市場でも取得できなければ全体を null（未供給）。
    // **未注入は呼ばない（呼び出し側が判定する）。照会失敗は null（未供給）である**——空（建玉なし）は別の主張になる。
    private async Task<OpeningInventorySnapshot?> SafeOpeningInventoryAsync(DueReport due, CancellationToken cancellationToken)
    {
        try
        {
            var window = ReportSchedule.SessionWindowOf(due, settings.Schedule);
            var lots = new List<OpeningLot>();
            foreach (var market in ReportSessionWindow.Markets)
            {
                var (from, _) = window.TradingDays(market);
                var marketLots = await openingInventorySource!
                    .GetOpeningInventoryAsync(market, from, cancellationToken)
                    .ConfigureAwait(false);
                if (marketLots is null)
                    return null;

                // 受け手の解釈（Interpret）が市場の食い違う応答を既に読めない（null）にしているので、ここは二つ目の安全弁である。
                lots.AddRange(marketLots.Where(l => l.Market == market));
            }

            return new OpeningInventorySnapshot(lots);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-06, FR-16, #563, IADR-0269: 日報 §3 のポジション一覧。
    // **未注入・照会失敗のいずれも null（未供給）である**——空列は「建玉なし」という別の主張になる。
    private async Task<IReadOnlyList<ReportPosition>?> SafeOpenPositionsAsync(CancellationToken cancellationToken)
    {
        if (openPositionSource is null)
            return null;

        try
        {
            return await openPositionSource.GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-06, FR-20, #569, INDEX 決定34, IADR-0271: 日報 §1 の稼働率・月報 §6.2 の分布。
    // **未注入・照会失敗のいずれも null（未供給）である**——為替・LLM 実績・判断根拠と同じ向きであり、
    // 約定の供給（空列へ倒す）とは逆である。🔴 **OpenD は本番で実際に稼働している**ため、
    // 「稼働率 0%」を既定にすると「終日停止していた」と読める端的な嘘になる。
    private async Task<OpenDUptimeRecord?> SafeUptimeAsync(DueReport due, CancellationToken cancellationToken)
    {
        if (uptimeSource is null)
            return null;

        try
        {
            // FR-06, #1172, IADR-0492 決定 4: 稼働率の日次は米国東部時間の取引日で記録される（Stage 1 の観測）。約定と同じ窓の
            // 米国の取引日で引く（報告書の期間〔JST の営業日〕で引くと、生成時点でまだ始まっていない ET の日を照会する）。
            // 窓に米国の取引日が 1 日も無い（休場日の日報）なら、観測された取引日が無いことが事実である。
            var (from, to) = ReportSchedule.SessionWindowOf(due, settings.Schedule).TradingDays(Market.UnitedStates);
            if (from > to)
                return new OpenDUptimeRecord([]);

            return await uptimeSource
                .GetUptimeAsync(from, to, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-06, FR-15, FR-20, #569, IADR-0271: 月報 §5 の三者比較が「空欄」と「0」を分けるための現在段階。
    // **未注入・照会失敗のいずれも null（未供給）＝節ごと「照会できませんでした」**であり、
    // 🔴 **Stage 0 等の既定へ倒さない**——到達済みの段の列を静かに空欄にする。
    private async Task<TradingStage?> SafeCurrentStageAsync(CancellationToken cancellationToken)
    {
        if (stageProgressSource is null)
            return null;

        try
        {
            return await stageProgressSource.GetCurrentStageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-06, FR-16, #611, 05_trading-assumptions §3, IADR-0286 決定2: 為替差損益の**期末レート**（期末日以前の直近の日次観測）。
    // **未注入・照会失敗のいずれも null（未供給）である**——為替・LLM 実績・稼働率と同じ向きであり、
    // 約定の供給（空列へ倒す）とは逆である。🔴 **USD 建ての建玉は本番で実際に存在し得る**ため、
    // 期末レートが無いのに「為替差損益 0 円」と描くと「為替では損得が無かった」という端的な嘘になる。
    private async Task<PeriodEndFxRate?> SafePeriodEndFxRateAsync(DueReport due, CancellationToken cancellationToken)
    {
        if (periodEndFxRateSource is null)
            return null;

        try
        {
            return await periodEndFxRateSource.GetRateAsync(due.PeriodEnd, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<BuyInInferred>?> SafeBuyInInferencesAsync(
        DueReport due, CancellationToken cancellationToken)
    {
        if (buyInSource is null)
            return null;

        try
        {
            // FR-06, #1224, IADR-0516 決定 2・3: 窓を覆う JST の暦日の外包で引き、市場・推定時刻で絞る。
            // 観測の被覆（FR-21）は外包の全日で判定される（窓は外包の前日の夜〔米国のセッション〕を含むため、その日の観測も要る）。
            var (window, from, to) = LedgerScope(due);
            var inferences = await buyInSource
                .GetInferencesAsync(from, to, cancellationToken)
                .ConfigureAwait(false);
            return inferences?.Within(window);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-06, FR-10, ADR-0040 決定1, #823, IADR-0422 決定3: 承認時点の損切りの実行機構の集計。
    // **未注入・照会失敗のいずれも null（未供給）**——「承認なし」は当日に新規建てが無かったと読める。
    private async Task<StopLossMethodUsage?> SafeStopLossMethodsAsync(DueReport due, CancellationToken cancellationToken)
    {
        if (stopLossMethodUsageSource is null)
            return null;

        try
        {
            // FR-06, #1224, IADR-0516 決定 2・3: 窓を覆う JST の暦日の外包で引き、承認の市場・承認時刻で絞る。
            var (window, from, to) = LedgerScope(due);
            var usage = await stopLossMethodUsageSource
                .GetUsageAsync(from, to, cancellationToken)
                .ConfigureAwait(false);
            // #1224, IADR-0516 決定 4: 月報 §6 の日数は、承認を数える日報の日付（報告可能になる瞬間を窓に含む日報）で数える。
            return usage?.Within(window, at => ReportSchedule.DailyReportDayOf(at, settings.Schedule));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-06, FR-10, ADR-0040 決定1, #1002, IADR-0429 決定4: 発注執行の損切りの実行機構の解決結果。
    // **未注入・照会失敗のいずれも null（未供給）**——空の記録は「承認はあるのに解決結果が無い」と読める。
    private async Task<StopLossMethodResolutionFeed?> SafeStopLossMethodResolutionsAsync(
        DueReport due, CancellationToken cancellationToken)
    {
        if (stopLossMethodResolutionSource is null)
            return null;

        try
        {
            // FR-06, #1224, IADR-0516 決定 2: 解決結果は承認と DecisionId で突き合わせる（解決の時刻では絞らない）。
            // 承認と同じ外包で引く（供給元が前後 1 日を足す）。
            var (_, from, to) = LedgerScope(due);
            return await stopLossMethodResolutionSource
                .GetResolutionsAsync(from, to, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // FR-11, ADR-0041 決定 1, #870, #859, IADR-0360 決定 2: 手動売買の取り込み。
    // 🔴 **不達は null（照会できていない）へ倒す。空列（該当なし）へ倒さない**——空列は日報 §2-b で嘘になり、
    // かつ在庫の畳み込みからも落ちて実在しない建玉の評価損益を出す。
    private async Task<IReadOnlyList<PeriodDriftAdoption>?> SafeDriftAdoptionsAsync(
        DueReport due, CancellationToken cancellationToken)
    {
        if (driftAdoptionSource is null)
            return null;

        try
        {
            // FR-06, #1172, IADR-0492 決定 3: 約定と同じ窓で絞る（§2 と §2-b・在庫の畳み込みが同じセッションを見る）。
            var window = ReportSchedule.SessionWindowOf(due, settings.Schedule);
            var (from, to) = window.QueryRange();
            var adoptions = await driftAdoptionSource
                .GetDriftAdoptionsAsync(from, to, cancellationToken)
                .ConfigureAwait(false);
            return adoptions is null ? null : [.. adoptions.Where(a => window.Includes(a.Market, a.AdoptedAt))];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // 供給不達は空列へ倒す（IADR-0115 決定5）。報告書は発注判断を行わないため、欠測が過大発注へ繋がる経路が無く、
    // 「数値 0 のドラフトを提示して気付かせる」ほうが「何も出さない」より安全である。
    //
    // #840, IADR-0352 決定 5: 倒す向きは変えないが、**倒れたこと**は呼び出し側へ返す（未供給として記録・提示する）。
    private async Task<(IReadOnlyList<PeriodTradeFill> Fills, bool Failed)> SafeFillsAsync(
        DueReport due, CancellationToken cancellationToken)
    {
        try
        {
            // FR-06, #1172, IADR-0492 決定 3: 報告書の期間（JST の営業日）を各市場の取引日としてそのまま引かない。
            // 生成境界までに大引けを迎えたセッションの窓を市場ごとの取引日へ写し、外包で照会してから市場ごとに絞る
            // （照会の契約〔市場の現地取引日の [from, to]〕は REST・gRPC とも変えない）。
            var window = ReportSchedule.SessionWindowOf(due, settings.Schedule);
            var (from, to) = window.QueryRange();
            var fills = await fillSource
                .GetFillsAsync(from, to, cancellationToken)
                .ConfigureAwait(false);
            return ([.. fills.Where(f => window.Includes(f.Market, f.ExecutedAt))], false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ([], true);
        }
    }
}

// FR-06, IADR-0115: 自動生成の構成。生成境界（JST）と休場日は ReportScheduleOptions（Domain・純関数の入力）へ委ねる。
public sealed record ReportAutoGenerationSettings
{
    /// <summary>生成境界（JST）と休場日。</summary>
    public ReportScheduleOptions Schedule { get; init; } = new();

    /// <summary>フロントマターの対象市場表記（"JP"/"US" 等）。既定は空。</summary>
    public IReadOnlyList<string> Markets { get; init; } = [];

    /// <summary>適用する全体前提条件のバージョン（FR-17）。既定 1。</summary>
    public int AssumptionsVersion { get; init; } = 1;

    /// <summary>提示（Present）の操作者。状態機械が actor 必須のため設定する（HTTP の OwnerOnly 認可は不変）。</summary>
    public string Actor { get; init; } = "report-scheduler";
}

// 1 巡回の結果。Generated は新規に生成した報告書、Failed は当該期間だけ落ちたもの（他期間は継続している）、
// NotPresented は生成できたが提示（PendingApproval への遷移）が受理されなかった期間（通常は空）、
// NotificationFailed は提示できたが確定依頼の通知に失敗した期間（通常は空）。
// いずれも常駐が警告ログに落とす＝異常を黙って捨てない（IADR-0115 決定1・IADR-0116 決定2）。
public sealed record ReportAutoGenerationResult(
    IReadOnlyList<TradingReport> Generated,
    IReadOnlyList<ReportAutoGenerationFailure> Failed,
    IReadOnlyList<string> NotPresented,
    IReadOnlyList<string> NotificationFailed)
{
    /// <summary>
    /// #840, IADR-0352 決定 3: 依存先が一過性に落ちていたため**生成を見送った**期間（保存・提示・通知のいずれもしていない）。
    /// 次の巡回で再び対象になる。常駐は次の巡回を早める。
    /// </summary>
    public IReadOnlyList<ReportGenerationDeferral> Deferred { get; init; } = [];

    /// <summary>
    /// #840, IADR-0352 決定 4: 生成はしたが**入力が未供給のまま**だった期間（Generated の部分集合）。
    /// 常駐が警告ログに落とす＝縮退を黙って通さない。
    /// </summary>
    public IReadOnlyList<ReportGenerationDegradation> Degraded { get; init; } = [];
}

// #840, IADR-0352 決定 3: 見送り 1 件。WaitingFor は一過性の失敗で欠けた入力、Causes は「依存先: 理由」の列。
public sealed record ReportGenerationDeferral(
    string PeriodKey,
    IReadOnlyList<ReportInput> WaitingFor,
    int Attempt,
    int MaxDeferrals,
    TimeSpan RetryAfter,
    IReadOnlyList<string> Causes);

// #840, IADR-0352 決定 4: 縮退して生成した 1 件。RetriesExhausted は「見送りの上限に達したので出した」、
// #866 の WindowClosing は「次に試す時刻には生成対象から外れる（窓が閉じる）ので、見送らずに出した」。
// 原因が違うので常駐は別の文言で警告する（同じ文にすると、なぜ縮退したのかを読み手が誤る）。
public sealed record ReportGenerationDegradation(
    string PeriodKey,
    IReadOnlyList<ReportInput> UnsuppliedInputs,
    bool RetriesExhausted,
    bool WindowClosing = false);


// 期間単位の失敗（常駐側のログ出力に用いる）。
public sealed record ReportAutoGenerationFailure(string PeriodKey, Exception Error);

// FR-06, FR-14, 計画 ADR-0052 決定 2・4, #1156, IADR-0491 決定 4: 期間の入力（自動生成と作り直しが同じ供給元・同じ規則で引いた値）。
// 各値の null は「照会できていない」であり、空・0 へ潰さない（各 Safe* の注記）。
public sealed record ReportInputSnapshot
{
    public required IReadOnlyList<PeriodTradeFill> Fills { get; init; }

    public IReadOnlyList<PeriodDriftAdoption>? DriftAdoptions { get; init; }

    public IReadOnlyList<MaintenanceMarginReductionExecuted>? MarginReductions { get; init; }

    public IReadOnlyList<BuyInInferred>? BuyInInferences { get; init; }

    public FxSourceStatus? FxSourceStatus { get; init; }

    public LlmUsageRecord? LlmUsage { get; init; }

    public BorrowFeeRecord? BorrowFees { get; init; }

    public IReadOnlyDictionary<Guid, string>? TradeRationales { get; init; }

    public IReadOnlyList<ReportPosition>? Positions { get; init; }

    public OpenDUptimeRecord? Uptime { get; init; }

    public StopLossMethodUsage? StopLossMethods { get; init; }

    public StopLossMethodResolutionFeed? StopLossMethodResolutions { get; init; }

    public TradingStage? CurrentStage { get; init; }

    public PeriodEndFxRate? PeriodEndFxRate { get; init; }

    /// <summary>FR-06, #1181, IADR-0493: 期間開始時点の在庫。<c>null</c>＝供給元が未注入、または照会できていない（後者は Unsupplied に入る）。</summary>
    public OpeningInventorySnapshot? OpeningInventory { get; init; }

    /// <summary>この種別が使う入力のうち、取得できなかった（または期間の時点に復元できないため取りに行かなかった）もの。</summary>
    public required IReadOnlyList<ReportInput> Unsupplied { get; init; }

    /// <summary>期間の時点に復元できないため取りに行かなかった入力（この種別が使うものだけ）。<see cref="Unsupplied"/> の部分集合。</summary>
    public IReadOnlyList<ReportInput> NotRestorable { get; init; } = [];

    /// <summary>
    /// 計画 ADR-0052 決定 4: <b>取得に失敗した</b>入力（<see cref="Unsupplied"/> から <see cref="NotRestorable"/> を除いたもの）。
    /// 復元できない入力は「取得の失敗」に含めない（含めると期間が過ぎた日報は常に断られ、決定 2 が意味を失う）。
    /// </summary>
    public IReadOnlyList<ReportInput> FetchFailed => [.. Unsupplied.Where(i => !NotRestorable.Contains(i))];
}
