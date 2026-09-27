using System.Globalization;
using System.Text.Json;
using InformationCollectionService.Features.InformationCollection;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// NFR（費用）, IADR-0031: 費用統制（#23）の GET /costs/state を同期照会して統制ゲートに写像する。
// 未取得・非 2xx・例外・タイムアウト・不正応答は Normal（停止せず・1×）の安全既定に倒す。
// 注意: Normal は「間隔延長/停止を止められない側」の縮退だが、月次予算は緩変で短時間の不達では超過しにくく、
// 費用統制の一時障害で取引サイクル全体を止める（Halt）のは過大なため（IADR-0031）。
// 認証（IADR-0051・#76 完了）: /costs/state は OwnerOrService へ分離済みで、本呼び出しは HttpClient に付与された
// client_credentials サービストークン（trading-service）で認証される（Program.cs の AddAiStockTradingServiceToken）。
// ServiceAuth:ClientId/ClientSecret 未設定なら従来どおり認証ヘッダなし＝401 → Normal の安全既定に倒れる。
public sealed class HttpCostControlGate(
    HttpClient httpClient,
    ILogger<HttpCostControlGate> logger)
    : ICostControlGate
{
    // CostControlDecision（費用統制）の JSON を、CostControlService.Domain を参照せず isHalted/intervalMultiplier で疎結合に読む。
    // FR-01, NFR（費用）, #915, IADR-0031: 両項目を nullable で読み、「項目が無い」を既定値（false / 0）と区別する。
    // 非 nullable だと本文 {} が (false, 0) になり、0× のまま写っていた（消費側の下限 1 で隠れていただけ）。
    //
    // FR-01, NFR（費用）, #1063 B: **項目ごとに**読む。以前は 1 つの DTO へ一括で逆直列化していたため、倍率だけが読めない
    // （文字列の "two"・decimal の範囲を超える桁）と本文全体が不正応答になり、**停止の旗が読めていても Normal（停止せず）**へ倒れていた
    // —— #915 の規則（停止は倍率に関わらず守る）より弱い。項目の読み方（名前の大小を区別しない・数値は文字列でも読む）は
    // 以前の Web 既定の逆直列化と同じにしてあり、変わるのは「停止が読めて倍率が読めない」ときに停止を守るようになる点だけである。

    public async Task<CostControlGate> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/costs/state", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("費用統制の照会に失敗（{Status}）。Normal（停止せず）に倒します。", (int)response.StatusCode);
                return CostControlGate.Normal;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Null)
                    logger.LogWarning("費用統制の応答がオブジェクトではありません。Normal（停止せず）に倒します。");
                return CostControlGate.Normal;
            }

            var isHalted = ReadIsHalted(document.RootElement);
            var multiplier = ReadIntervalMultiplier(document.RootElement, out var unreadable);
            if (unreadable)
                logger.LogWarning("費用統制の応答の intervalMultiplier を読めません。倍率は無いものとして扱います（停止の旗は守ります）。");

            return Map(isHalted, multiplier, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("費用統制の照会がタイムアウト。Normal（停止せず）に倒します。");
            return CostControlGate.Normal;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "費用統制の照会で例外。Normal（停止せず）に倒します。");
            return CostControlGate.Normal;
        }
    }

    // FR-01, NFR（費用）, #915, IADR-0031（2026-09-25 追記）: 200 OK の本文を統制ゲートへ写す。
    // - isHalted が明示的に true: 停止を尊重する（倍率は見ない）。送り手は Halted で倍率 0（無効値）を返すのが正常であり、
    //   倍率の欠落・非正を理由に停止を Normal へ落とすと「費用上限 100% でも収集を続ける」側へ倒れるため。
    // - isHalted が欠落: 停止か否かを判定できない不正応答として Normal（不達・非 2xx と同じ安全既定）。
    // - isHalted が false で倍率が欠落・非正: 「費用統制は何も言っていない」を 0× と読まず Normal（1×）。
    // 項目の読み取り（以前の Web 既定の逆直列化と同じ規則: 名前の大小を区別しない・真偽は true/false のみ・倍率は数値か数値の文字列）。
    private static JsonElement? Property(JsonElement root, string name)
    {
        JsonElement? found = null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                found = property.Value;
        }

        return found;
    }

    private static bool? ReadIsHalted(JsonElement root) => Property(root, "isHalted") switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        // 欠落・null・真偽でない値は「判定できない」（以前は真偽でない値で本文全体が不正応答＝Normal。Map でも Normal になる）。
        _ => null,
    };

    // #1065 F2a: 数値の文字列の書式は以前の Web 既定の逆直列化（数値の文字列を読む・前後の空白は拒む）と同じにする。`NumberStyles.Float` は
    // 前後の空白を許し、`" 2"` を 2 倍として受け付けていた（以前は本文全体が不正応答＝Normal 1 倍）。符号・小数点・指数だけを許す。
    private const NumberStyles MultiplierStringStyles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    private static decimal? ReadIntervalMultiplier(JsonElement root, out bool unreadable)
    {
        unreadable = false;
        switch (Property(root, "intervalMultiplier"))
        {
            case null:
            case { ValueKind: JsonValueKind.Null }:
                return null;
            case { ValueKind: JsonValueKind.Number } number when number.TryGetDecimal(out var value):
                return value;
            case { ValueKind: JsonValueKind.String } text
                when decimal.TryParse(text.GetString(), MultiplierStringStyles, CultureInfo.InvariantCulture, out var value):
                return value;
            default:
                unreadable = true;
                return null;
        }
    }

    // NFR, IADR-0446 決定 4, #1061 (#753): 写しは gRPC 実装（GrpcCostControlGate）と共有する（`internal static`）。
    internal static CostControlGate Map(bool? isHaltedOrMissing, decimal? intervalMultiplier, ILogger logger)
    {
        if (isHaltedOrMissing is not bool isHalted)
        {
            logger.LogWarning("費用統制の応答に isHalted がありません。Normal（停止せず）に倒します。");
            return CostControlGate.Normal;
        }

        if (isHalted)
            return new CostControlGate(true, intervalMultiplier ?? 0m);

        if (intervalMultiplier is not decimal multiplier || multiplier <= 0m)
        {
            logger.LogWarning("費用統制の応答の intervalMultiplier が欠落または非正（{Multiplier}）。Normal（1×）に倒します。",
                intervalMultiplier);
            return CostControlGate.Normal;
        }

        return new CostControlGate(false, multiplier);
    }
}
