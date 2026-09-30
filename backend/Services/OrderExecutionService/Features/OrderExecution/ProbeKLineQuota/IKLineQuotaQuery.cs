namespace OrderExecutionService.Features.OrderExecution.ProbeKLineQuota;

// FR-02, FR-15, ADR-0048 決定 3, ADR-0023 決定 5, #1117, IADR-0464: 日足 K 線の取得枠と復権の扱いを実機で確かめる
// 1 回実行の検証口（KLineQuotaProbeCommand）だけが使う、moomoo の相場（Qot）の**読み取り専用**ポート。
//
// 🔴 **メソッドは 2 つだけに保つ**（枠の照会・日足の取得）。検証口はこのポートしか受け取らないため、ここに発注・訂正・取消を
// 足さない限り、検証口から書き込み系の API へ届く経路が型の上に存在しない（試験で固定）。
// 🔴 **実装は 1 呼び出しにつき OpenD へ 1 回だけ撃つ。** 再試行・ページングのループを持たない（取得枠と頻度制限を消費しないため）。
// 非成功（retType ≠ 0）は例外にせず、retType / retMsg を結果として返す —— 検証の目的は応答そのものを見ることである。
public interface IKLineQuotaQuery
{
    /// <summary>履歴 K 線の取得枠を照会する（QotRequestHistoryKLQuota）。includeDetail なら銘柄ごとの要求時刻の一覧も求める。</summary>
    Task<KLineQuotaReading> QueryQuotaAsync(bool includeDetail, CancellationToken cancellationToken = default);

    /// <summary>米国株の日足 K 線を 1 リクエストだけ取得する（QotRequestHistoryKL。続きの鍵は追わない）。</summary>
    Task<DailyKLineReading> RequestDailyKLinesAsync(DailyKLineProbeRequest request, CancellationToken cancellationToken = default);
}

// 復権（株式分割・配当による過去の足の調整）の区分。SDK の RehabType（None 0 / Forward 1 / Backward 2）に 1 対 1 で写す。
public enum KLineRehab
{
    /// <summary>無復権（RehabType_None）。</summary>
    None,

    /// <summary>前復権（RehabType_Forward）。バックテストの履歴源が使う区分（ADR-0023 決定 5）。</summary>
    Forward,

    /// <summary>後復権（RehabType_Backward）。</summary>
    Backward,
}

// 日足の 1 リクエスト。期間は両端を含む。
public sealed record DailyKLineProbeRequest(string Symbol, KLineRehab Rehab, DateOnly From, DateOnly To);

// 枠の照会の結果。値は応答のまま（欄が無ければ null）。
public sealed record KLineQuotaReading(
    int RetType,
    string RetMsg,
    int? UsedQuota,
    int? RemainQuota,
    IReadOnlyList<KLineQuotaDetail> Details)
{
    public bool Succeeded => RetType == 0;
}

// 枠の詳細の 1 行（DetailItem）。Security は `US.AAPL` の形。
public sealed record KLineQuotaDetail(string? Security, string? Name, string? RequestTime, long? RequestTimeStamp);

// 日足の取得の結果。HasMore は応答が続きの鍵を返したか（検証口は追わない）。
public sealed record DailyKLineReading(int RetType, string RetMsg, IReadOnlyList<ProbeKLine> KLines, bool HasMore)
{
    public bool Succeeded => RetType == 0;
}

// 応答の日足 1 本。値は応答のまま（欄が無ければ null）。Time は OpenD の文字列そのまま。
public sealed record ProbeKLine(
    string? Time,
    bool IsBlank,
    double? Open,
    double? High,
    double? Low,
    double? Close,
    long? Volume,
    double? Turnover);
