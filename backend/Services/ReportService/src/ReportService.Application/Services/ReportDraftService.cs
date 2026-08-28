using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.Report.Application.Services;

// FR-06/07/16, 04_report-templates, IADR-0032: 報告書ドラフトの生成オーケストレーション（日報/週報/月報）。
// 約定列＋前提条件から数値をコード集計（PnlAggregator）し、散文は LLM ドラフト（IReportNarrativeDrafter）で得て、
// ReportRenderer でテンプレートへ組み立てる（数値は LLM に計算させない）。生成のみでステートレス（永続化しない）。
//
// #81, IADR-0066: 評価損益（税引前・参考）の現在値は、要求が指定していないときだけ市場データ源から補完する
// （要求指定は上書きしない＝既存 API 互換）。marketData 未注入・取得不可なら現在値なし＝評価損益 0（現行挙動）。
public sealed class ReportDraftService(IReportNarrativeDrafter drafter, IMarketDataSource? marketData = null)
{
    public async Task<ReportDraft> BuildDraftAsync(DraftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PeriodKey);

        // periodKey と種別/対象日の不整合を弾く（Date を正とする）。不整合な報告書が後続の永続化/KB 保存で残らないようにする。
        var periodLabel = ReportPeriod.Label(request.Kind, request.Date);
        var expectedKey = ReportPeriod.ExpectedKey(request.Kind, request.Date);
        if (!string.Equals(request.PeriodKey, expectedKey, StringComparison.Ordinal))
            throw new ArgumentException($"{request.Kind} 報告書の PeriodKey は種別・対象日と一致する必要があります（期待 '{expectedKey}'・実際 '{request.PeriodKey}'）。");

        var markets = request.Markets ?? [];
        var fills = request.Fills ?? [];

        // 評価損益の現在値: 要求指定が最優先。無ければ市場データ源から補完する（#81・IADR-0066）。
        var currentPrices = request.CurrentPrices
            ?? await ResolveCurrentPricesAsync(fills, cancellationToken).ConfigureAwait(false);

        // 数値はコード集計（FR-16）。前提条件は暫定で既定値（#19 バージョン付き取得・#63 台帳連携は #22 後続）。
        var pnl = PnlAggregator.Aggregate(fills, TradingAssumptionsDefaults.Create(), currentPrices);

        var buyCount = fills.Count(f => f.Side == TradeSide.Buy);
        var sellCount = fills.Count(f => f.Side == TradeSide.Sell);

        // FR-07, IADR-0120 決定3, #293: 上位方針（BasedOn の期間キー＋本文）を散文の文脈として渡す。
        // 期間キーと本文の**両方が揃ったときだけ**参照とする。片方だけでは差異評価も出典提示もできないため、
        // 欠損は「上位未確定」の 1 通りに閉じる（プロンプト側がその旨を明記する＝捏造しない）。
        var parentPolicy = !string.IsNullOrWhiteSpace(request.BasedOn) && !string.IsNullOrWhiteSpace(request.ParentPolicySummary)
            ? new ParentPolicyReference(request.BasedOn, request.ParentPolicySummary)
            : null;

        // 散文のみ LLM ドラフトへ委ねる（数値は提示のみで再計算させない）。
        // #335, ADR-0017 決定4-(1), IADR-0217: あわせて**実際に使用したモデル**を受け取り、報告書のメタ情報へ残す。
        // 「月報が第 1 候補で書かれたのか第 2 候補で書かれたのかは、その月報を方針書として採用する際の判断材料である」。
        var draft = await drafter
            .DraftAsync(
                new ReportNarrativeContext(
                    request.Kind, request.PeriodKey, periodLabel, markets, pnl, request.PolicySummary, parentPolicy),
                cancellationToken)
            .ConfigureAwait(false);
        var narrative = draft.Text;

