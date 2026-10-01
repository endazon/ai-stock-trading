using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-15, ADR-0033 決定2, #632, IADR-0318: **過去のある時点までの情報だけ**を運ぶ型。
//
// ADR-0033 決定2 は記録について「その時点までに得られていた情報だけを取引判断サービスへ与え、
// ルックアヘッド排除は `BacktestContext.History`（当日 AsOf まで）と同じ規律で行う」と定めた。
//
// 🔴 **規律を運用の注意書きに委ねない。** 日付を持つ入力（日報方針・参照価格・参考情報）は
// **構築時にすべて AsOf で切る**。未来を渡そうとすれば例外になるか除外され、
// 「その時点の情報だけ」という性質が**型の側で保たれる**。
// 供給側（`IAsOfDecisionInputProvider` の実装）が注意深いかどうかに依存しない。

/// <summary>日付つきの価格。AsOf より後の日付は受け付けない（参照価格をスカラで渡さない理由）。</summary>
public readonly record struct DatedPrice(DateOnly Date, decimal Value);

public sealed class AsOfDecisionInput
{
    /// <param name="asOf">判断時点（この日の終値までの情報しか使わない）。</param>
    /// <param name="policy">確定済み日報の方針。<b>AsOf より後の日付は例外</b>（未来の方針で判断させない）。</param>
    /// <param name="price">参照価格（AsOf 時点）。AsOf より後の日付は例外。null は価格文脈なし。</param>
    /// <param name="references">
    /// 参考情報（RAG 相当）。<b>発行時刻が AsOf 以前のものだけを残す。</b>
    /// 発行時刻が不明（null）のものは**除外する** —— 過去のものだと確かめられない以上、
    /// 入れれば未来の情報が紛れ込み得る（本番の縮退規則〔最古扱い〕とは目的が違う）。
    /// </param>
    /// <param name="rateToBase">
    /// 基準通貨への換算レート（FR-10 / IADR-0107）。基準通貨の市場では定義から 1 である。
    /// 非基準通貨の市場では**その時点のレート**を供給側が渡す（渡せなければ記録は本番と単位が食い違う）。
    /// </param>
    /// <param name="notReconstructable">
    /// FR-15, ADR-0036 決定1, #749, IADR-0387: **当時の値を再構成できなかった入力の種別**（供給側の申告）。
    /// <para>
    /// 🔴 **「渡さなかった」ことと「再構成できなかった」ことを分けるための引数である。** 日報方針は必須引数、
    /// 換算レートは既定 1 であり、**型の側からは痩せを観測できない** —— 供給側が明示しない限り、記録は
    /// 「当時の方針で判断した」「基準通貨だから 1 だった」と読まれる。ADR-0036 決定1 はその読み違いを禁じている。
    /// </para>
    /// <para>
    /// 参考情報（(b)）については**型の側でも倒れる** —— 発行時刻が不明で落としたものがあれば、
    /// その日の参考情報は「無かった」ではなく「再構成できなかった」である（下の導出を参照）。
    /// </para>
    /// </param>
    /// <param name="watchlist">
    /// FR-04, ADR-0044 決定 3, #1034, IADR-0440 決定 7: **判断時点の監視銘柄**（市場監視の変更履歴から再構成した一覧。(e)）。
    /// <para>
    /// 🔴 **null は「再構成できなかった」であり、(e) を <see cref="Stage0AsOfInputAvailability.NotReconstructable"/> と申告する**
    /// （プロンプトの監視銘柄の節は「不明」になり、この判断は Stage 0 の合否から外れる）。空の一覧は「当時 0 件だった」という事実である。
    /// 再構成の供給口（#1049・IADR-0442）は `WatchlistAsOfDecisionInputProvider` が <see cref="WithWatchlist"/> で埋める。
    /// 🔴 **記録の対象銘柄の集合をここへ渡さない**（ADR-0044 決定 3。当時の方針が挙げる銘柄と食い違うことがある）。
    /// </para>
    /// </param>
    /// <param name="watchlistUnavailableReason">
    /// FR-04, ADR-0046 決定 1, #1049, IADR-0442 決定 4: 一覧が null のとき、(e) の申告へ載せる<b>再構成できなかった理由</b>
    /// （供給口が返した理由。例: SeededAt より前）。null・空なら既定の文言。一覧があるときは使わない。
    /// </param>
    /// <param name="previousClose">
    /// FR-02, FR-04, ADR-0033 決定2, #1035, IADR-0451: <b>判断時点より前の最後の終値</b>（日足。前日比の基準）。
    /// <b>日付が AsOf 以降なら例外</b>（当日以降の終値は前日終値ではない＝未来の値で判断させない）。null は「不明」
    /// （プロンプトの前日比は「不明」になる）。🔴 **当日の始値・日中高安は渡さない** —— 本番の定時の判断は場中に走り、
    /// 日足の当日の値（その日の全体）は判断時点では得られない情報を含むため、Stage 0 の当日の変化率は常に「不明」とする。
    /// </param>
    /// <param name="volume">
    /// FR-04, FR-15, ADR-0048 決定 2, #1139, IADR-0479 決定 2: <b>判断時点の前営業日までの確定足から計算した出来高と 20 日平均比</b>
    /// （本番と同じ純関数 <see cref="DailyVolumeContext.From"/> の値）。<b>前営業日の日付が AsOf 以降なら例外</b>（当日以降の足で判断させない）。
    /// 🔴 **null は「判断の出来高が無効」**であり、プロンプトは従来の「出来高: 未提供」の行のまま（本番の無効の構成と同じ）。
    /// 取得できない日は null ではなく <see cref="DailyVolumeContext.Unavailable"/>（本番と同じ「未提供」の別の文）。
    /// </param>
    public AsOfDecisionInput(
        DateOnly asOf,
        DailyPolicy policy,
        SizingContext sizing,
        DatedPrice? price = null,
        IEnumerable<RetrievedContext>? references = null,
        decimal rateToBase = 1m,
        IEnumerable<Stage0AsOfInputKind>? notReconstructable = null,
        IReadOnlyList<WatchedSymbol>? watchlist = null,
        string? watchlistUnavailableReason = null,
        DatedPrice? previousClose = null,
        DailyVolumeContext? volume = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sizing);

