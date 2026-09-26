using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-14, FR-07, UC-03〜05, ADR-0003, IADR-0240: 報告書サービス（#14）のレビュー・確定エンドポイントを
// 呼ぶだけのアダプタ。通知サービスは報告書の状態を持たない（権威は報告書サービス側）。
// kill switch / pause / 段階ゲート（HttpKillSwitchController ほか）と同型。
//
// 当該エンドポイントは OwnerOnly（trading-owner）であり、s2s トークン（trading-service）では 403 になる。本アダプタが
// 使う名前付き HttpClient には Bot 専用の owner マップ機密クライアントのトークンを付与する。資格情報が未設定なら
// トークン無し＝401 となり操作は失敗する（安全側）。
//
// 失敗時の方針: 握り潰さない。**「確定したつもりで確定していない」状態を作らない**（kill switch と同じ）。
public sealed class HttpReportReviewController(
    HttpClient httpClient,
    ILogger<HttpReportReviewController> logger)
    : IReportReviewController
{
    public async Task<ReportReviewResult> GetReviewAsync(
        string periodKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        try
        {
            using var response = await httpClient
                .GetAsync($"/reports/{periodKey}/review", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new ReportReviewResult(false, 0, FailureMessage("レビュー局面の照会", response.StatusCode));

            var view = await response.Content
                .ReadFromJsonAsync<ReviewView>(cancellationToken)
                .ConfigureAwait(false);

            // 2xx だが本文を解釈できない場合、版番号を騙らず失敗として返す（誤った版で確定させない）。
            if (view is null)
            {
                logger.LogWarning("レビュー局面の応答を解釈できませんでした（PeriodKey={PeriodKey}）。", periodKey);
                return new ReportReviewResult(false, 0, "レビュー局面の応答を解釈できませんでした");
            }

            // FR-07, FR-14, #840, IADR-0352 決定 5: 入力が未供給のまま生成された報告書なら、**確定の前に**それを見せる。
            // 本メッセージは `/report show` と、版番号なしの `/report approve`（確認ボタンの前段）の両方に出る。
            var message = $"報告書 {periodKey}: 版 {view.Version}";
            if (ReportUnsuppliedNotice.Format(view.UnsuppliedInputs) is { } notice)
            {
                logger.LogWarning(
                    "報告書 {PeriodKey}（版 {Version}）は入力が未供給のまま生成されています（{Count} 件）。",
                    periodKey, view.Version, view.UnsuppliedInputs?.Count ?? 0);
                message += $"\n{notice}";
            }

            return new ReportReviewResult(true, view.Version, message);
        }
        catch (Exception ex) when (Handled(ex, cancellationToken))
        {
            return new ReportReviewResult(false, 0, ExceptionMessage("レビュー局面の照会", ex, cancellationToken));
        }
    }

    // FR-07, ADR-0003, 詳細設計07 §二重実行防止: 版番号付き冪等の確定。
    // 409（版不一致・確定済み変更）は Succeeded=true / Confirmed=false ではなく、**呼び出しの失敗として扱わない**
    // ——サーバは正しく応答している。受理されなかったことを Confirmed=false で表す。
    //
    // FR-09, UC-03, IADR-0240 決定11, #774: 本文に**代理される利用者**（onBehalfOf）を載せる。報告書サービスは
    // owner マップ機密クライアントのトークン（azp）に限ってこの値を確定者として採り、認可の主体（クライアント）と
    // 併せて ReportConfirmed に残す。空の操作者では呼ばない（確定者を記録できない確定をさせない）。
    public async Task<ReportConfirmResult> ConfirmAsync(
        string periodKey, int expectedVersion, string onBehalfOf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync(
                    $"/reports/{periodKey}/confirm", new ConfirmRequest(expectedVersion, onBehalfOf), cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                logger.LogWarning(
                    "報告書の確定が版不一致で拒否されました（PeriodKey={PeriodKey}・版={Version}）。",
                    periodKey, expectedVersion);
                return new ReportConfirmResult(
                    true, false, "版番号が一致しません。最新のドラフトを確認してください。");
            }

            if (!response.IsSuccessStatusCode)
                return new ReportConfirmResult(false, false, FailureMessage("報告書の確定", response.StatusCode));

            // FR-07, FR-13, #1025, IADR-0433 決定 7（PR #1027 の監査 H1）: 確定済みの報告書への再確定は、どの版を送っても冪等な 200
            // （IADR-0024 の「再確定は版非依存で冪等」は変えない）。**200 を「版 N を確定した」と読まない。** 応答の transitioned と
            // version（確定後の版）で見分ける: 遷移した／この版で確定済み（version == expectedVersion + 1）だけを確定として扱い、
            // 別の版で確定済みなら「確定していない」と返す（窓口の版番号ガードは再起動で空になるため、古いボタンがここへ届く）。
            ConfirmView? view = null;
            try
            {
                view = await response.Content.ReadFromJsonAsync<ConfirmView>(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                view = null;
            }

            if (view?.Transitioned is not { } transitioned || view.Version is not { } confirmedVersion)
            {
                // 項目を返さない旧版の報告書サービス（配備順の窓）。従来どおりの扱い（入れ替えの適用は報告書サービスの照会が確定の版を検査する）。
                return new ReportConfirmResult(true, true, $"報告書 {periodKey}（版 {expectedVersion}）を確定しました。");
            }

            if (transitioned)
                return new ReportConfirmResult(true, true, $"報告書 {periodKey}（版 {expectedVersion}）を確定しました。");

            if (confirmedVersion == expectedVersion + 1)
                return new ReportConfirmResult(true, true, $"報告書 {periodKey}（版 {expectedVersion}）は確定済みです（この版で確定されています）。");

            logger.LogWarning(
                "報告書は別の版で確定済みです（PeriodKey={PeriodKey}・要求の版={Version}・確定後の版={ConfirmedVersion}）。",
                periodKey, expectedVersion, confirmedVersion);
            return new ReportConfirmResult(
                true, false,
                $"報告書 {periodKey} は既に別の版で確定済みです（確定後の版 {confirmedVersion}）。版 {expectedVersion} は確定していません。");
        }
        catch (Exception ex) when (Handled(ex, cancellationToken))
        {
            return new ReportConfirmResult(false, false, ExceptionMessage("報告書の確定", ex, cancellationToken));
        }
    }

    public async Task<ReportReviewResult> RequestChangesAsync(
        string periodKey, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync(
                    $"/reports/{periodKey}/request-changes",
                    new ReviewCommandRequest(expectedVersion),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new ReportReviewResult(false, 0, FailureMessage("報告書の差し戻し", response.StatusCode));

            var view = await response.Content
                .ReadFromJsonAsync<ReviewView>(cancellationToken)
                .ConfigureAwait(false);

            return view is null
                ? new ReportReviewResult(false, 0, "報告書の差し戻しの応答を解釈できませんでした")
                : new ReportReviewResult(
                    true, view.Version, $"報告書 {periodKey}（版 {view.Version}）を差し戻しました。");
        }
        catch (Exception ex) when (Handled(ex, cancellationToken))
        {
            return new ReportReviewResult(false, 0, ExceptionMessage("報告書の差し戻し", ex, cancellationToken));
        }
    }

    // FR-14, UC-03〜05, #834: 会話キーの一覧（新しい順）。入力補完の候補にだけ使う。
    //
    // **fail-safe**: 失敗はすべて空で返す（契約は IReportReviewController）。呼び出し側のキャンセルだけは
    // 伝播させる（他メソッドと同じ `Handled`）。
    //
    // 射影するのは `periodKey` と並び替えに使う `periodStart` だけである。**本文・要約は読まない**
    //（IADR-0240 決定4）。**状態 enum も読まない**（同 決定5。数値/文字列いずれの JSON 表現にも結合しない）
    // ——一覧に載る報告書はレビュー待ちも確定済みも等しくレビュー操作の対象であり、絞り込みに状態は要らない。
    //
    // `periodStart` は**JSON の値のまま（JsonElement）受け**、文字列で日付として解釈できるときだけ並び替えに使う。
    // 数値・欠落・解釈不能の 1 件は末尾へ回し（会話キーは残す）、同順位は会話キーの降順で並ぶ（#843 項目3）。
    // 以前は `string?` で受けていたため、数値で来た 1 件の逆シリアル化失敗が**一覧ごと空**にしていた（実測）。
    //
    // #843 項目1, IADR-0418: 読むのは報告書サービスの**軽い一覧**（`GET /reports/period-keys`＝会話キーと開始日だけ）で
    // ある。以前は本文を含む全件（`GET /reports`）を読み捨てており、**補完は打鍵ごとに発火する**ため件数とともに悪化した。
    // **新しいルートが 404 のときだけ**従来の `GET /reports` へ退避する——配備順の窓（通知サービスだけが新しい）で
    // 補完が黙って死なないため。500・例外・タイムアウトでは退避しない（障害中の報告書サービスへより重い全件照会を
    // 重ねない）。応答の受け口（ReportListItem）は両ルートで共用する（どちらも periodKey / periodStart を持つ）。
    // 遅い帯は補完の時間予算（ReportCommandHandler.SuggestionBudget・#843 項目2）が「候補なし」へ倒す（退避も予算の内側）。
    public async Task<IReadOnlyList<string>> ListPeriodKeysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await GetPeriodKeyListAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("報告書一覧の照会に失敗しました（{Status}）。補完の候補なしで続行します。",
                    (int)response.StatusCode);
                return [];
            }

            var reports = await response.Content
                .ReadFromJsonAsync<List<ReportListItem>>(cancellationToken)
                .ConfigureAwait(false);

            if (reports is null)
            {
                logger.LogWarning("報告書一覧の応答を解釈できませんでした。補完の候補なしで続行します。");
                return [];
            }

            return [.. reports
                .Where(r => !string.IsNullOrWhiteSpace(r.PeriodKey))
                .OrderByDescending(r => StartOf(r.PeriodStart))
                .ThenByDescending(r => r.PeriodKey, StringComparer.Ordinal)
                .Select(r => r.PeriodKey!)];
        }
        catch (Exception ex) when (Handled(ex, cancellationToken))
        {
            logger.LogWarning(ex, "報告書一覧の照会で例外が発生しました。補完の候補なしで続行します。");
            return [];
        }
    }

    // #843 項目1, IADR-0418: 補完が読む軽い一覧と、配備順の窓でだけ使う従来の一覧。
    private const string PeriodKeysPath = "/reports/period-keys";
    private const string LegacyListPath = "/reports";

    // 軽い一覧を読み、**404 のときだけ**従来の一覧へ退避する（上のコメント・IADR-0418 決定3）。
    private async Task<HttpResponseMessage> GetPeriodKeyListAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync(PeriodKeysPath, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
            return response;

        response.Dispose();
        logger.LogInformation(
            "報告書サービスに {Path} がありません（配備が揃う前の旧版）。従来の一覧 {Fallback} で続行します。",
            PeriodKeysPath, LegacyListPath);
        return await httpClient.GetAsync(LegacyListPath, cancellationToken).ConfigureAwait(false);
    }

    private string FailureMessage(string operation, HttpStatusCode status)
    {
        // FR-14, #834: 404 は「呼び出しの失敗」ではなく**その会話キーの報告書が無い**ことを意味する。
        // 状態番号だけを返すと、利用者は何が悪いのか・正しい形が何かを知る術が無い（日付だけを入れて
        // 4 回続けて 404 になった実測がある）。**正しい形の例を添える。**
        if (status == HttpStatusCode.NotFound)
        {
            logger.LogWarning("{Operation}の対象が見つかりませんでした（404）。", operation);
            return "その会話キーの報告書が見つかりません（例: `daily-2026-09-18`）";
        }

        var hint = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）"
            : string.Empty;
        logger.LogWarning("{Operation}に失敗しました（{Status}）。{Hint}", operation, (int)status, hint);
        return $"{operation}に失敗しました（HTTP {(int)status}）{hint}";
    }

    // 対象期間の開始日。文字列で日付として解釈できる値だけを採り、それ以外（数値・null・欠落・解釈不能）は
    // 最小値へ倒す（その 1 件を末尾へ回すだけで、一覧ごと落とさない。#843 項目3）。
    private static DateTime StartOf(JsonElement? periodStart) =>
        periodStart is { ValueKind: JsonValueKind.String } element
        && DateTime.TryParse(
            element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.MinValue;

    private string ExceptionMessage(string operation, Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Operation}がタイムアウトしました。", operation);
            return $"{operation}がタイムアウトしました（結果は不明です）";
        }

        logger.LogWarning(ex, "{Operation}で例外が発生しました。", operation);
        return $"{operation}に失敗しました（{ex.GetType().Name}）";
    }

    // 呼び出し側のキャンセルだけは伝播させる（タイムアウトは HttpClient 由来の OperationCanceledException）。
    private static bool Handled(Exception ex, CancellationToken cancellationToken) =>
        ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    // 報告書サービス側 ReportReview の必要部分のみを受ける射影。
    // **State（enum）は受けない**——数値/文字列いずれの JSON 表現にも結合しないため（IADR-0240 決定5）。
    //
    // #840, IADR-0352 決定 5: UnsuppliedInputs は報告書サービスの**コード定数の表示名**（本文・要約ではない＝
    // IADR-0240 決定4 に反しない）。欠落（旧版の報告書サービス）は null＝警告なし＝従来どおり。
    private sealed record ReviewView(int Version, IReadOnlyList<string?>? UnsuppliedInputs = null);

    // 報告書サービス側 ConfirmReportRequest / ReviewCommandRequest と同形（版番号付き）。
    // OnBehalfOf は #774 で末尾に追加（旧版の報告書サービスは未知のプロパティとして読み飛ばす）。
    private sealed record ConfirmRequest(int ExpectedVersion, string OnBehalfOf);

    // 報告書サービス側 ConfirmReportResponse の必要部分（#1025 で足された 2 項目）。欠落は null＝旧版。
    private sealed record ConfirmView(bool? Transitioned, int? Version);

    private sealed record ReviewCommandRequest(int ExpectedVersion);

    // #834: 一覧応答の必要部分だけを受ける射影。**本文（body）・要約（policySummary）・状態（state）は
    // 受けない**（IADR-0240 決定4/5）。軽い一覧（#843 項目1）はこの 2 つしか返さず、従来の一覧（退避先）は
    // 余分な項目を読み飛ばす。
    // #843 項目3: periodStart は並び替えにしか使わないため **JSON の表現を問わず受ける**（JsonElement）。`string?` で
    // 受けると文字列表現に結合し、数値で来た 1 件が逆シリアル化ごと一覧を空にする。periodKey は候補そのもので
    // あり文字列でなければ契約違反のため `string?` のまま受ける（違反時は一覧ごと空＝従来の fail-safe）。
    private sealed record ReportListItem(string? PeriodKey, JsonElement? PeriodStart);
}
