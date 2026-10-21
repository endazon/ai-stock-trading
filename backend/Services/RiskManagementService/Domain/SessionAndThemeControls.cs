using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace RiskManagementService.Domain;

// 🔴 FR-10, FR-19, SC-02, ADR-0070 決定 2・5, #1407, IADR-0564 決定 1: 閉場前の手仕舞いの統制値（有効・無効と、市場ごとの M 分）。
// 置き場はリスク管理である（締め切り・テーマの上限と同じ設定行・同じ変更の手続き）。手仕舞いを出す常駐は取引判断にあり、
// 効いている値をサイジング文脈で受け取る（IADR-0527 決定 3 の高ボラティリティ銘柄の統制値と同じ経路）。
// 🔴 方針の書式の行（「閉場前の手仕舞い: 有効」）による有効化とは OR である。ここで無効でも方針の行で有効になり得る（ADR-0070 決定 2）。
public sealed record PreCloseFlattenSettings(bool Enabled, IReadOnlyDictionary<Market, int> Minutes)
{
    /// <summary>構成で変えられる M の上限（分）。ADR-0070 決定 2「1 分以上 60 分以下」。</summary>
    public const int MaxMinutes = 60;

    /// <summary>その市場の M（無い市場は既定の 15 分）。</summary>
    public int MinutesFor(Market market) =>
        Minutes.TryGetValue(market, out var minutes) ? minutes : TradingDefaults.PreCloseFlattenMinutes;

    /// <summary>値域の外（1〜60 分・既知の市場）の項目（0 件なら受理できる）。</summary>
    public IReadOnlyList<string> Violations()
    {
        var violations = new List<string>();
        foreach (var (market, minutes) in Minutes)
        {
            if (MarketSessions.RegularSessionMinutes(market) == 0)
                violations.Add($"閉場前の手仕舞いの市場「{market}」は未知の市場です。");
            else if (minutes is < 1 or > MaxMinutes)
                violations.Add($"閉場前の手仕舞いの {ControlFormatting.MarketName(market)} の M は 1〜{MaxMinutes} 分で指定してください（指定値 {minutes}）。");
        }

        return violations;
    }

    /// <summary>値の上で同じか（辞書は参照で比べない）。</summary>
    public bool SameAs(PreCloseFlattenSettings? other) =>
        other is not null && Enabled == other.Enabled && ControlFormatting.SameMinutes(Minutes, other.Minutes, MinutesFor, other.MinutesFor);

    // FR-11: 変更履歴の前後値に中身を残す（既定の ToString は辞書の型名しか出さない）。
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"PreCloseFlattenSettings {{ Enabled = {Enabled}, Minutes = {ControlFormatting.Minutes(Minutes)} }}");
}

/// <summary>
/// 🔴 FR-10, SC-02, ADR-0070 決定 5・ADR-0071 決定 5, #1407, IADR-0564 決定 2: <b>構成の値</b>（起動時に読んだ締め切り・手仕舞い・テーマの定義）。
/// 画面で保存した値（<see cref="RiskManagementSettings.PreCloseEntryCutoff"/> ほか）が無い項目は、この値が効く。
/// </summary>
public sealed record ConfiguredSessionAndThemeControls(
    PreCloseEntryCutoffSettings PreCloseEntryCutoff,
    PreCloseFlattenSettings PreCloseFlatten,
    ThemeConcentrationSettings ThemeConcentration)
{
    /// <summary>既定（締め切り 60 分・手仕舞いは無効で 15 分・既定の 2 テーマ）。試験と構成が無いときの値。</summary>
    public static ConfiguredSessionAndThemeControls Default { get; } = new(
        TradingDefaults.CreatePreCloseEntryCutoff(),
        TradingDefaults.CreatePreCloseFlatten(),
        TradingDefaults.CreateThemeConcentration());
}

/// <summary>どちらの値が効いているか。</summary>
public enum ControlValueSource
{
    /// <summary>画面で保存した値が無い。構成の値が効いている。</summary>
    Configuration,

