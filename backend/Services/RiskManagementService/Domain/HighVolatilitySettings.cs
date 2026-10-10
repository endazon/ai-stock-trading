using System.Globalization;
using AiStockTrading.Shared.Contracts.Errors;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

/// <summary>
/// FR-10, UC-06, ADR-0063 決定1, #1291, IADR-0527 決定2: 利用者が明示指定した高ボラティリティ銘柄（銘柄コード・市場）。
/// </summary>
public sealed record HighVolatilitySymbol(string Symbol, Market Market);

/// <summary>
/// 🔴 FR-10, UC-06, ADR-0063 決定1・決定2, #1291, IADR-0527 決定1・決定2: 高ボラティリティ銘柄の統制値
/// （05_trading-assumptions §5「高ボラティリティ銘柄の区分」「高ボラティリティ銘柄の 1 注文あたりの発注金額上限」）。
/// <para>
/// <b>区分の上限</b>（<see cref="MaxOrderAmountRatio"/>。既定 equity の 5%）と、<b>利用者の明示指定</b>（<see cref="DesignatedSymbols"/>）を持つ。
/// 自動判定（ATR(14) ÷ 参照価格 ≥ 4%）の値は持たない（計算は <see cref="HighVolatilityOrderCap"/>。しきい値は確定単一値で構成で変えない）。
/// </para>
/// <para>
/// 置き場はリスク管理の設定（利用者だけが変える統制値）である。監視銘柄（市場監視）には置かない —— 監視銘柄は AI の入れ替え案
/// （ADR-0042）からも書かれる経路を持ち、ADR-0063 決定1「AI の入れ替え案からは指定させない」を構造で守れない。審査（最終防衛線）が
/// 他サービスの照会に依らず自分の設定だけで判定できる（IADR-0527 決定2）。
/// </para>
/// </summary>
public sealed record HighVolatilitySettings
{
    /// <summary>区分の 1 注文あたりの発注金額上限（equity 比。既定 0.05）。範囲は <see cref="HighVolatilityOrderCap.Validate"/>。</summary>
    public decimal MaxOrderAmountRatio { get; init; } = TradingDefaults.HighVolatilityMaxOrderAmountRatio;

    /// <summary>利用者が明示指定した銘柄（既定は空）。明示指定は厳しい側へだけ効く（外す操作で自動判定を打ち消せない）。</summary>
    public IReadOnlyList<HighVolatilitySymbol> DesignatedSymbols { get; init; } = [];

    /// <summary>
    /// (銘柄, 市場) が明示指定されているか。銘柄コードは前後の空白と大小文字を無視して比べる（照合の取りこぼしで緩い側へ倒さない）。
    /// </summary>
    public bool IsDesignated(string symbol, Market market) =>
        symbol is not null
        && DesignatedSymbols.Any(s => s.Market == market
            && string.Equals(s.Symbol?.Trim(), symbol.Trim(), StringComparison.OrdinalIgnoreCase));

    // 一覧は参照ではなく中身で比べる（設定の往復・前後値の比較で同じ指定を別物と読まない）。
    public bool Equals(HighVolatilitySettings? other) =>
        other is not null
        && MaxOrderAmountRatio == other.MaxOrderAmountRatio
        && DesignatedSymbols.SequenceEqual(other.DesignatedSymbols);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(MaxOrderAmountRatio);
        foreach (var symbol in DesignatedSymbols)
            hash.Add(symbol);
        return hash.ToHashCode();
    }

    // FR-11: 変更履歴の前後値（SettingsChangeEntry.Before / After）に一覧の中身を残す（既定の ToString は型名しか出さない）。
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"HighVolatilitySettings {{ MaxOrderAmountRatio = {MaxOrderAmountRatio}, DesignatedSymbols = [{string.Join(", ", DesignatedSymbols.Select(s => $"{s.Symbol}({s.Market})"))}] }}");
}

/// <summary>
/// 🔴 FR-10, ADR-0063 決定1〜5, #1291, IADR-0527 決定1・決定3: 高ボラティリティ銘柄の区分の判定と、区分に応じた 1 注文あたりの発注金額上限。
/// <para>
/// <b>サイジング（取引判断）・LLM の前の見送り（取引判断）・審査（リスク管理）・Stage 0 の記録が、この 1 か所の関数を呼ぶ。</b>
/// どこか 1 か所だけ判定や上限が違うと、サイジングの数量が審査で拒否される（#29 と同じ形）か、見送りの判定が実際の数量と合わなくなる
/// （ADR-0063 フォローアップ 3）。純関数（時計・I/O なし）。
/// </para>
/// </summary>
public static class HighVolatilityOrderCap
{
    /// <summary>区分の上限の構成範囲の上端（区分外の 1 注文上限の既定＝equity の 25%。ADR-0063 決定2）。</summary>
    public const decimal MaxRatioUpperBound = 0.25m;

