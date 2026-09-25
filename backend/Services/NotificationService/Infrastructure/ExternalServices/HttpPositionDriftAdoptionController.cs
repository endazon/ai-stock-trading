using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-10, FR-11, FR-14, UC-06, ADR-0041 決定 4, #871, IADR-0350, IADR-0423:
// 台帳とブローカーの乖離の取り込み（リスク管理の OwnerOnly エンドポイント `POST /risk-controls/position-drift/adopt`）を
// 呼ぶだけのアダプタ。kill switch / GFV 解除 / 段階ゲートと同型。
//
// 当該エンドポイントは OwnerOnly（trading-owner）であり、s2s トークン（trading-service）では 403 になる。
// 本アダプタが使う名前付き HttpClient には Bot 専用の owner マップ機密クライアントのトークンを付与する。
// 資格情報が未設定ならトークン無し＝401 となり操作は失敗する（安全側）。
//
// 失敗時の方針: 握り潰さない。利用者が結果を知る必要がある操作であり、「失敗を成功に見せない」ことが安全側になる。
// 🔴 **拒否（422）の理由はリスク管理の文言をそのまま返す**——「観測が古い」「乖離が無い」「減らす乖離だけ」など、
// 何が足りないか・どうすれば通るかが書いてある（IADR-0350）。「受理されませんでした」だけでは次の一手が分からない。
internal sealed class HttpPositionDriftAdoptionController(
    HttpClient httpClient,
    ILogger<HttpPositionDriftAdoptionController> logger)
    : IPositionDriftAdoptionController
{
    private const string OwnerHint = "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）";

    private const string NotAdopted = "取り込みは行いませんでした（台帳は変わっていません）";

    // #871, IADR-0423: 200 の応答に操作者が無い＝リスク管理が本件より前の版（代理を解さない）。窓の間に取り込むと、
    // 台帳の操作者が `unknown` になり得る（作業仕様書 規則 11 の (b)）。黙らせずに見えるようにする。
    internal const string ActorUnconfirmed =
        "操作者が記録されたかを応答から確認できません（リスク管理が旧版の可能性があります。監査台帳で操作者を確認してください）。";

    public async Task<PositionDriftAdoptionResult> AdoptAsync(
        string symbol, Market market, string reason, string onBehalfOf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        try
        {
            // FR-11, #871, IADR-0240 決定11, IADR-0383: 本文に**代理される利用者**（onBehalfOf）を載せる。リスク管理は
            // これを**信頼するクライアントのトークンに限って**操作者として採る（利用者トークン直叩きでは無視される）。
            // **数量は載せない**（目標は最新の観測が決める）。市場は数値で送る（リスク管理の JSON 設定＝web 既定）。
            using var response = await httpClient
                .PostAsJsonAsync(
                    "/risk-controls/position-drift/adopt", new AdoptRequest(symbol, market, reason, onBehalfOf), cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var view = await response.Content
                    .ReadFromJsonAsync<AdoptionView>(cancellationToken)
                    .ConfigureAwait(false);

                if (view is null)
                {
                    logger.LogWarning("乖離の取り込みの応答を解釈できませんでした。");
                    return new PositionDriftAdoptionResult(
                        false, false, "乖離の取り込みの応答を解釈できませんでした（台帳が変わったかは監査台帳で確認してください）");
                }

                return new PositionDriftAdoptionResult(true, true, FormatAdopted(view));
            }

            // 422＝受理不能（観測が無い・古い・乖離が無い・未報告・数量の増加など。台帳は動いていない）。
            // リスク管理は明確に応答した（Succeeded=true・Adopted=false）。理由の文言をそのまま返す。
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                return new PositionDriftAdoptionResult(true, false, $"{NotAdopted}: {error}");
            }

            // 400＝要求の不備（理由の欠如・代理される利用者の値域外・操作者を特定できない）。直し方が分かるよう本文を返す。
            // 失敗として扱う点は変えない（Succeeded=false。失敗を成功に見せない）。
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                logger.LogWarning("乖離の取り込みが受理されませんでした（400）。");
                return new PositionDriftAdoptionResult(false, false, $"{NotAdopted}: {error}");
            }

            var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? OwnerHint
                : string.Empty;
            logger.LogWarning("乖離の取り込みに失敗しました（{Status}）。{Hint}", (int)response.StatusCode, hint);
            return new PositionDriftAdoptionResult(
                false, false, $"乖離の取り込みに失敗しました（HTTP {(int)response.StatusCode}）{hint}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("乖離の取り込みがタイムアウトしました。");
            return new PositionDriftAdoptionResult(
                false, false,
                "乖離の取り込みがタイムアウトしました（台帳が変わったかは不明です。監査台帳で確認してください。"
                + "再実行しても二重には取り込まれません）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "乖離の取り込みで例外が発生しました。");
            return new PositionDriftAdoptionResult(false, false, $"乖離の取り込みに失敗しました（{ex.GetType().Name}）");
        }
    }

    // 🔴 **実現損益を記録していないことを必ず書く**（「損益 0 の決済」と読ませない。通知・監査と同じ規律）。
    // 前後の数量と観測を出す——利用者が「何を観測して、何株から何株へ合わせたか」を Discord の応答だけで確かめられる。
    private static string FormatAdopted(AdoptionView view)
    {
        var observed = view.ObservedAt is { } at
            ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z"
            : "不明";
        var actor = string.IsNullOrWhiteSpace(view.Actor)
            ? ActorUnconfirmed
            : $"操作者 {view.Actor} として記録しました。";

        return $"台帳の {view.Symbol}（{MarketLabel(view.Market)}）の建玉を {view.LedgerQuantityBefore} → {view.LedgerQuantityAfter} へ合わせました"
            + $"（ブローカーの観測 {view.BrokerQuantity}・観測 {observed}）。"
            + actor
            + "システム外の売買の約定価格は分からないため、**実現損益は記録していません**。"
            + "当該銘柄にブローカー側の保護注文（逆指値）が残っていないか、証券会社のアプリで確認してください。";
    }

    internal static string MarketLabel(Market? market) => market switch
    {
        Market.Japan => "日本市場",
        Market.UnitedStates => "米国市場",
        _ => "市場不明",
    };

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorView>(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body?.Error) ? "理由は返されませんでした。" : body!.Error;
        }
        catch (Exception)
        {
            // 本文が読めなくても「取り込んでいない」ことは伝える（黙って成功に見せない）。
            return "理由は返されませんでした。";
        }
    }

    // 要求本文（web JSON = camelCase・列挙は数値）。OnBehalfOf はリスク管理の要求型の末尾（#871）。
    private sealed record AdoptRequest(string Symbol, Market Market, string Reason, string OnBehalfOf);

    // リスク管理の応答の射影 DTO（送り手の本物の型は PositionDriftAdoptionResponse。T-10-989 の契約テストが結ぶ）。
    // 数量・市場は受け手で null 許容にしない——欠落は 0／既定値へ落ちるが、送り手の改名は契約テストが止める。
    // Actor だけは null 許容（配備順の窓で旧版のリスク管理は返さない。ActorUnconfirmed を出す）。
    internal sealed record AdoptionView(
        Guid AdoptionId,
        string? Symbol,
        Market? Market,
        int LedgerQuantityBefore,
        int LedgerQuantityAfter,
        int BrokerQuantity,
        DateTimeOffset? ObservedAt,
        bool RealizedPnlRecorded,
        string? Actor);

    internal sealed record ErrorView(string? Error);
}
