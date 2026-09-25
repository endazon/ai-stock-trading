using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-05, FR-10, #292, IADR-0119: 保有建玉をリスク管理（#12・#63 台帳）の
// GET /risk-controls/open-positions（既存・OwnerOrService）から同期照会する。新規エンドポイントは作らない。
// ［2026-09-24 追記 / #934・IADR-0390］未約定の新規建て注文（判断の入力の第 3 の状態）のため、リスク管理に
// GET /risk-controls/working-entry-orders（OwnerOrService・読み取り専用）を足し、本クラスが読む。上の「作らない」は
// 保有建玉の照会についての記述である。
//
// fail-safe の要: 非 2xx・例外・タイムアウト・不正応答は **null（不明）**。空配列は **0（保有なし）**。
// 市場監視の HttpPositionStore は失敗を空列へ倒す（損切り検知対象なし＝そちらの安全側）が、ここで同じことをすると
// 「保有していない」と誤断定して裸の新規売りを通してしまうため、区別を厳格に保つ。
public sealed class HttpHeldPositionProvider(
    HttpClient httpClient,
    ILogger<HttpHeldPositionProvider> logger)
    : IHeldPositionProvider
{
    // #865, IADR-0358: 実結線。RiskManagement:BaseUrl が設定されたときだけ生成されるため常に true。
    // 以後の「不明」は**照会したが答えが得られなかった**ことを意味し、判断側は新規建てを見送る。
    public bool IsEnabled => true;

    public async Task<int?> GetSignedQuantityAsync(
        string symbol, Market market, CancellationToken cancellationToken = default) =>
        (await GetPositionAsync(symbol, market, cancellationToken).ConfigureAwait(false))?.SignedQuantity;

    // FR-04, FR-10, ADR-0003, #854, IADR-0351 決定1: 数量に加えて平均取得単価・記録上の損切りラインも読む
    // （同じ応答に既に載っている。従来は数量だけを読んでいた）。失敗＝null（不明）／一覧に無い＝保有なしの区別は不変。
    public async Task<HeldPosition?> GetPositionAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/open-positions", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("保有建玉の照会に失敗（{Status}）。不明として扱います。", (int)response.StatusCode);
                return null;
            }

            var positions = await response.Content
                .ReadFromJsonAsync<List<OpenPositionDto>>(cancellationToken)
                .ConfigureAwait(false);
            if (positions is null)
            {
                logger.LogWarning("保有建玉の応答を解釈できません。不明として扱います。");
                return null;
            }

            return InterpretPositions(positions, symbol, market, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("保有建玉の照会がタイムアウト。不明として扱います。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "保有建玉の照会で例外。不明として扱います。");
            return null;
        }
    }

    // NFR, IADR-0427 決定 5, #997: 応答の**解釈**（行の検証と一致行の畳み込み）。輸送に依らず 1 つであり、
    // gRPC（GrpcHeldPositionProvider）も proto を同じ nullable の行へ写してからここを呼ぶ —— 行の検証
    // （#943・#854・#865 の規則）を 2 箇所に書かない。**中身は切り出す前と 1 文字も変えていない。**
    internal static HeldPosition? InterpretPositions(
        IReadOnlyList<OpenPositionDto> positions, string symbol, Market market, ILogger logger)
    {
        // 🔴 #943, IADR-0390（PR #940 監査の同型）: 銘柄・市場を持たない行は「一致しない」と読まない。
        // OpenPositionView の項目名が変わると、ここは既定値（null）で逆シリアル化され、一致する行が 0 件＝「保有なし」へ
        // 黙って倒れる —— 建玉を持っているのにプロンプトは「保有: なし」と書き、新規建ての見送り（IADR-0358）も効かない。
        // その行が判断対象の銘柄かどうか判らないので、応答全体を解釈できない（不明）とする。
        if (positions.Any(p => p is null || string.IsNullOrEmpty(p.Symbol) || p.Market is null))
        {
            logger.LogWarning("保有建玉の応答に銘柄・市場の無い行があります。不明として扱います。");
            return null;
        }

        var matched = positions
            .Where(p => string.Equals(p.Symbol, symbol, StringComparison.Ordinal) && p.Market == market)
            .ToList();

        // 🔴 方向・数量の欠けた一致行、数量が正でない一致行は「保有なし」と読まない —— 台帳の射影は数量 0 の建玉を含めず
        // 数量は常に正（向きは Side）という契約であり、それを破る応答（項目名の変更で既定値に落ちた場合を含む）は解釈できない。
        if (matched.Any(p => p.Side is null || p.Quantity is not > 0))
        {
            logger.LogWarning("保有建玉の応答に方向・数量の欠けた、または数量が正でない行があります。不明として扱います。");
            return null;
        }

        // 一覧に無ければ保有なし（0）。台帳の射影は数量 0 の建玉を含めない（PortfolioProjection）。
        // 射影は (銘柄, 市場) ごとに 1 行のため、取得単価・損切りラインは一致した行の値をそのまま採る。
        var signed = 0;
        decimal? entryPrice = null;
        decimal? stopLossPrice = null;
        foreach (var p in matched)
        {
            signed += p.Side == TradeSide.Buy ? p.Quantity!.Value : -p.Quantity!.Value;
            entryPrice = p.EntryPrice;
            stopLossPrice = p.StopLossPrice;
        }

        if (signed == 0)
            return HeldPosition.None;

        // 🔴 応答に無い・正でない価格は「不明」（null）にする。0 で埋めると含み損益と損切り判定が偽の値になる。
        return new HeldPosition(
            signed,
            entryPrice is > 0m ? entryPrice : null,
            stopLossPrice is > 0m ? stopLossPrice : null);
    }

    // FR-04, FR-10, ADR-0003, #934, IADR-0390 決定2: 当日の未約定の新規建て注文を GET /risk-controls/working-entry-orders から読む
    // （統制 IADR-0346 と同じ定義。約定済みの保有＝/open-positions とは別の口・別の型）。
    // 🔴 fail-safe の区別は保有と同じ: 非 2xx・例外・タイムアウト・不正応答は **null（不明）**、一致する行が無ければ **None（無い）**。
    // 不明を None へ倒すと、指値が板に残っているのに判断は「保有なし」を前提に同じ銘柄を重ねて買う（#934 の実測）。
    public async Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/working-entry-orders", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("未約定の新規建て注文の照会に失敗（{Status}）。不明として扱います。", (int)response.StatusCode);
                return null;
            }

            var orders = await response.Content
                .ReadFromJsonAsync<List<WorkingEntryOrderDto>>(cancellationToken)
                .ConfigureAwait(false);
            if (orders is null)
            {
                logger.LogWarning("未約定の新規建て注文の応答を解釈できません。不明として扱います。");
                return null;
            }

            return InterpretWorkingEntryOrders(orders, symbol, market, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("未約定の新規建て注文の照会がタイムアウト。不明として扱います。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "未約定の新規建て注文の照会で例外。不明として扱います。");
            return null;
        }
    }

    // NFR, IADR-0427 決定 5, #997: 応答の**解釈**。輸送に依らず 1 つ（GrpcHeldPositionProvider も同じ行へ写してから呼ぶ）。
    // **中身は切り出す前と 1 文字も変えていない。**
    internal static WorkingEntryOrders? InterpretWorkingEntryOrders(
        IReadOnlyList<WorkingEntryOrderDto> orders, string symbol, Market market, ILogger logger)
    {
        // 🔴 PR #940 監査（契約の fail-open）: 銘柄・市場を持たない行は「一致しない」と読まない。
        // WorkingEntryOrderView の項目名が変わると、ここは既定値（null）で逆シリアル化され、一致する行が 0 件＝「無い」へ
        // 黙って倒れる —— 板に指値が残っているのにプロンプトは「保有: なし」と書く（#934 の実測そのもの）。
        // その行が判断対象の銘柄かどうか判らないので、応答全体を解釈できない（不明）とする。
        if (orders.Any(o => o is null || string.IsNullOrEmpty(o.Symbol) || o.Market is null))
        {
            logger.LogWarning("未約定の新規建て注文の応答に銘柄・市場の無い行があります。不明として扱います。");
            return null;
        }

        var matched = orders
            .Where(o => string.Equals(o.Symbol, symbol, StringComparison.Ordinal) && o.Market == market)
            .ToList();

        // 🔴 残数量が正でない行・方向／価格／承認時刻の無い行は「無い」と読まない —— リスク管理は残 0 を返さない契約であり、
        // それを破る応答（項目名の変更で既定値に落ちた場合を含む）は解釈できない。
        if (matched.Any(o => o.RemainingQuantity is not > 0 || o.Side is null || o.Price is null || o.ApprovedAt is null))
        {
            logger.LogWarning("未約定の新規建て注文の応答に残数量が正でない、または項目の欠けた行があります。不明として扱います。");
            return null;
        }

        return matched.Count == 0
            ? WorkingEntryOrders.None
            : new WorkingEntryOrders(
                [.. matched.Select(o => new WorkingEntryOrder(
                    o.Side!.Value, o.RemainingQuantity!.Value, o.Price!.Value, o.ApprovedAt!.Value))]);
    }

    // WorkingEntryOrderView（RiskManagement・#934）の必要フィールドのみ。camelCase・列挙は数値で往復する。
    // 🔴 PR #940 監査: 全項目を nullable で受ける —— 非 nullable だと項目の欠落（送り手の改名）が既定値（銘柄 null・
    // 市場 0＝日本・方向 0＝買い・数量 0）に化けて「無い」と区別できない。契約は T-10-744 が本物の型で固定する。
    internal sealed record WorkingEntryOrderDto(
        string? Symbol, Market? Market, TradeSide? Side, int? RemainingQuantity, decimal? Price, DateTimeOffset? ApprovedAt);

    // OpenPositionView（RiskManagement）の必要フィールドのみ。camelCase・列挙は数値で往復する。
    // 価格 2 項目は nullable（項目を持たない応答を 0 と読まない）。
    // 🔴 #943, IADR-0390: 識別・数量の 4 項目も nullable で受ける —— 非 nullable だと項目の欠落（送り手の改名）が既定値
    // （銘柄 null・市場 0＝日本・方向 0＝買い・数量 0）に化けて「保有なし」と区別できない。契約は T-10-800 が本物の型で固定する。
    internal sealed record OpenPositionDto(
        string? Symbol, Market? Market, TradeSide? Side, int? Quantity, decimal? EntryPrice, decimal? StopLossPrice);
}