    /// <summary>
    /// 区分の上限の構成範囲の下端（新規建ての最小の名目額の比率の既定＝equity の 1%。ADR-0063 決定2）。これ未満では区分の銘柄の数量は
    /// 必ず 0 になり、設定として意味を持たない。🔴 下端ちょうどでも端数切り捨てでほぼ常に見送りになる（禁じない。利用者が選ぶ余地）。
    /// </summary>
    public const decimal MinRatioLowerBound = TradingDefaults.MinEntryNotionalRatio;

    /// <summary>
    /// 自動判定: ATR(14, 日足) ÷ 参照価格 ≥ 4% か（<see cref="TradingDefaults.HighVolatilityAtrRatioThreshold"/>）。<b>ちょうど 4% は区分に入る。</b>
    /// ATR が得られない（null・0 以下）か参照価格が正でなければ <b>false</b>（自動判定は働かず、明示指定だけで判定する。ADR-0063 決定1）。
    /// ATR と参照価格は同じ通貨（銘柄のローカル通貨）で渡す（比は通貨に依らない）。
    /// </summary>
    public static bool IsAutoClassified(decimal? atr, decimal referencePrice) =>
        atr is > 0m && referencePrice > 0m
        && atr.Value / referencePrice >= TradingDefaults.HighVolatilityAtrRatioThreshold;

    /// <summary>区分の判定: 明示指定 <b>または</b> 自動判定のどちらかが成り立てば区分に入る（ADR-0063 決定1）。</summary>
    public static bool IsHighVolatility(
        HighVolatilitySettings settings, string symbol, Market market, decimal? atr, decimal referencePrice)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.IsDesignated(symbol, market) || IsAutoClassified(atr, referencePrice);
    }

    /// <summary>
    /// 1 注文あたりの発注金額上限（基準通貨）。区分外は従来の <see cref="RiskLimitSettings.MaxOrderAmountFor"/>（既定 25%）そのもの。
    /// 区分の銘柄は <b>区分外の上限と equity × 区分の比率の小さい方</b>（「常に厳しい方が効く」。区分外の上限を 5% より下げた構成でも、
    /// 区分の銘柄が区分外より緩くならない。ADR-0063 決定5）。
    /// </summary>
    public static decimal MaxOrderAmountFor(
        RiskLimitSettings limits, HighVolatilitySettings settings, decimal equity, bool isHighVolatility)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(settings);
        var standard = limits.MaxOrderAmountFor(equity);
        return isHighVolatility ? Math.Min(standard, equity * settings.MaxOrderAmountRatio) : standard;
    }

    /// <summary>
    /// FR-10, UC-06, ADR-0063 決定2: 設定値の妥当性（空なら受理してよい）。比率は <see cref="MinRatioLowerBound"/> 以上 <see cref="MaxRatioUpperBound"/> 以下。
    /// 明示指定の銘柄コードは空でないこと・市場は既知の値であること。
    /// </summary>
    public static IReadOnlyList<string> Validate(HighVolatilitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var violations = new List<string>();

        if (settings.MaxOrderAmountRatio < MinRatioLowerBound || settings.MaxOrderAmountRatio > MaxRatioUpperBound)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture,
                $"高ボラティリティ銘柄の 1 注文あたりの発注金額上限（equity 比）は {MinRatioLowerBound}（新規建ての最小の名目額）以上 "
                + $"{MaxRatioUpperBound}（区分外の上限）以下でなければなりません（指定値: {settings.MaxOrderAmountRatio}）。"));
        }

        if (settings.DesignatedSymbols is null)
        {
            violations.Add("明示指定の銘柄の一覧がありません（指定しない場合は空の一覧を送ってください）。");
            return violations;
        }

        foreach (var symbol in settings.DesignatedSymbols)
        {
            if (symbol is null || string.IsNullOrWhiteSpace(symbol.Symbol))
                violations.Add("明示指定の銘柄コードは空にできません。");
            else if (!Enum.IsDefined(symbol.Market))
                violations.Add($"明示指定の銘柄 {symbol.Symbol} の市場が不明です。");
        }

        return violations;
    }

    /// <summary>値域に反していれば <see cref="ArgumentException"/> を投げる（違反の全件をメッセージへ含める）。</summary>
    public static void ThrowIfInvalid(HighVolatilitySettings settings)
    {
        var violations = Validate(settings);
        if (violations.Count > 0)
        {
            throw new ArgumentException(
                $"高ボラティリティ銘柄の設定が受理できません。{string.Join(" / ", violations)}", nameof(settings))
                .ClientVisible();
        }
    }
}