    /// <summary>画面で保存した値が効いている（構成の値は効いていない）。</summary>
    Saved,
}

/// <summary>
/// 🔴 FR-10, SC-02, ADR-0070 決定 5・ADR-0071 決定 5, #1407, IADR-0564 決定 2・3: 効いている値の解決と、保存値と構成の値の食い違いの検知。
/// <para>
/// <b>保存値があれば保存値が効く。無ければ構成の値が効く</b>（項目ごと＝締め切り・手仕舞い・テーマの定義の 3 つを別々に解決する）。
/// 構成は「画面で保存するまでの値」である。保存値が構成と違うときは <c>Drift</c> を真にし、画面に構成の値と並べて示す
/// （構成を変えて再起動しても、保存値がある項目には効かない。それを利用者が読めるようにする）。
/// </para>
/// </summary>
public static class SessionAndThemeControlResolution
{
    public static PreCloseEntryCutoffSettings EffectivePreCloseEntryCutoff(RiskManagementSettings settings, ConfiguredSessionAndThemeControls configured)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configured);
        return settings.PreCloseEntryCutoff ?? configured.PreCloseEntryCutoff;
    }

    public static PreCloseFlattenSettings EffectivePreCloseFlatten(RiskManagementSettings settings, ConfiguredSessionAndThemeControls configured)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configured);
        return settings.PreCloseFlatten ?? configured.PreCloseFlatten;
    }

    public static ThemeConcentrationSettings EffectiveThemeConcentration(RiskManagementSettings settings, ConfiguredSessionAndThemeControls configured)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configured);
        return settings.ThemeConcentration ?? configured.ThemeConcentration;
    }

    /// <summary>締め切りの保存値と構成の値が違うか（保存値が無ければ false＝構成の値が効いている）。</summary>
    public static bool PreCloseEntryCutoffDrifts(RiskManagementSettings settings, ConfiguredSessionAndThemeControls configured) =>
        settings.PreCloseEntryCutoff is { } saved && !SameAs(saved, configured.PreCloseEntryCutoff);

    /// <summary>手仕舞いの保存値と構成の値が違うか。</summary>
    public static bool PreCloseFlattenDrifts(RiskManagementSettings settings, ConfiguredSessionAndThemeControls configured) =>
        settings.PreCloseFlatten is { } saved && !saved.SameAs(configured.PreCloseFlatten);

    /// <summary>テーマの定義の保存値と構成の値が違うか（名前・市場・銘柄の集合〔大文字小文字・空白を区別しない〕・上限で比べる。並び順は比べない）。</summary>
    public static bool ThemeConcentrationDrifts(RiskManagementSettings settings, ConfiguredSessionAndThemeControls configured) =>
        settings.ThemeConcentration is { } saved && !SameAs(saved, configured.ThemeConcentration);

    public static bool SameAs(PreCloseEntryCutoffSettings a, PreCloseEntryCutoffSettings b) =>
        ControlFormatting.SameMinutes(a.Minutes, b.Minutes, a.MinutesFor, b.MinutesFor);

    public static bool SameAs(ThemeConcentrationSettings a, ThemeConcentrationSettings b)
    {
        if (a.Themes.Count != b.Themes.Count)
            return false;

        var left = a.Themes.Select(ThemeKey).Order(StringComparer.Ordinal).ToArray();
        var right = b.Themes.Select(ThemeKey).Order(StringComparer.Ordinal).ToArray();
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private static string ThemeKey(ThemeLimit theme) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{theme.Name.Trim()}|{theme.Market}|{theme.MaxPositions}|{string.Join(",", theme.Symbols.Select(s => s.Trim().ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal))}");
}

