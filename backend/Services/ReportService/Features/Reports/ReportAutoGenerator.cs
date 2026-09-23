using ReportService.Common.Exceptions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;

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
    IPeriodDriftAdoptionSource? driftAdoptionSource = null)
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

        observation.Enter(ReportInput.OpenPositions);
        var positions = await SafeOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
        if (positions is null)
            unsupplied.Add(ReportInput.OpenPositions);

        observation.Enter(ReportInput.OpenDUptime);
        var uptime = await SafeUptimeAsync(due, cancellationToken).ConfigureAwait(false);
        if (uptime is null)
            unsupplied.Add(ReportInput.OpenDUptime);

        observation.Enter(ReportInput.CurrentStage);
        var currentStage = await SafeCurrentStageAsync(cancellationToken).ConfigureAwait(false);
        if (currentStage is null)
            unsupplied.Add(ReportInput.CurrentStage);

        observation.Enter(ReportInput.PeriodEndFxRate);
        var periodEndFxRate = await SafePeriodEndFxRateAsync(due, cancellationToken).ConfigureAwait(false);
        if (periodEndFxRate is null)
            unsupplied.Add(ReportInput.PeriodEndFxRate);

        // この種別が使わない入力の欠落は数えない（週報は建玉を描かない。警告にも見送りの判定にも混ぜない）。
        // Stage 0 の見積り承認額は構成値であり、未設定（承認が無い）が通常の状態のため、そもそも数えない。
        unsupplied.RemoveWhere(input => !ReportInputs.AppliesTo(input, due.Kind));

        // #840, IADR-0352 決定 3: **散文（LLM）を呼ぶ前に**見送りを判定する。入力が欠けたままの回に
        // LLM 費用を出さない（見送る回の散文は捨てるしかない）。
        var retriesExhausted = false;
        var windowClosing = false;
        if (TryDefer(due, unsupplied, observation, ref retriesExhausted, ref windowClosing) is { } deferredForInputs)
            return GenerationOutcome.Deferred(deferredForInputs);

        observation.Enter(ReportInput.Narrative);

        // 数値はコード集計・散文は LLM ドラフト（IADR-0032）。現在値は要求で指定せず、市場データ源へ委ねる（IADR-0066）。
        //
        // FR-07, IADR-0120 決定3, #293: 上位方針は **PeriodKey だけでなく本文まで**散文ドラフトへ渡す。
        // 従来は取得済みの parent から PeriodKey のみを使い PolicySummary を破棄していたため、計画
        // （04_workflows/03_reporting-cycle）が求める「上位方針の目標との差異評価」を LLM が書けなかった。
        // 渡し先は散文の文脈のみ。方針文（policy）へは混ぜない（IADR-0115 決定4・ADR-0003）。
        //
        // IADR-0125 決定4, #310: 渡すのは**方針の実体だけ**（Substance）。累積済みのレコードを持つ環境では
        // 上位方針の本文が定型文で膨らんでおり、そのまま渡すとプロンプトの大半が前置きで埋まる。
        var draft = await draftService.BuildDraftAsync(
            new DraftRequest(
                due.Kind, due.PeriodKey, due.PeriodStart, settings.Markets, settings.AssumptionsVersion,
                parent?.Report.PeriodKey, policy, fills, CurrentPrices: null,
                ParentPolicySummary: ReportPolicyDraft.Substance(parent?.Report.PolicySummary),
                MarginReductions: reductions,
                BuyInInferences: buyIns,
                FxSourceStatus: fxStatus,
                LlmUsage: llmUsage,
                Stage0RecordingApprovedEstimateJpy: SafeStage0RecordingEstimate(),
                BorrowFees: borrowFees,
                TradeRationales: rationales,
                Positions: positions,
                Uptime: uptime,
                CurrentStage: currentStage,
                PeriodEndFxRate: periodEndFxRate,
                DriftAdoptions: driftAdoptions),
            cancellationToken).ConfigureAwait(false);

        // 散文の未供給＝プレースホルダ散文（LLM 未接続・縮退のいずれも。数値には関与しない）。
        if (string.Equals(draft.Narrative, ReportNarrativeDefaults.PlaceholderText, StringComparison.Ordinal))
        {
            unsupplied.Add(ReportInput.Narrative);
            if (TryDefer(due, [ReportInput.Narrative], observation, ref retriesExhausted, ref windowClosing)
                is { } deferredForNarrative)
            {
                return GenerationOutcome.Deferred(deferredForNarrative);
            }
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
        var summary = ReportSummary.Build(
            due.Kind, ReportPeriod.Label(due.Kind, due.PeriodStart), draft.Pnl, draft.Narrative, unsuppliedInputs);

        // FR-09, IADR-0116 決定2: 提示まで到達したものだけ通知する（承認待ちに無いものを「確認してください」と言わない）。
        var notificationFailed = presented
            && !await NotifyAsync(due, summary, version, cancellationToken).ConfigureAwait(false);

        return new GenerationOutcome(
            report, presented, notificationFailed, retriesExhausted, windowClosing, Deferral: null);
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

        // 待ち時間は**回数を消費せずに**先読みする（窓の外なら 1 回も数えない）。
        if (deferrals.NextDelay(due.PeriodKey) is not { } nextDelay)
        {
            // 上限 0（見送らない構成）は「使い切った」ではない。警告の文言を変えないために分ける。
            retriesExhausted = deferrals.MaxDeferrals > 0;
            return null;
        }

        if (!StillDueAfter(due, nextDelay))
        {
            windowClosing = true;
            // この期間はこの巡回を逃すと対象から消える。数えた回数も用済みである（生成による解放が起きない）。
            deferrals.Clear(due.PeriodKey);
            return null;
        }

        if (deferrals.TryDefer(due.PeriodKey) is not { } ticket)
        {
            retriesExhausted = deferrals.MaxDeferrals > 0;
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
            return await reductionSource
                .GetReductionsAsync(due.PeriodStart, due.PeriodEnd, cancellationToken)
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
            return await fxSourceStatusSource
                .GetStatusAsync(due.PeriodStart, due.PeriodEnd, cancellationToken)
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
            return await borrowFeeSource
                .GetBorrowFeesAsync(due.PeriodStart, due.PeriodEnd, cancellationToken)
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
            return await rationaleSource
                .GetRationalesAsync(due.PeriodStart, due.PeriodEnd, cancellationToken)
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
            return await uptimeSource
                .GetUptimeAsync(due.PeriodStart, due.PeriodEnd, cancellationToken)
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
            return await buyInSource
                .GetInferencesAsync(due.PeriodStart, due.PeriodEnd, cancellationToken)
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
            return await driftAdoptionSource
                .GetDriftAdoptionsAsync(due.PeriodStart, due.PeriodEnd, cancellationToken)
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

    // 供給不達は空列へ倒す（IADR-0115 決定5）。報告書は発注判断を行わないため、欠測が過大発注へ繋がる経路が無く、
    // 「数値 0 のドラフトを提示して気付かせる」ほうが「何も出さない」より安全である。
    //
    // #840, IADR-0352 決定 5: 倒す向きは変えないが、**倒れたこと**は呼び出し側へ返す（未供給として記録・提示する）。
    private async Task<(IReadOnlyList<PeriodTradeFill> Fills, bool Failed)> SafeFillsAsync(
        DueReport due, CancellationToken cancellationToken)
    {
        try
        {
            var fills = await fillSource
                .GetFillsAsync(due.PeriodStart, due.PeriodEnd, cancellationToken)
                .ConfigureAwait(false);
            return (fills, false);
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
