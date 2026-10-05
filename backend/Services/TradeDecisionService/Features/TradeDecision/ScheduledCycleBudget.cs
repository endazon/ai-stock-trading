using System.Globalization;

namespace TradeDecisionService.Features.TradeDecision;

// FR-02, NFR-02, #1169, IADR-0490 決定1: 定時サイクル（InformationCollected 1 通＝監視銘柄の全件を順に判断する）の実行時間の上限。
//
// 🔴 なぜ要るのか（#1169 の実測）: ハンドラの実行時間の上限は、明示しなければ Wolverine の既定 60 秒である
// （WolverineOptions.DefaultExecutionTimeout）。LLM が 1 銘柄 7〜10 秒かかるため、監視 6 銘柄が全部 LLM へ回ると
// 60 秒前後になり、サイクルが丸ごと打ち切られて再配送された（1 回目の判断を捨て、LLM を二重に呼び、判断の時刻が 1 分ずれた）。
//
// 上限は**数字を直書きせず、同じ構成から導く**（LLM の timeout・1 判断あたりの LLM 呼び出し回数・監視銘柄数の前提）。
//   1 銘柄の上限 ＝ LLM の timeout × 1 判断あたりの LLM 呼び出し回数 ＋ LLM 以外の照会の見込み（NonLlmAllowancePerSymbol）
//   サイクルの上限 ＝ 監視銘柄数の前提（MaxWatchedSymbols） × 1 銘柄の上限 ＋ サイクルの余裕（CycleMargin）
// 1 銘柄の上限はハンドラが銘柄ごとの締め切りとして**強制する**（超えた銘柄はその銘柄の失敗として分離される）。
// したがって監視銘柄が前提の数以内なら、サイクルの上限に届く前に必ず全銘柄が終わる（導出が上界として成り立つ）。
public sealed record ScheduledCycleBudget
{
    /// <summary>監視銘柄数の前提の既定（<c>TradeCycle:MaxWatchedSymbols</c> 未設定・不正・非正値のとき）。</summary>
    public const int DefaultMaxWatchedSymbols = 10;

    /// <summary>監視銘柄数の前提の構成キー。</summary>
    public const string MaxWatchedSymbolsKey = "TradeCycle:MaxWatchedSymbols";

    /// <summary>
    /// 1 銘柄あたりの LLM 以外の照会（日報の方針・サイジング・建玉・日足・現在値・為替・知識ベース）の見込み。
    /// 名前付きクライアントの上限（5〜8 秒）の和に相当する。超えても銘柄ごとの締め切りが止める（推測の値で上界を崩さない）。
    /// </summary>
    public static readonly TimeSpan NonLlmAllowancePerSymbol = TimeSpan.FromSeconds(30);

    /// <summary>サイクル全体の余裕（監視銘柄の照会〔5 秒〕・ニュースの状態の記録・発行の前後）。</summary>
    public static readonly TimeSpan CycleMargin = TimeSpan.FromSeconds(60);

    private ScheduledCycleBudget(int maxWatchedSymbols, TimeSpan perSymbol, TimeSpan handlerTimeout)
    {
        MaxWatchedSymbols = maxWatchedSymbols;
        PerSymbol = perSymbol;
        HandlerTimeout = handlerTimeout;
    }

    /// <summary>上限を導いた前提の監視銘柄数。実際の監視銘柄がこれを超えると、サイクルが上限に達し得る（ハンドラが警告する）。</summary>
    public int MaxWatchedSymbols { get; }

    /// <summary>1 銘柄の判断の締め切り（ハンドラが銘柄ごとに強制する）。</summary>
    public TimeSpan PerSymbol { get; }

    /// <summary>定時サイクルのハンドラの実行時間の上限（Wolverine の既定 60 秒に頼らない）。</summary>
    public TimeSpan HandlerTimeout { get; }

    /// <summary>Wolverine のハンドラチェーンへ設定する秒数（整数秒。切り上げて上限を縮めない）。</summary>
    public int HandlerTimeoutSeconds => (int)Math.Ceiling(HandlerTimeout.TotalSeconds);