        if (policy.Date > asOf)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy), policy.Date, $"日報の方針が判断時点より後である（AsOf={asOf}）。未来の方針で判断させない。");
        }

        if (price is { } p && p.Date > asOf)
        {
            throw new ArgumentOutOfRangeException(
                nameof(price), p.Date, $"参照価格が判断時点より後である（AsOf={asOf}）。未来の価格で判断させない。");
        }

        if (previousClose is { } pc && pc.Date >= asOf)
        {
            throw new ArgumentOutOfRangeException(
                nameof(previousClose), pc.Date, $"前日終値が判断時点以降である（AsOf={asOf}）。当日以降の終値を前日比の基準にしない。");
        }

        if (volume is { PreviousDay: { } volumeDay } && volumeDay >= asOf)
        {
            throw new ArgumentOutOfRangeException(
                nameof(volume), volumeDay, $"出来高の前営業日が判断時点以降である（AsOf={asOf}）。当日以降の足で判断させない。");
        }

        AsOf = asOf;
        Policy = policy;
        Volume = volume;
        // #1035, IADR-0451: 前日比の基準だけを持つ。当日の始値・日中高安は不明（上の previousClose の説明）。
        Intraday = IntradayPriceContext.Of(previousClose?.Value, open: null, high: null, low: null);
        _previousClose = previousClose;
        Sizing = sizing;
        ReferencePrice = price?.Value;
        RateToBase = rateToBase;

        var kept = new List<RetrievedContext>();
        var droppedFuture = 0;
        var droppedUndated = 0;
        foreach (var reference in references ?? [])
        {
            if (reference.PublishedAt is not { } publishedAt)
            {
                droppedUndated++;
                continue;
            }

            if (DateOnly.FromDateTime(publishedAt.UtcDateTime) > asOf)
            {
                droppedFuture++;
                continue;
            }

            kept.Add(reference);
        }

        References = kept;
        DroppedFutureReferenceCount = droppedFuture;
        DroppedUndatedReferenceCount = droppedUndated;
        Watchlist = watchlist;
        AsOfInputs = DeriveAvailability(
            notReconstructable, kept.Count, droppedUndated, watchlist is not null, watchlistUnavailableReason);

        // #1049, IADR-0442 決定 4: WithWatchlist で同じ入力を組み直すために、構築時の引数を持つ（切る前の参考情報を含む）。
        _price = price;
        _references = references is null ? [] : [.. references];
        _notReconstructable = notReconstructable is null ? [] : [.. notReconstructable];
        _watchlistUnavailableReason = watchlistUnavailableReason;
    }

    private readonly DatedPrice? _price;
    private readonly DatedPrice? _previousClose;
    private readonly IReadOnlyList<RetrievedContext> _references;
    private readonly IReadOnlyList<Stage0AsOfInputKind> _notReconstructable;
    private readonly string? _watchlistUnavailableReason;

    /// <summary>
    /// FR-04, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442 決定 4: 当時の監視銘柄（(e)）だけを差し替えた入力を返す。
    /// 他の入力（方針・価格・参考情報・換算レート・供給側の申告）は同じ規律で組み直す（as-of の切り方は変わらない）。
    /// <paramref name="watchlist"/> が null なら (e) は再構成できないと申告し、<paramref name="unavailableReason"/> を理由に載せる。
    /// </summary>
    public AsOfDecisionInput WithWatchlist(IReadOnlyList<WatchedSymbol>? watchlist, string? unavailableReason) =>
        new(AsOf, Policy, Sizing, _price, _references, RateToBase, _notReconstructable, watchlist, unavailableReason, _previousClose, Volume);

    /// <summary>
    /// FR-04, FR-15, ADR-0048 決定 2, #1139, IADR-0479 決定 2: 出来高だけを差し替えた入力を返す。
    /// 他の入力（監視銘柄とその理由を含む）は同じ規律で組み直す。前営業日が AsOf 以降なら例外。
    /// </summary>
    public AsOfDecisionInput WithVolume(DailyVolumeContext? volume) =>
        new(AsOf, Policy, Sizing, _price, _references, RateToBase, _notReconstructable, Watchlist, _watchlistUnavailableReason,
            _previousClose, volume);

    // FR-15, ADR-0036 決定1, #749, IADR-0387: 4 種（ADR-0044 決定 3 の (e) を含む）すべての再構成可否を導出する（**部分申告を作らない**）。
    //
    // 導出の規則:
    //   - 供給側が申告した種別は `NotReconstructable`（申告は無条件に効く。供給側だけが情報源の射程を知る）。
    //   - (b) は**発行時刻が不明で落としたものがあれば自動で `NotReconstructable`** —— 時点に置けなかった資料が
    //     現にあった以上、その日の参考情報は「無かった」ではなく「当時の集合を再構成できなかった」である。
    //     🔴 **未来を落としただけでは倒さない** —— AsOf より後の資料を除くのは as-of の**正しい**振る舞いであり、
    //     入力が痩せたのではない（落とさなければルックアヘッドになる）。
    //   - (b) が 0 件で落としたものも無ければ `AbsentAtAsOf`（**当時ニュースが無かったという事実**。
    //     本番の AI 判断も同じ入力で動くため、除外の理由にならない）。
    //   - (c)(d) は値の有無から痩せを観測できないため、申告が無ければ `Reconstructed`。
    //   - (e) 当時の監視銘柄（FR-04, ADR-0044 決定 3, #1034, IADR-0440 決定 7）は**値の有無そのものが可否である** ——
    //     一覧が無ければ `NotReconstructable`（プロンプトの節は「不明」）。🔴 **既定は再構成できない側**であり、
    //     供給側が一覧を渡さない限り記録は合格根拠にならない（ADR-0044 決定 4 の暫定手段）。
    private static IReadOnlyList<Stage0AsOfInputStatus> DeriveAvailability(
        IEnumerable<Stage0AsOfInputKind>? notReconstructable, int keptReferenceCount, int droppedUndatedCount,
        bool watchlistReconstructed, string? watchlistUnavailableReason)
    {
        var declared = notReconstructable is null
            ? new HashSet<Stage0AsOfInputKind>()
            : [.. notReconstructable];

        var statuses = new List<Stage0AsOfInputStatus>(Stage0AsOfInputs.DeclarableKinds.Count);
        foreach (var kind in Stage0AsOfInputs.DeclarableKinds)
        {
            if (declared.Contains(kind))
            {
                statuses.Add(new Stage0AsOfInputStatus(
                    kind, Stage0AsOfInputAvailability.NotReconstructable, "供給側が再構成不可と申告した"));
                continue;
            }

            if (kind == Stage0AsOfInputKind.Watchlist)
            {
                statuses.Add(watchlistReconstructed
                    ? new Stage0AsOfInputStatus(kind, Stage0AsOfInputAvailability.Reconstructed)
                    : new Stage0AsOfInputStatus(
                        kind,
                        Stage0AsOfInputAvailability.NotReconstructable,
                        string.IsNullOrWhiteSpace(watchlistUnavailableReason)
                            ? "当時の監視銘柄を再構成できなかった（記録の対象銘柄では代えない。ADR-0044 決定 3）"
                            : $"当時の監視銘柄を再構成できなかった: {watchlistUnavailableReason}"));
                continue;
            }

            if (kind == Stage0AsOfInputKind.NewsAndDisclosures && droppedUndatedCount > 0)
            {
                statuses.Add(new Stage0AsOfInputStatus(
                    kind,
                    Stage0AsOfInputAvailability.NotReconstructable,
                    $"発行時刻が不明な参考情報 {droppedUndatedCount} 件を時点に置けなかった"));
                continue;
            }

            var availability = kind == Stage0AsOfInputKind.NewsAndDisclosures && keptReferenceCount == 0
                ? Stage0AsOfInputAvailability.AbsentAtAsOf
                : Stage0AsOfInputAvailability.Reconstructed;
            statuses.Add(new Stage0AsOfInputStatus(kind, availability));
        }

        return statuses;
    }

    public DateOnly AsOf { get; }

    public DailyPolicy Policy { get; }

    public SizingContext Sizing { get; }

    /// <summary>AsOf 時点の参照価格（null は価格文脈なし）。</summary>
    public decimal? ReferencePrice { get; }

    /// <summary>
    /// FR-02, FR-04, #1035, IADR-0451: 判断時点の日中文脈。前日終値（AsOf より前の最後の終値）だけを持ち、
    /// 当日の始値・日中高安は常に不明（null）。プロンプトの値動きの行へ渡る。
    /// </summary>
    public IntradayPriceContext Intraday { get; }

    /// <summary>
    /// FR-04, ADR-0048 決定 2, #1139, IADR-0479 決定 2: 判断時点の前営業日までの確定足から計算した出来高（null＝判断の出来高が無効）。
    /// 記録器はプロンプトの出来高の行をここからだけ書く（本番と同じ <c>TradeDecisionPromptBuilder.Build(volume:)</c>）。
    /// </summary>
    public DailyVolumeContext? Volume { get; }

    /// <summary>基準通貨への換算レート（基準通貨の市場では 1）。</summary>
    public decimal RateToBase { get; }

    /// <summary>AsOf 以前の参考情報だけ。</summary>
    public IReadOnlyList<RetrievedContext> References { get; }

    /// <summary>AsOf より後だったため落とした参考情報の件数（**黙って捨てない**）。</summary>
    public int DroppedFutureReferenceCount { get; }

    /// <summary>発行時刻が不明だったため落とした参考情報の件数。</summary>
    public int DroppedUndatedReferenceCount { get; }

    /// <summary>
    /// FR-04, ADR-0044 決定 3, #1034, IADR-0440 決定 7: 判断時点の監視銘柄（(e)）。**null は再構成できなかった**
    /// （プロンプトの節は「不明」と書く）。記録器はプロンプトの監視銘柄をここからだけ取る。
    /// </summary>
    public IReadOnlyList<WatchedSymbol>? Watchlist { get; }

    /// <summary>
    /// FR-15, ADR-0036 決定1, #749, IADR-0387: as-of 入力 4 種（(b)(c)(d)(e)。ADR-0044 決定 3）の再構成可否（**常に 4 件そろう**）。
    /// 記録（<c>Stage0DecisionRecord.AsOfInputs</c>）へそのまま載る。
    /// </summary>
    public IReadOnlyList<Stage0AsOfInputStatus> AsOfInputs { get; }

    /// <summary>
    /// 再構成できなかった種別（安定順・空なら痩せていない）。**この判断は Stage 0 の判定母集団から外れる。**
    /// </summary>
    public IReadOnlyList<Stage0AsOfInputKind> NotReconstructableKinds =>
        Stage0AsOfInputs.NotReconstructableKinds(AsOfInputs);
}