        var view = new ReportView
        {
            Kind = request.Kind,
            PeriodKey = request.PeriodKey,
            PeriodLabel = periodLabel,
            Markets = markets,
            AssumptionsVersion = request.AssumptionsVersion,
            BasedOn = request.BasedOn,
            ConfirmedAt = null, // ドラフトは未確定
            Pnl = pnl,
            BuyCount = buyCount,
            SellCount = sellCount,
            PolicySummary = request.PolicySummary,
            Narrative = narrative,
            // FR-10, UC-06, #330: 自動縮小の記録はコード集計値であり LLM に語らせない（散文と分ける）。
            MarginReductions = request.MarginReductions,
            // FR-10, UC-06, ADR-0016 決定4/決定15, #419: 強制買戻し（推定）も同様にコード集計値である。
            // **null（未供給）を空列（推定 0 件）へ潰さない**（決定15: 供給が無い間は 0 件と表示しない）。
            BuyInInferences = request.BuyInInferences,
            // FR-10, FR-06, UC-06, #381: 為替の情報源の状態もコード集計値である（散文に語らせない）。
            // **null（未供給）を空（事象なし）へ潰さない**——「照会できませんでした」と「劣化なし」は別物である。
            FxSourceStatus = request.FxSourceStatus,
            // #335, ADR-0017 決定4-(1), IADR-0217: **null（未供給）を「フォールバックなし」へ潰さない。**
            // プレースホルダ実装・縮退時はモデルを知り得ないため、節ごと出さない（他の未供給項目と同じ扱い）。
            LlmModelUsage = draft.ModelUsage,
            // #338: 以下はいずれもコード集計値であり、散文（LLM）には渡さない（FR-16）。
            // **null（未供給）を空・0 へ潰さない**——既存の各節と同じ規律である。
            LlmUsage = request.LlmUsage,
            BorrowFees = request.BorrowFees,
            FxTranslation = request.FxTranslation,
            Uptime = request.Uptime,
            ThreeWayComparison = request.ThreeWayComparison,
        };

        return new ReportDraft(ReportRenderer.RenderMarkdown(view), pnl, narrative);
    }

    // #81, IADR-0066: 期間末に建玉が残る銘柄の現在値を市場データ源から引く（全決済済みは評価に不要なので引かない
    // ＝無駄な市況取得でレート制限を消費しない）。取得不可の銘柄はキーを含めない＝当該建玉の評価損益は 0。
    //
    // PnlAggregator の現在値辞書は**銘柄コードのみ**がキーで市場を持たない（IADR-0025）。そのため同一銘柄コードが
    // 複数市場に現れる場合は市場を判別できず、**曖昧としてキーを落とす**（誤った市場の価格で評価するより 0 に倒す）。
    private async Task<IReadOnlyDictionary<string, decimal>?> ResolveCurrentPricesAsync(
        IReadOnlyList<PeriodTradeFill> fills,
        CancellationToken cancellationToken)
    {
        if (marketData is null || fills.Count == 0)
            return null;

        // IADR-0033: 平均取得単価法の畳み込みは共有の純関数（SignedInventory）を単一情報源とする。
        // ここでは建玉の有無（数量 ≠ 0）だけが要るため、符号付き在庫のみを畳み込む。
        var inventory = new Dictionary<(string Symbol, Market Market), InventoryLot>();
        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            var key = (fill.Symbol, fill.Market);
            var signedQ = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            inventory.TryGetValue(key, out var lot);
            inventory[key] = SignedInventory.Apply(lot, signedQ, fill.Price).Lot;
        }

        var open = inventory.Where(e => e.Value.Quantity != 0).Select(e => e.Key).ToList();

        // 銘柄コードが複数市場にまたがるものは判別できないため対象から外す。
        var ambiguous = open.GroupBy(k => k.Symbol).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        var prices = new Dictionary<string, decimal>();
        foreach (var (symbol, market) in open)
        {
            if (ambiguous.Contains(symbol))
                continue;

            var quote = await marketData.GetLatestQuoteAsync(symbol, market, cancellationToken).ConfigureAwait(false);
            if (quote is not null)
                prices[symbol] = quote.Price;
        }

        return prices;
    }
}

