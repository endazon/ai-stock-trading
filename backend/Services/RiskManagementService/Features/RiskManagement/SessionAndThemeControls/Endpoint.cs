using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.SessionAndThemeControls;

// ---- 閉場前の統制とテーマの上限（FR-10, FR-19, SC-02, UC-06, ADR-0070 決定 5, ADR-0071 決定 5, #1407, IADR-0564）----
// GET は**効いている値**・構成の値・保存値・どちらが効いているか・食い違い（保存値 ≠ 構成の値）を返す（画面は効いている値を表示する。ADR-0070 / ADR-0071 決定 5）。
// PUT は保存値を置き換える。**変更は利用者のみ**（OwnerOnly。生成 AI・サービス間呼び出し・AI の監視銘柄の入れ替え案〔ADR-0042〕は変更できない）・
// **理由必須**・前後値つきで履歴に残す（PreCloseControlsChanged / ThemeConcentrationChanged）。値域外は 400（設定も履歴も変えない）。
// SC-02 からの変更は BFF が本口へ素通しする（値域・理由・認可の実効は本口が持つ。IADR-0071）。
internal static class SessionAndThemeControlsEndpoint
{
    private static readonly Market[] KnownMarkets = [Market.Japan, Market.UnitedStates];

    public static void MapGetPreCloseControls(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/settings/pre-close",
            (RiskSettingsService svc, ConfiguredSessionAndThemeControls configured) =>
                Results.Ok(PreCloseControlsView.From(svc.GetCurrent(), configured)));

    public static void MapUpdatePreCloseControls(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/pre-close",
            (PreCloseControlsUpdateRequest req, RiskSettingsService svc, ConfiguredSessionAndThemeControls configured, HttpContext http) =>
        {
            // 省略（null）は 400。既定値への暗黙束縛で「送っていない値」へ黙って切り替えない（損切りの実行機構の変更と同じ規律）。
            if (req.CutoffMinutes is not { } cutoffRows || req.FlattenEnabled is not { } enabled || req.FlattenMinutes is not { } flattenRows)
            {
                return Results.BadRequest(new
                {
                    error = "cutoffMinutes（市場ごとの締め切り N）・flattenEnabled（手仕舞いの有効・無効）・flattenMinutes（市場ごとの M）を指定してください。",
                });
            }

            var details = new List<string>();
            var cutoff = ToMinutes(cutoffRows, "閉場前の締め切り", details);
            var flatten = ToMinutes(flattenRows, "閉場前の手仕舞い", details);
            if (details.Count == 0)
            {
                details.AddRange(SessionAndThemeControlValidation.Violations(new PreCloseEntryCutoffSettings(cutoff)));
                details.AddRange(new PreCloseFlattenSettings(enabled, flatten).Violations());
            }

            if (details.Count > 0)
                return Results.BadRequest(new { error = "閉場前の統制の設定が受理できません。", details });

            svc.UpdatePreCloseControls(
                new PreCloseEntryCutoffSettings(cutoff), new PreCloseFlattenSettings(enabled, flatten),
                RiskControlEndpoints.ActorOf(http), req.Reason ?? string.Empty);
            return Results.Ok(PreCloseControlsView.From(svc.GetCurrent(), configured));
        });

    public static void MapGetThemeConcentration(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/settings/theme-concentration",
            (RiskSettingsService svc, ConfiguredSessionAndThemeControls configured) =>
                Results.Ok(ThemeConcentrationControlView.From(svc.GetCurrent(), configured)));