// FR-04, FR-15, ADR-0033 決定2, #632, IADR-0318: as-of 入力の供給ポート。
//
// **実供給は残件である**（過去のニュース・開示・価格を「その時点までに得られていた分だけ」再構成できるかは
// 情報源の提供範囲に依存する。ADR-0033 §残るもの が「実装が記録器を作る過程で判明する」とした点）。
// 既定実装は常に null を返し、記録は 1 件も作られない（＝LLM も呼ばれない）。
public interface IAsOfDecisionInputProvider
{
    /// <summary>
    /// 指定銘柄の AsOf 時点の入力を返す。**非取引日・入力を 1 つも組めない日は null**
    /// （呼び出し元はその日をスキップする）。
    /// <para>
    /// 🔴 FR-15, ADR-0036 決定1, #749, IADR-0387: **一部の入力だけが再構成できない場合は null を返さない。**
    /// 入力を組み、再構成できなかった種別を <c>notReconstructable</c> で申告する ——
    /// 同決定は「**『外す』は『走らせない』ではない。痩せた入力での実行はしてよい。その結果を合格根拠として
    /// 引かないことだけを定める**」と明記しており、記録ごと消すと**何を外したのかが記録から読めなくなる**。
    /// </para>
    /// </summary>
    Task<AsOfDecisionInput?> GetAsync(
        string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default);
}

// 安全既定: 常に「入力なし」。実供給を構成するまで記録は 1 件も作られない（LLM も呼ばれない＝費用も出ない）。
public sealed class NoAsOfDecisionInputProvider : IAsOfDecisionInputProvider
{
    public Task<AsOfDecisionInput?> GetAsync(
        string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default) =>
        Task.FromResult<AsOfDecisionInput?>(null);
}
