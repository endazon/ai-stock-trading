using System.Text.Json;
using System.Text.Json.Serialization;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

// IADR-0012: RiskManagementSettings の JSON 直列化。ドメイン型の Guard は IReadOnlySet/IReadOnlyCollection を
// 用いており System.Text.Json が逆直列化時に具象化できないため、具象コレクションの永続 DTO を介して双方向変換する。
// Limits/Stage/BannedSymbol は位置/必須プロパティのレコードで、標準の直列化が往復可能。
internal static class RiskSettingsSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(RiskManagementSettings settings)
    {
        var dto = new SettingsDto(
            new GuardDto(
                [.. settings.Guard.EnabledProductTypes],
                [.. settings.Guard.EnabledMarkets],
                [.. settings.Guard.BannedSymbols],
                settings.Guard.PreventSameDayReentry,
                settings.Guard.ProhibitManipulativeOrderPatterns,
                settings.Guard.ConfiguredAccountType),
            settings.Limits,
            settings.Stage,
            settings.ShortSell,
            settings.BrokerProvider);
        return JsonSerializer.Serialize(dto, Options);
    }

    public static RiskManagementSettings Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<SettingsDto>(json, Options)
            ?? throw new InvalidOperationException("リスク管理設定の JSON を逆直列化できませんでした。");

        var guard = new TradingGuardSettings
        {
            EnabledProductTypes = new HashSet<ProductType>(dto.Guard.EnabledProductTypes),
            EnabledMarkets = new HashSet<Market>(dto.Guard.EnabledMarkets),
            BannedSymbols = dto.Guard.BannedSymbols,
            PreventSameDayReentry = dto.Guard.PreventSameDayReentry,
            ProhibitManipulativeOrderPatterns = dto.Guard.ProhibitManipulativeOrderPatterns,
            // FR-19, #375, ADR-0021 決定1: 口座種別を持たない旧行は**信用口座**（既定）として読む。
            // 本値は統制の切り替えには使わず、照会結果との**食い違いの検知**にのみ使う（決定3）。
            // したがって「旧行を既定で読む」ことは統制を緩めない——照会結果が無ければ新規建ては止まる。
            ConfiguredAccountType = dto.Guard.ConfiguredAccountType ?? AccountType.Margin,
        };
        return new RiskManagementSettings(guard, dto.Limits, dto.Stage)
        {
            // FR-10, ADR-0016, #329 第 2 段階: 空売り統制を持たない旧行は**既定（無効）**として読む。
            // 「読めない行は空売り有効」に倒れないことが要点である（フェイルクローズ）。
            ShortSell = dto.ShortSell ?? TradingDefaults.CreateShortSellSettings(),
            // FR-20 (3), #334, #422, IADR-0140 決定4, IADR-0161: 発注先は **allow-list** で解決する。
            // 3 値の明示一致だけを受理し、それ以外（本項目を持たない旧行・null・未知の序数・未知の文字列・
            // 大小文字違い・別の型）は**すべて内蔵 paper**（外部へ一度も発注しない値）へ落とす。
            // 計画（FR-20 の 2026-08-07 追記 (3)）が名指しする「読めない行は実弾」に倒れないための構造であり、
            // `?? 既定` という deny-list では未知の値が素通りする（本 issue が是正した欠陥）。
            BrokerProvider = BrokerProviderResolution.Resolve(dto.BrokerProvider),
        };
    }

    // 具象コレクションを持つ永続 DTO（逆直列化可能な形）。
    private sealed record SettingsDto(
        GuardDto Guard,
        RiskLimitSettings Limits,
        StageSettings Stage,
        ShortSellSettings? ShortSell = null,
        // FR-20 (3), #334, #422: 発注先。nullable＝本プロパティの追加前に書かれた行（旧行はキーを持たないため
        // null のまま allow-list へ入り内蔵 paper になる）。**マイグレーションで既存行を書き換えない**——
        // 設定は単一行 JSON であり、「列を足す」に相当するのはキーを足すことである。旧行の中身は変えず、
        // 既定値は読み取り時に allow-list が与える（IADR-0161 決定2）。
        [property: JsonConverter(typeof(BrokerProviderJsonConverter))]
        BrokerProvider? BrokerProvider = null);

    private sealed record GuardDto(
        List<ProductType> EnabledProductTypes,
        List<Market> EnabledMarkets,
        List<BannedSymbol> BannedSymbols,
        bool PreventSameDayReentry,
        bool ProhibitManipulativeOrderPatterns,
        // FR-19, #375: 利用者が設定した口座種別。nullable＝本プロパティの追加前に書かれた行。
        AccountType? ConfiguredAccountType = null);
}

/// <summary>
/// FR-20 (3), #422, IADR-0161 決定2: 設定ストア（単一行 JSON）の発注先を <b>allow-list</b> で読む変換器。
/// <para>
/// <b>どのトークンが来ても例外を投げない。</b>標準の enum 変換は、数値なら範囲外の序数をそのまま通し、
/// 文字列・真偽値・オブジェクトなら <c>JsonException</c> を投げる。後者は一見フェイルクローズだが、
/// 実際には<b>設定行全体が読めなくなる</b>——統制値・ガード設定・段階もろとも失われ、リスク判定が動かない。
/// 計画（FR-20 の 2026-08-07 追記 (3)）は「同じ既定へ落とす」と明言しており、そちらへ従う。
/// </para>
/// <para>書き込みは現行どおり数値（序数）である。ワイヤ形式を変えると旧版が読めなくなる。</para>
/// </summary>
internal sealed class BrokerProviderJsonConverter : JsonConverter<BrokerProvider>
{
    public override BrokerProvider Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                // 範囲外・int に収まらない数値はいずれも allow-list が既定へ落とす。
                return reader.TryGetInt32(out var ordinal)
                    ? BrokerProviderResolution.Resolve((BrokerProvider)ordinal)
                    : BrokerProviderResolution.Default;

            case JsonTokenType.String:
                // 正準名・序数の 10 進表記の完全一致のみ（大小文字を区別する）。
                return BrokerProviderResolution.Resolve(reader.GetString());

            case JsonTokenType.Null:
                return BrokerProviderResolution.Default;

            default:
                // 真偽値・オブジェクト・配列。読み飛ばして既定へ落とす（例外にして設定行ごと失わない）。
                reader.Skip();
                return BrokerProviderResolution.Default;
        }
    }

    public override void Write(Utf8JsonWriter writer, BrokerProvider value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue((int)value);
    }
}