    public static void MapUpdateThemeConcentration(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/theme-concentration",
            (ThemeConcentrationUpdateRequest req, RiskSettingsService svc, ConfiguredSessionAndThemeControls configured, HttpContext http) =>
        {
            if (req.Themes is not { } rows)
            {
                return Results.BadRequest(new { error = "themes（テーマの定義の一覧。名前・市場・銘柄・上限）を指定してください。" });
            }

            var details = new List<string>();
            var themes = new List<ThemeLimit>();
            foreach (var row in rows)
            {
                if (row is null || row.Market is not { } market || row.MaxPositions is not { } max || row.Symbols is not { } symbols)
                {
                    details.Add($"テーマ「{row?.Name}」の市場・銘柄・上限を指定してください。");
                    continue;
                }

                themes.Add(new ThemeLimit(row.Name ?? string.Empty, market, symbols, max));
            }

            var normalized = SessionAndThemeControlValidation.Normalize(new ThemeConcentrationSettings(themes));
            if (details.Count == 0)
                details.AddRange(SessionAndThemeControlValidation.Violations(normalized));
            if (details.Count > 0)
                return Results.BadRequest(new { error = "テーマの定義が受理できません。", details });

            svc.UpdateThemeConcentration(normalized, RiskControlEndpoints.ActorOf(http), req.Reason ?? string.Empty);
            return Results.Ok(ThemeConcentrationControlView.From(svc.GetCurrent(), configured));
        });

    // 市場ごとの行を辞書へ。既知の 2 市場のそれぞれちょうど 1 行を求める（欠けた市場を既定で黙って埋めない・重複を後勝ちにしない）。
    private static Dictionary<Market, int> ToMinutes(List<MarketMinutesRow?> rows, string label, List<string> details)
    {
        var minutes = new Dictionary<Market, int>();
        foreach (var row in rows)
        {
            if (row?.Market is not { } market || row.Minutes is not { } value)
            {
                details.Add($"{label}の行に市場（market）と分（minutes）を指定してください。");
                continue;
            }

            if (!KnownMarkets.Contains(market))
                details.Add($"{label}の市場「{(int)market}」は未知の市場です。");
            else if (!minutes.TryAdd(market, value))
                details.Add($"{label}の市場「{market}」が重複しています。");
        }

        foreach (var market in KnownMarkets.Where(m => !minutes.ContainsKey(m)))
            details.Add($"{label}に市場「{market}」の値がありません（米国・日本の両方を指定してください）。");

        return minutes;
    }
}

// FR-10, SC-02, #1407: 市場ごとの分（N または M）。各項目は nullable（省略をエンドポイントで 400 に弾く）。
internal sealed record MarketMinutesRow(Market? Market, int? Minutes);

// FR-10, UC-06, #1407: 閉場前の統制の変更要求（理由必須・FR-11）。
internal sealed record PreCloseControlsUpdateRequest(
    List<MarketMinutesRow?>? CutoffMinutes, bool? FlattenEnabled, List<MarketMinutesRow?>? FlattenMinutes, string? Reason);

// FR-10, UC-06, #1407: テーマの 1 行。
internal sealed record ThemeRow(string? Name, Market? Market, List<string>? Symbols, int? MaxPositions);

// FR-10, UC-06, #1407: テーマの定義の変更要求（定義の全体を置き換える。理由必須）。
internal sealed record ThemeConcentrationUpdateRequest(List<ThemeRow?>? Themes, string? Reason);

/// <summary>市場ごとの分の応答行（市場は数値の enum。画面の他の設定と同じ）。</summary>
public sealed record MarketMinutesView(Market Market, int Minutes);

/// <summary>締め切りの値（市場ごと）。</summary>
public sealed record PreCloseEntryCutoffValues(IReadOnlyList<MarketMinutesView> Minutes);

/// <summary>手仕舞いの値（有効・無効と市場ごとの M）。</summary>
public sealed record PreCloseFlattenValues(bool Enabled, IReadOnlyList<MarketMinutesView> Minutes);