/// <summary>
/// FR-10, SC-02, ADR-0070 決定 1・5, ADR-0071 決定 2・5, #1407, IADR-0564 決定 4: 画面（API）から保存する値の値域と正規化。
/// <b>値域の実効はサーバが持つ</b>（画面の検証はその代わりにならない）。構成の読み込み（起動を止める <c>Validate</c>）と同じ値域である。
/// </summary>
public static class SessionAndThemeControlValidation
{
    /// <summary>締め切り N の値域の外（1〜場中の長さ・既知の市場・市場の重複は辞書で起きない）。</summary>
    public static IReadOnlyList<string> Violations(PreCloseEntryCutoffSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var violations = new List<string>();
        foreach (var (market, minutes) in settings.Minutes)
        {
            var max = MarketSessions.RegularSessionMinutes(market);
            if (max == 0)
                violations.Add($"閉場前の締め切りの市場「{market}」は未知の市場です。");
            else if (minutes < 1 || minutes > max)
                violations.Add($"閉場前の締め切りの {ControlFormatting.MarketName(market)} の N は 1〜{max} 分で指定してください（指定値 {minutes}）。締め切りを外す値は持ちません。");
        }

        return violations;
    }

    /// <summary>テーマの定義の値域の外（ADR-0071 決定 2）。<paramref name="settings"/> は <see cref="Normalize"/> の後の値を渡す。</summary>
    public static IReadOnlyList<string> Violations(ThemeConcentrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var violations = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in settings.Themes)
        {
            if (string.IsNullOrWhiteSpace(theme.Name))
            {
                violations.Add("テーマの名前が空です。");
                continue;
            }

            if (!names.Add(theme.Name))
                violations.Add($"テーマの名前「{theme.Name}」が重複しています。");
            if (MarketSessions.RegularSessionMinutes(theme.Market) == 0)
                violations.Add($"テーマ「{theme.Name}」の市場「{theme.Market}」は未知の市場です。");
            if (theme.Symbols.Count == 0)
                violations.Add($"テーマ「{theme.Name}」に銘柄がありません（1 つ以上）。");
            if (theme.Symbols.Any(string.IsNullOrWhiteSpace))
                violations.Add($"テーマ「{theme.Name}」に空の銘柄コードがあります。");
            if (theme.MaxPositions < 1)
                violations.Add($"テーマ「{theme.Name}」の上限は 1 以上で指定してください（指定値 {theme.MaxPositions}）。上限を外す値は持ちません。");
        }

        return violations;
    }

    /// <summary>
    /// 保存の前の正規化: 名前の前後の空白を落とし、銘柄コードは前後の空白を落として大文字へ揃え、テーマの中の重複を除く（照合は大文字小文字と空白を区別しない。
    /// 履歴と画面に残る値を揃える）。空の銘柄コードは残す（<see cref="Violations(ThemeConcentrationSettings)"/> が拒む）。
    /// </summary>
    public static ThemeConcentrationSettings Normalize(ThemeConcentrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ThemeConcentrationSettings(
        [
            .. settings.Themes.Select(t => t with
            {
                Name = (t.Name ?? string.Empty).Trim(),
                Symbols = [.. (t.Symbols ?? []).Select(s => (s ?? string.Empty).Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal)],
            }),
        ]);
    }
}

/// <summary>辞書の中身の比較と、履歴・メッセージ用の表記。</summary>
internal static class ControlFormatting
{
    public static string MarketName(Market market) => market switch
    {
        Market.UnitedStates => "米国",
        Market.Japan => "日本",
        _ => market.ToString(),
    };

    public static string Minutes(IReadOnlyDictionary<Market, int> minutes) =>
        "{" + string.Join(", ", minutes.OrderBy(kv => kv.Key).Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} = {kv.Value}"))) + "}";

    /// <summary>既知の 2 市場について、効く値（無い市場は既定）で比べる。</summary>
    public static bool SameMinutes(
        IReadOnlyDictionary<Market, int> a, IReadOnlyDictionary<Market, int> b, Func<Market, int> aFor, Func<Market, int> bFor) =>
        a.Keys.Concat(b.Keys).Append(Market.UnitedStates).Append(Market.Japan).Distinct().All(m => aFor(m) == bFor(m));
}