    /// <summary>
    /// 構成値から上限を導く（純関数）。
    /// </summary>
    /// <param name="llmTimeout">LLM ゲートウェイの 1 呼び出しの上限（<c>LlmGateway:TimeoutSeconds</c>）。</param>
    /// <param name="llmCallsPerDecision">1 判断あたりの LLM 呼び出し回数（<see cref="LlmCallsPerDecision"/>）。</param>
    /// <param name="maxWatchedSymbols">監視銘柄数の前提。</param>
    public static ScheduledCycleBudget Derive(TimeSpan llmTimeout, int llmCallsPerDecision, int maxWatchedSymbols) =>
        Derive(llmTimeout, llmCallsPerDecision, maxWatchedSymbols, NonLlmAllowancePerSymbol, CycleMargin);

    /// <summary>
    /// 見込みと余裕も明示して導く（本番は上の既定の見込み・余裕を使う。試験が締め切りを短くするための口でもある）。
    /// </summary>
    public static ScheduledCycleBudget Derive(
        TimeSpan llmTimeout, int llmCallsPerDecision, int maxWatchedSymbols, TimeSpan nonLlmAllowance, TimeSpan cycleMargin)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(llmTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(llmCallsPerDecision, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWatchedSymbols, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nonLlmAllowance, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycleMargin, TimeSpan.Zero);

        var perSymbol = llmTimeout * llmCallsPerDecision + nonLlmAllowance;
        var handlerTimeout = perSymbol * maxWatchedSymbols + cycleMargin;
        return new ScheduledCycleBudget(maxWatchedSymbols, perSymbol, handlerTimeout);
    }

    /// <summary>
    /// 1 判断あたりの LLM 呼び出し回数。一次スクリーニング（有効なら 1 回）＋ 二次本判断（多数決の回数。順に呼ぶ）。
    /// スクリーニングが Hold なら二次は呼ばれないが、上限は最悪の側で数える。
    /// </summary>
    public static int LlmCallsPerDecision(DecisionOrchestrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (options.EnableScreening ? 1 : 0) + options.VoteCount;
    }

    /// <summary>
    /// 起点イベントが有効期間を宣言していない（旧発行側）ときの鮮度の上限。NFR-02（定時サイクル 1 回の所要 10 分以内）に合わせる。
    /// </summary>
    public static readonly TimeSpan DefaultStaleness = TimeSpan.FromMinutes(10);

    /// <summary>
    /// #1169 監査 🟡1, IADR-0490 決定3: 定時サイクルの起点の鮮度の上限。発行側が宣言した有効期間（<c>InformationCollected.NewsStatusValidFor</c>。
    /// 巡回間隔の 2 倍・下限 5 分）を、ニュースの状態と同じ範囲へクランプして使う（同じ巡回の同じ事実の鮮度であるため）。
    /// 宣言が無ければ <see cref="DefaultStaleness"/>。
    /// </summary>
    public static TimeSpan StalenessBound(TimeSpan? declaredValidFor) =>
        declaredValidFor is { } validFor ? NewsCollectionStatusStore.Clamp(validFor) : DefaultStaleness;

    /// <summary>
    /// 起点が鮮度の上限を**超えて**古いか（ちょうどは古くない）。古い起点は次の巡回の起点が既に届いているか届く頃であり、
    /// 判断しても NFR-02 を満たさず、滞留を伸ばすだけである。
    /// </summary>
    public static bool IsStale(DateTimeOffset now, DateTimeOffset collectedAt, TimeSpan? declaredValidFor) =>
        now - collectedAt > StalenessBound(declaredValidFor);

    /// <summary>
    /// 監視銘柄数の前提を構成から読む。未設定・不正・非正値は既定（<see cref="DefaultMaxWatchedSymbols"/>）へ倒す
    /// （LLM の timeout の解釈〔未設定・不正・非正値は既定 30 秒〕と同じ作法。0 や負で上限を潰さない）。
    /// </summary>
    public static int ParseMaxWatchedSymbols(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : DefaultMaxWatchedSymbols;
}