/// <summary>
/// FR-10, SC-02, ADR-0070 決定 5, #1407, IADR-0564 決定 3: 閉場前の統制の応答。<c>Effective</c> が効いている値、<c>Configured</c> が構成の値、
/// <c>Saved</c> が画面で保存した値（無ければ null）、<c>*Source</c> がどちらが効いているか、<c>*Drift</c> が保存値と構成の値の食い違い。
/// <c>CutoffMaxMinutes</c> は市場ごとの N の上限（場中の長さ）、<c>FlattenMaxMinutes</c> は M の上限（60）。
/// </summary>
public sealed record PreCloseControlsView(
    PreCloseEntryCutoffValues EffectiveCutoff,
    PreCloseEntryCutoffValues ConfiguredCutoff,
    PreCloseEntryCutoffValues? SavedCutoff,
    ControlValueSource CutoffSource,
    bool CutoffDrift,
    PreCloseFlattenValues EffectiveFlatten,
    PreCloseFlattenValues ConfiguredFlatten,
    PreCloseFlattenValues? SavedFlatten,
    ControlValueSource FlattenSource,
    bool FlattenDrift,
    IReadOnlyList<MarketMinutesView> CutoffMaxMinutes,
    int FlattenMaxMinutes)
{
    private static readonly Market[] Markets = [Market.Japan, Market.UnitedStates];

    public static PreCloseControlsView From(RiskManagementSettings settings, ConfiguredSessionAndThemeControls configured)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configured);
        return new PreCloseControlsView(
            Cutoff(SessionAndThemeControlResolution.EffectivePreCloseEntryCutoff(settings, configured)),
            Cutoff(configured.PreCloseEntryCutoff),
            settings.PreCloseEntryCutoff is { } savedCutoff ? Cutoff(savedCutoff) : null,
            settings.PreCloseEntryCutoff is null ? ControlValueSource.Configuration : ControlValueSource.Saved,
            SessionAndThemeControlResolution.PreCloseEntryCutoffDrifts(settings, configured),
            Flatten(SessionAndThemeControlResolution.EffectivePreCloseFlatten(settings, configured)),
            Flatten(configured.PreCloseFlatten),
            settings.PreCloseFlatten is { } savedFlatten ? Flatten(savedFlatten) : null,
            settings.PreCloseFlatten is null ? ControlValueSource.Configuration : ControlValueSource.Saved,
            SessionAndThemeControlResolution.PreCloseFlattenDrifts(settings, configured),
            [.. Markets.Select(m => new MarketMinutesView(m, MarketSessions.RegularSessionMinutes(m)))],
            PreCloseFlattenSettings.MaxMinutes);
    }

    private static PreCloseEntryCutoffValues Cutoff(PreCloseEntryCutoffSettings s) =>
        new([.. Markets.Select(m => new MarketMinutesView(m, s.MinutesFor(m)))]);

    private static PreCloseFlattenValues Flatten(PreCloseFlattenSettings s) =>
        new(s.Enabled, [.. Markets.Select(m => new MarketMinutesView(m, s.MinutesFor(m)))]);
}

/// <summary>テーマの 1 行の応答。</summary>
public sealed record ThemeView(string Name, Market Market, IReadOnlyList<string> Symbols, int MaxPositions);

/// <summary>
/// FR-10, SC-02, ADR-0071 決定 5, #1407, IADR-0564 決定 3: テーマの定義の応答（効いている定義・構成の定義・保存した定義・どちらが効いているか・食い違い）。
/// </summary>
public sealed record ThemeConcentrationControlView(
    IReadOnlyList<ThemeView> Effective,
    IReadOnlyList<ThemeView> Configured,
    IReadOnlyList<ThemeView>? Saved,
    ControlValueSource Source,
    bool Drift)
{
    public static ThemeConcentrationControlView From(RiskManagementSettings settings, ConfiguredSessionAndThemeControls configured)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configured);
        return new ThemeConcentrationControlView(
            Rows(SessionAndThemeControlResolution.EffectiveThemeConcentration(settings, configured)),
            Rows(configured.ThemeConcentration),
            settings.ThemeConcentration is { } saved ? Rows(saved) : null,
            settings.ThemeConcentration is null ? ControlValueSource.Configuration : ControlValueSource.Saved,
            SessionAndThemeControlResolution.ThemeConcentrationDrifts(settings, configured));
    }

    private static IReadOnlyList<ThemeView> Rows(ThemeConcentrationSettings s) =>
        [.. s.Themes.Select(t => new ThemeView(t.Name, t.Market, [.. t.Symbols], t.MaxPositions))];
}