// 報告書ドラフト生成の要求。Kind で日報/週報/月報を切り替える。Fills は集計対象の約定列（#63 台帳の実データ連携は #22 後続）。
//
// FR-07, IADR-0120 決定3, #293: ParentPolicySummary は上位方針（BasedOn が指す報告書）の本文。
// BasedOn（期間キー）だけでは「上位方針の目標との差異評価」が書けないため本文を伴わせる。
// null＝上位未確定。既定 null により既存の呼び出し側は非破壊で通る。
public sealed record DraftRequest(
    ReportKind Kind,
    string PeriodKey,
    DateOnly Date,
    IReadOnlyList<string>? Markets,
    int AssumptionsVersion,
    string? BasedOn,
    string PolicySummary,
    IReadOnlyList<PeriodTradeFill>? Fills,
    IReadOnlyDictionary<string, decimal>? CurrentPrices,
    string? ParentPolicySummary = null,
    // FR-10, UC-06, #330: 当期間の「維持率割れによる自動縮小」。空列＝発動なし／null＝照会できていない
    // （04_report-templates は「空欄と『なし』を区別する」ことを求める）。既定 null により既存の呼び出しは非破壊。
    IReadOnlyList<MaintenanceMarginReductionExecuted>? MarginReductions = null,
    // FR-10, UC-06, ADR-0016 決定4/決定15, #419: 当期間に強制買戻しと**推定**した件。
    // **空列＝推定 0 件／null＝供給が無い**（決定15: 供給が無い間は 0 件と表示してはならない）。
    // 既定 null は「未供給」であり、既存の呼び出しは非破壊で通る。
    IReadOnlyList<BuyInInferred>? BuyInInferences = null,
    // FR-06, FR-10, UC-06, #381, ADR-0022 決定2, IADR-0196 決定3, IADR-0199: 当期間の為替の情報源の状態。
    // **空の FxSourceStatus＝事象なし／null＝照会できていない**（「切替なし」と書かないため区別する）。
    // 既定 null は「未供給」であり、既存の呼び出しは非破壊で通る。
    FxSourceStatus? FxSourceStatus = null,
    // FR-06, FR-16, #338, #282, ADR-0017 決定2・決定4, 04_report-templates 月報 §7: 当期間の LLM 利用実績。
    // **null＝照会できていない**（費用 0 円・スキップ 0 件と書かない）。既定 null で既存の呼び出しは非破壊。
    LlmUsageRecord? LlmUsage = null,
    // FR-06, #338, ADR-0016 決定15, ADR-0027 決定4: 当期間の借株料の記録。**null＝照会できていない**。
    BorrowFeeRecord? BorrowFees = null,
    // FR-06, FR-16, #338: 為替差損益（独立表示）。**null＝供給されていない**（0 円と書かない）。
    // 本 PR では供給元が無い（換算前の外貨額と換算レートを台帳が保持していない）。
    FxTranslationSummary? FxTranslation = null,
    // FR-06, FR-20, #338, INDEX 決定34: OpenD 稼働率。**null＝照会できていない**（稼働率 0% と書かない）。
    OpenDUptimeRecord? Uptime = null,
    // FR-06, FR-15, #338, 04_report-templates 月報 §5: 三者比較。**null＝照会できていない**。
    ThreeWayComparison? ThreeWayComparison = null);

// 生成結果（Markdown 本文＋集計した数値サマリ＋LLM ドラフトの散文）。永続化はしない。
// Narrative を分けて返すのは、Discord 提示の要約（IADR-0116）が散文を Markdown から再抽出せずに済むようにするため。
public sealed record ReportDraft(string Markdown, PnlSummary Pnl, string Narrative);
