using System.Text.Json;
using System.Text.Json.Serialization;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

// IADR-0012: RiskManagementSettings の JSON 直列化。ドメイン型の Guard は IReadOnlySet/IReadOnlyCollection を
// 用いており System.Text.Json が逆直列化時に具象化できないため、具象コレクションの永続 DTO を介して双方向変換する。
// Limits/BannedSymbol は位置/必須プロパティのレコードで、標準の直列化が往復可能。
//
// FR-20 (3), #431, IADR-0163 決定1: Stage も **DTO（StageDto）を介す**。段階の既定発注先（StageSettings.Mode）は
// 現在の発注先（SettingsDto.BrokerProvider）と**同じ BrokerProvider 型**であり、同じ allow-list を通す必要がある。
// ドメイン型 StageSettings へ [JsonConverter] を直付けすると永続化の関心がドメインへ漏れるため、GuardDto と
// 同じ形で DTO を挟む。
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
            new StageDto(settings.Stage.Stage, settings.Stage.Mode, settings.Stage.CapitalCapRatio),
            settings.ShortSell,
            settings.BrokerProvider,
            settings.Stage1MinimumTradeCount);
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
        // FR-20 (3), #431, IADR-0163 決定1: 段階の既定発注先は StageDto.Mode の converter が **allow-list** で
        // 解決済みである（解決できない値は内蔵 paper）。ここで再度 Resolve を挟まない——解決の単一情報源を
        // converter に保ち、属性を外す変異がテストで検知できる状態にしておくためである。
        var stage = new StageSettings(dto.Stage.Stage, dto.Stage.Mode, dto.Stage.CapitalCapRatio);
        return new RiskManagementSettings(guard, dto.Limits, stage)
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
            // FR-20, FR-13, SC-02, #423, IADR-0164 決定5: Stage 1 の最小取引件数は**既定 100 へ落として読む**。
            // 本項目を持たない旧行（null）も、手編集などで入り込んだ値域外の値も同じ扱いである。
            // **倒す先は「厳しい側」＝合格に遠い値**である（発注先の allow-list＝IADR-0161 と同型だが、
            // そちらは「外部へ発注しない値」へ倒す）。読めない行が「少ない件数」へ倒れると、
            // 擬似的に合格へ近づく＝統制が緩む側であり、しかも画面には正常な設定として現れる。
            Stage1MinimumTradeCount = Stage1TradeCountBounds.Resolve(dto.Stage1MinimumTradeCount),
        };
    }

    // 具象コレクションを持つ永続 DTO（逆直列化可能な形）。
    private sealed record SettingsDto(
        GuardDto Guard,
        RiskLimitSettings Limits,
        StageDto Stage,
        ShortSellSettings? ShortSell = null,
        // FR-20 (3), #334, #422: 発注先。nullable＝本プロパティの追加前に書かれた行（旧行はキーを持たないため
        // null のまま allow-list へ入り内蔵 paper になる）。**マイグレーションで既存行を書き換えない**——
        // 設定は単一行 JSON であり、「列を足す」に相当するのはキーを足すことである。旧行の中身は変えず、
        // 既定値は読み取り時に allow-list が与える（IADR-0161 決定2）。
        [property: JsonConverter(typeof(BrokerProviderJsonConverter))]
        BrokerProvider? BrokerProvider = null,
        // FR-20, FR-13, SC-02, #423: Stage 1 の最小取引件数。nullable＝本プロパティの追加前に書かれた行
        // （旧行はキーを持たないため null のまま入り、`Stage1TradeCountBounds.Resolve` が既定 100 を与える）。
        // **マイグレーションで既存行を書き換えない**（BrokerProvider と同じ規律・IADR-0161 決定2）。
        int? Stage1MinimumTradeCount = null);

    private sealed record GuardDto(
        List<ProductType> EnabledProductTypes,
        List<Market> EnabledMarkets,
        List<BannedSymbol> BannedSymbols,
        bool PreventSameDayReentry,
        bool ProhibitManipulativeOrderPatterns,
        // FR-19, #375: 利用者が設定した口座種別。nullable＝本プロパティの追加前に書かれた行。
        AccountType? ConfiguredAccountType = null);

    // FR-20 (3), #431, IADR-0161 / IADR-0163 決定1: 段階設定の永続 DTO。
    //
    // **`Mode` は現在の発注先と同じ `BrokerProvider` 型であり、同じ allow-list を通す。** 属性を持たない
    // 標準の直列化では、未知の**序数**は素通りし（`!= MoomooReal` により実弾は止まるが構造の担保ではない）、
    // 未知の**文字列**・真偽値・オブジェクト・配列は `JsonException` になって**設定行全体が読めなくなる**
    // ——統制値・ガード・段階もろとも失われ、`GetCurrent` が 500 を返しリスク判定そのものが動かない。
    // IADR-0161 が「採らない」と明記した挙動そのものであり、その適用範囲を本 DTO へ広げる。
    //
    // **プロパティ名・順序・ワイヤ形式は `StageSettings` と同一に保つ**（`stage` / `mode` / `capitalCapRatio`、
    // 書き込みは数値の序数）。変えると旧行・旧版が読めなくなる。旧行を書き換える移行は行わない
    // （既定は読み取り時に allow-list が与える。IADR-0161 決定2）。
    private sealed record StageDto(
        TradingStage Stage,
        [property: JsonConverter(typeof(BrokerProviderJsonConverter))]
        BrokerProvider Mode,
        decimal CapitalCapRatio);
}

/// <summary>
/// FR-20 (3), #422, #431, IADR-0161 決定2 / IADR-0163 決定1: 設定ストア（単一行 JSON）の発注先を
/// <b>allow-list</b> で読む変換器。<b>現在の発注先（<c>SettingsDto.BrokerProvider</c>）と段階の既定発注先
/// （<c>StageDto.Mode</c>）の両方に適用する</b>——同じ設定行の隣り合う 2 項目は同じ型であり、
/// 片方だけを塞いでも同じ到達経路（手編集された行・外部ツールが書いた行）が残る。
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
