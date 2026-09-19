using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

// FR-10, ADR-0040 決定1, #820（#826 項目 3 の S1 側）, IADR-0344 決定5-2・決定6・追記(4)・追記(7): 保護記録の「持ち分」。
//
// 🔴 **持ち分を毎巡回ゼロから計算し直さない。** ブローカーの建玉照会は銘柄単位の純額であり、どの建玉がどの保護記録の
// ものかを区別しない。それを毎回引き直して持ち分を決めると、規則をどう変えても「売り過ぎ（反対建玉）」か
// 「損切りが黙って出ない」のどちらかへ倒れる（#820 で 4 巡連続して実測された）。
// そこで **S1 の行は自分の残保護数量（ProtectiveStopOrder.RemainingProtected）を状態として持つ**。
//
// 🔴 **［IADR-0344 追記(7) / #820 の 7 巡目監査］「削る前に確かめる」形にした。復元という操作は存在しない。**
// 追記(5)・追記(6) は「観測した巡回で即時に削り、建玉が戻ったら復元する」形だった。しかし
// **建玉照会の純額からは「自分の建玉が戻った」と「他人の建玉が現れた」を区別できない**ため、戻す操作がある限り
// この区別を毎回誤る（7 巡のうち 3 巡が「削る／戻す」の往復で潰れた）。帳簿を書き換えるのを確定まで遅らせれば、
// 戻す対象そのものが無くなる。ここが動かすのは次の 3 つだけである。
//
//   1. **確定（エントリーの約定）**（ConfirmEntryFills）— 発注記録が終端になった時点で、残保護数量＝約定数量。
//   2. **外部要因の減少の観測**（ReconcileShares）— 人手決済・強制決済・S0 の逆指値の約定などで建玉が減ったぶんを、
//      **PendingExternalReduction へ「観測値」として積むだけ**にとどめる（RemainingProtected は動かさない）。
//      削る順は **帳簿だけの行（S1）→ 実注文を持つ行（S0）**、それぞれの中で作成時刻 → EntryDecisionId である
//      （#820 の 5 巡目監査。作成時刻だけで決めると、生きた S0 の逆指値が 0 にされて取り消される）。
//   3. **確定（外部要因の減少）**（ReconcileShares の観測の巡回）— 同じ観測値が
//      ExternalReductionConfirmations 回**連続**したときだけ RemainingProtected を減らし、1 行 1 回だけ通知する。
//
// 自分が出した決済による減算は SoftwareStopExecutor が行う（決定的な SoftwareCloseDecisionId の試行と 1:1）。
//
// 🔴 **未確定の観測は書き戻さない。** 超過が見えなくなっても帳簿へは何も書き戻さない
// （RemainingProtected は確定でしか動かない）。これにより
//   - **1 巡回だけ過少に照会された行が失われない**（帳簿を書いていないので、State も RemainingProtected も無傷）
//   - **S1 の記録を持たない建玉（S2 の建玉・人手で建てた建玉）が現れても幽霊行が復活しない**
// の 2 つが同時に成り立つ。倒れ方は常に「その行はしばらく動けない（EffectiveProtectedQuantity が 0）」側であり、
// **反対建玉という不可逆な事故側へは倒れない**。
//
// 🔴 **［IADR-0344 追記(8) / #820 の 8 巡目監査］観測を「対称に」失効させる。**
// 追記(7) の観測値は**単調**で確定か完了でしか消えなかった。そのため建玉照会が **1 巡回だけ**過少に返っただけで
// PendingExternalReduction がその値のまま残り、以後どれだけ照会が正常でも EffectiveProtectedQuantity が
// **恒久的に 0**——行は Active・帳簿も無傷なのでどの検査も通るのに、到達しても 1 株も決済しない（無音）。
// **超過が ExternalReductionConfirmations 回連続で「消えた」ら観測を捨てる**（確定と同じ回数で対称にする）。
// **これは「復元」ではない**——帳簿（RemainingProtected）を書き戻さないので、6・7 巡目の復元問題は生じない。
// 🔴 ただし **到達済みの行では失効させない**（下の Confirm の注記。7 巡目 BLK-7-2 / T-10-411）。
//
// 🔴 **不変条件（#820 の 6 巡目監査・IADR-0344 追記(6)。追記(7) でも維持する）**:
//   **Active 行が実際に動かす株数の合計 ＋ 送信済みで建玉照会に未反映の決済 ≦ 方向の純額**
public static class ProtectiveStopNetting
{
    /// <summary>
    /// #820 の 5 巡目監査, IADR-0344 追記(5)・追記(7): 外部要因による減少を<b>確定</b>するまでに要する連続観測回数。
    /// 建玉照会は銘柄単位の純額でしかなく、<b>1 巡回だけ過少に返り得る</b>——1 回の観測で帳簿を書き換えない。
    /// </summary>
    public const int ExternalReductionConfirmations = 2;

    /// <summary>
    /// #820 の 6 巡目監査, IADR-0344 追記(6)・追記(7): <b>観測を数え続けてよいかの判定</b>で走査する完了済み S1 行の上限。
    /// 更新が新しい順に引くため、直前の巡回で決済を送って完了した行（＝未反映の決済を抱える行）が先に入る。
    /// </summary>
    public const int SentCloseScanLimit = 50;

    /// <summary>
    /// #820 の 8 巡目監査, IADR-0344 追記(8) 決定3: <b>主張はあるのに 1 株も動かせない</b>状態が続いていることを
    /// Critical で知らせるまでの猶予。<b>到達の有無に依らない</b>。
    /// 孤立行の猶予（<c>SoftwareStopExecutor.DefaultOrphanGrace</c>）・据え置きの猶予
    /// （<c>DefaultSettlementGrace</c>）と同型の 15 分にする（同じ性質の「黙って続く異常」を同じ尺度で扱う）。
    /// </summary>
    public static readonly TimeSpan ProtectionSuspendedGrace = TimeSpan.FromMinutes(15);

    /// <summary>
    /// #820 の 10 巡目監査, IADR-0344 追記(9) 決定3: <b>帰属不明の建玉</b>が同じ株数のまま続いているときに、
    /// 改めて知らせるまでの間隔。状態が変わったとき（株数が動いたとき）は間隔を待たずに知らせる。
    /// <b>毎巡回（既定 30 秒）鳴らすと通知が埋もれて意味を失う</b>ため、猶予の類（15 分）より長く取る。
    /// </summary>
    public static readonly TimeSpan UnattributedRenotifyInterval = TimeSpan.FromMinutes(60);

    /// <summary>
    /// FR-10, #820, IADR-0344 決定6・追記(4) 決定10・追記(7): <b>S0（ブローカー側逆指値）の行から見た建玉残</b>。
    /// 方向の純額から<b>他の S1 行が実際に動かす株数</b>を差し引く（S1 行が無い構成では差し引く量が 0 で従来と同一）。
    /// <para>
    /// 差し引くのは、S1 の建玉が S0 の「建玉あり」を支えてしまうと、S0 の建玉が消えた後も逆指値が残って
    /// <b>反対建玉</b>を生むためである（#826 項目 3）。発注記録（決済レグ）は<b>一切見ない</b>——
    /// 完了済み S0 行の取消済みレグで持ち分が食われる事故（#820 の 4 巡目監査 BLK-2）を構造的に無くす。
    /// </para>
    /// <para>
    /// 🔴 差し引くのは<b>未確定の観測を引いた後</b>の値（<see cref="ProtectiveStopOrder.EffectiveProtectedQuantity"/>）である。
    /// 帳簿の値で引くと、S1 が超過を吸収している最中に S0 の建玉残が 0 に見え、<b>生きた逆指値を取り消す</b>（BLK-1）。
    /// </para>
    /// </summary>
    public static int RemainingPositionFor(
        ProtectiveStopOrder stop,
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        IEnumerable<ProtectiveStopOrder> activeStops)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activeStops);

        var claimedByOtherMechanisms = activeStops
            .Where(other => other.EntryDecisionId != stop.EntryDecisionId
                && other.State == ProtectiveStopState.Active
                && other.Mechanism != stop.Mechanism
                && other.Symbol == stop.Symbol
                && other.Market == stop.Market
                && other.EntrySide == stop.EntrySide)
            .Sum(other => other.EffectiveProtectedQuantity);

        var net = Math.Max(0, DirectionalNet(stop, snapshot) - claimedByOtherMechanisms);

        // 未確定の観測で自分の動かせる株数が 0 になっていれば、その分の建玉はこの巡回では自分のものではない。
        // 未確定＝null の行は旧来の計算のまま（S1 が無い構成で挙動が変わらない）。
        return stop.RemainingProtected is not null ? Math.Min(stop.EffectiveProtectedQuantity, net) : net;
    }

    // 建玉スナップショットから「エントリー方向の残数量」を求める。数量は符号付き（+ロング/−ショート・IADR-0118）。
    public static int DirectionalNet(ProtectiveStopOrder stop, IReadOnlyList<BrokerPositionSnapshot> snapshot) =>
        DirectionalNet(stop.Symbol, stop.Market, stop.EntrySide, snapshot);

    /// <summary>銘柄・市場・エントリー方向から「その方向の建玉残」を求める（行を持たない呼び出し用）。</summary>
    public static int DirectionalNet(
        string symbol, Market market, TradeSide entrySide, IReadOnlyList<BrokerPositionSnapshot> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var net = snapshot
            .Where(p => p.Symbol == symbol && p.Market == market)
            .Sum(p => p.Quantity);
        return entrySide == TradeSide.Buy ? Math.Max(0, net) : Math.Max(0, -net);
    }

    /// <summary>
    /// FR-10, ADR-0040 決定1（S1）, #820 の 7 巡目監査, IADR-0344 追記(7):
    /// 同じ銘柄・市場・方向の <b>S1 行の残保護数量を確定し、外部要因による減少を観測して（確定したら）保存する</b>。
    /// 変化した行を保存したうえで、その群の最新の行を返す。
    /// <para>
    /// <b>エントリーの確定</b>: 発注記録が終端になっていれば、残保護数量＝その約定数量（未確定の行だけを確定する。
    /// 自分の決済で減った値を約定数量へ戻さない）。記録が無い・未終端の行は<b>未確定のまま</b>で、
    /// 主張もしないし削られもしない（孤立行の扱いは SoftwareStopExecutor の猶予と Critical が受け持つ）。
    /// </para>
    /// <para>
    /// 🔴 <b>外部要因の超過は「観測」として積むだけで、帳簿（RemainingProtected）を書き換えない。</b>
    /// 同じ観測値が <see cref="ExternalReductionConfirmations"/> 回<b>連続</b>したときだけ確定し、
    /// <see cref="SoftwareStopExecuted"/>（<see cref="SoftwareStopOutcome.ProtectionReduced"/>）を<b>1 行 1 回</b>積む。
    /// 超過が見えなくなった巡回は<b>観測の連続回数を 0 へ戻すだけ</b>で、書き戻す（復元する）ものは無い。
    /// </para>
    /// <para>
    /// 🔴 <b>割り当ては「帳簿だけの行」から</b>（#820 の 5 巡目監査）: 超過分は
    /// <b>S1（ブローカーに注文を持たない行）を先に使い切り</b>、<b>S0（実注文を持つ行）は最後</b>に回す。
    /// S0 の行に注文照会が <c>Pending</c> を返していること自体が「その建玉はまだ在る」証拠であり、
    /// 作成時刻だけで順序を決めると<b>ブローカーに実在する生きた逆指値が 0 にされて取り消される</b>（無音・不可逆）。
    /// <b>S0 行は「全部か 0 か」でしか削らない</b>——帳簿だけを部分的に削っても注文は縮まず、縮まない注文が
    /// 建玉より大きいまま残ると発火して<b>反対建玉</b>を作る（#826 項目 3）。
    /// </para>
    /// <para>
    /// 🔴 <b>その巡回で動かしてよい株数は「観測を引いた後」の値である</b>
    /// （<see cref="ProtectiveStopOrder.EffectiveProtectedQuantity"/>）。これは一時的な計算であって永続化しない。
    /// 引かないと、同じ巡回の別の行が古い建玉を主張して<b>売り過ぎる</b>（T-10-353 が実測で固定している）。
    /// </para>
    /// </summary>
    /// <param name="observing">
    /// この呼び出しを<b>1 回の観測</b>として数えるか。ガードの巡回の先頭（群につき 1 巡回 1 回）だけが <c>true</c> で、
    /// 確定と通知を行う。決済経路（<c>SoftwareStopExecutor</c>）からの呼び出しは観測値の記録だけを行う。
    /// </param>
    /// <param name="events">確定したときに発行すべきイベントの受け皿（発行は呼び出し側）。</param>
    public static IReadOnlyList<ProtectiveStopOrder> ReconcileShares(
        string symbol,
        Market market,
        TradeSide entrySide,
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        IEnumerable<ProtectiveStopOrder> activeStops,
        IProtectiveStopOrderStore stops,
        IExecutedOrderStore store,
        DateTimeOffset now,
        bool observing = false,
        ICollection<object>? events = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activeStops);
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(store);

        var group = ConfirmEntryFills(activeStops, stops, store, now)
            .Where(s => s.State == ProtectiveStopState.Active
                && s.Symbol == symbol && s.Market == market && s.EntrySide == entrySide)
            .ToList();

        // 🔴 **S1 の主張が 1 株も無い群には手を触れない。** 割り当ての目的は S1 の持ち分を確定させることであり、
        // S0 だけの群（実弾・S0 のみの構成）では従来の判定がそのまま働く——ここで S0 の主張を削ると
        // 「同一銘柄に複数の S0 が併存し純額がそれを賄えない」既存の近似の挙動が変わってしまう（IADR-0344 追記(4)）。
        // ただし**未確定の観測を抱えた行がある群は例外**である（確定をここでしか解けないため）。
        if (group.Where(s => s.IsSoftwareStop).Sum(s => s.ProtectedQuantity) <= 0
            && group.All(s => !s.HasUnconfirmedExternalReduction))
        {
            return group;
        }

        // 🔴 **超過は「帳簿の主張 − 純額」だけで測る。** ここへ「送信済みで未反映の決済」を足して大きくすると、
        // 逆に決済レグの記録が建玉照会より遅れている局面（取消が当日一覧から消えるブローカー）で
        // **二重に削る**（4 巡目監査 B1 と同型）。未反映の決済は下の「観測を数え続けてよいか」の判定にだけ使う。
        var excess = group.Sum(s => s.ProtectedQuantity) - DirectionalNet(symbol, market, entrySide, snapshot);
        var observed = Allocate(group, Math.Max(0, excess));

        if (!observing)
        {
            RecordObservation(group, observed, stops, now);
            return group;
        }

        return Confirm(group, observed, excess, symbol, market, entrySide, stops, store, events, now);
    }

    // 超過分を「帳簿だけの行（S1）→ 実注文を持つ行（S0）」の順に割り当てる（保存しない・純粋な計算）。
    private static Dictionary<Guid, int> Allocate(List<ProtectiveStopOrder> group, int budget)
    {
        var takes = new Dictionary<Guid, int>();
        foreach (var row in group
            .Where(s => s.ProtectedQuantity > 0)
            .OrderBy(s => s.IsSoftwareStop ? 0 : 1)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.EntryDecisionId))
        {
            if (budget <= 0)
                break;

            // S0 は部分的に削れない（ブローカーの注文を縮められないため）。超過が主張に届かなければ飛ばす。
            if (!row.IsSoftwareStop && row.ProtectedQuantity > budget)
                continue;

            var take = Math.Min(row.ProtectedQuantity, budget);
            takes[row.EntryDecisionId] = take;
            budget -= take;
        }

        return takes;
    }

    // 決済経路（観測として数えない呼び出し）: **観測値を増やす方向にだけ**記録する。
    // 🔴 減らさないのは、同じ巡回で先に決済を出した行が Completed になって群から消えると超過が見かけ上消え、
    // 残った行が古い建玉を主張して売り過ぎるためである（T-10-353）。確定も通知もここでは行わない。
    //
    // 🔴 #820 の 10 巡目監査, IADR-0344 追記(9) 決定4: **不在の連続回数（ExternalReductionAbsences）も 0 へ戻す。**
    // 観測が増えた＝真の追加減少であり、それまでに数えた「超過が消えた」観測は無効である。戻さないと、
    // 決済経路が観測を積み増した直後の 1 巡回で失効が成立し得た（確定と対称であるべき失効が対称でなくなる）。
    // Confirm 側の同じ分岐は初めから 0 へ戻していた。
    private static void RecordObservation(
        List<ProtectiveStopOrder> group,
        IReadOnlyDictionary<Guid, int> observed,
        IProtectiveStopOrderStore stops,
        DateTimeOffset now)
    {
        foreach (var row in group.ToList())
        {
            var take = observed.GetValueOrDefault(row.EntryDecisionId);
            if (take <= row.PendingExternalReduction)
                continue;

            Replace(
                group,
                row with
                {
                    PendingExternalReduction = take,
                    ExternalReductionObservations = 0,
                    ExternalReductionAbsences = 0,
                    UpdatedAt = now,
                },
                stops);
        }
    }

    // ガードの巡回（群につき 1 巡回 1 回）: 観測を 1 回数え、規定回数に達した観測だけを**帳簿へ書いて**通知する。
    private static IReadOnlyList<ProtectiveStopOrder> Confirm(
        List<ProtectiveStopOrder> group,
        IReadOnlyDictionary<Guid, int> observed,
        int excess,
        string symbol,
        Market market,
        TradeSide entrySide,
        IProtectiveStopOrderStore stops,
        IExecutedOrderStore store,
        ICollection<object>? events,
        DateTimeOffset now)
    {
        // 超過が見かけ上小さくなった行がある場合にだけ、その理由を調べる（走査を毎巡回は行わない）。
        var needsEvidence = group.Any(s => s.HasUnconfirmedExternalReduction
            && observed.GetValueOrDefault(s.EntryDecisionId) < s.PendingExternalReduction);
        var supported = needsEvidence
            ? Allocate(group, Math.Max(0, excess + SharesAccountedElsewhere(symbol, market, entrySide, group, stops, store)))
            : new Dictionary<Guid, int>();

        foreach (var row in group.ToList())
        {
            var take = observed.GetValueOrDefault(row.EntryDecisionId);

            // 観測値が増えた（真の追加減少 or 初回）。**増分は 1 回しか観測していない**ので数え直す（6 巡目監査）。
            if (take > row.PendingExternalReduction)
            {
                Replace(
                    group,
                    row with
                    {
                        PendingExternalReduction = take,
                        ExternalReductionObservations = 1,
                        ExternalReductionAbsences = 0,
                        UpdatedAt = now,
                    },
                    stops);
                continue;
            }

            if (row.PendingExternalReduction <= 0)
                continue;

            // 🔴 超過が見えなくなった。**書き戻すものは無い**（帳簿を書いていないため）。
            // ただし「送信済みで未反映の決済」「確定前の新規エントリーの**約定済み**株数」が減り分を説明できるなら、
            // 超過は消えていない——数え続ける（そうしないと、決済を送って完了した行のぶんが「建玉が戻った」に見える）。
            if (Math.Max(take, supported.GetValueOrDefault(row.EntryDecisionId)) < row.PendingExternalReduction)
            {
                var expired = Expire(row, now);
                if (!ReferenceEquals(expired, row))
                    Replace(group, expired, stops);
                continue;
            }

            var observations = row.ExternalReductionObservations + 1;
            if (observations < ExternalReductionConfirmations)
            {
                Replace(
                    group,
                    row with
                    {
                        ExternalReductionObservations = observations,
                        ExternalReductionAbsences = 0,
                        UpdatedAt = now,
                    },
                    stops);
                continue;
            }

            // 確定: ここではじめて帳簿を減らす（0 になった行はガードが完了させる）。
            var reduction = row.PendingExternalReduction;
            Replace(
                group,
                row with
                {
                    RemainingProtected = Math.Max(0, row.ProtectedQuantity - reduction),
                    PendingExternalReduction = 0,
                    ExternalReductionObservations = 0,
                    ExternalReductionAbsences = 0,
                    UpdatedAt = now,
                },
                stops);

            // 🔴 無音にしない。外部要因で保護対象を減らしたことを必ず 1 回残す（S0 の行を 0 にして
            // 逆指値を取り消す場合は、この記録が唯一の痕跡になる）。
            events?.Add(new SoftwareStopExecuted(
                row.EntryDecisionId, row.Symbol, row.Market, SoftwareStopOutcome.ProtectionReduced,
                reduction, row.TriggerPrice, row.TriggeredPrice ?? row.TriggerPrice, row.Attempt,
                CloseDecisionId: null, CloseOrderId: null, CloseIntent: null, now));
        }

        NotifySuspended(group, stops, events, now);

        return group;
    }

    /// <summary>
    /// FR-10, #820 の 8 巡目監査, IADR-0344 追記(8): <b>超過が消えた巡回</b>の扱い（確定と対称の失効）。
    /// <para>
    /// 追記(7) は連続回数を 0 へ戻すだけで <see cref="ProtectiveStopOrder.PendingExternalReduction"/> を
    /// <b>単調</b>に保っていた。その結果、建玉照会が 1 巡回だけ過少に返っただけで観測がその値のまま残り、
    /// 以後どれだけ照会が正常でも <see cref="ProtectiveStopOrder.EffectiveProtectedQuantity"/> が
    /// <b>恒久的に 0</b>——到達しても 1 株も決済しない状態が無音で続いた（8 巡目監査 BLK-8-1）。
    /// <b>消えたことを確定と同じ回数だけ連続で観測したら、観測そのものを捨てる。</b>
    /// 帳簿（<see cref="ProtectiveStopOrder.RemainingProtected"/>）は書き戻さないので「復元」ではない。
    /// </para>
    /// <para>
    /// 🔴 <b>到達済みの行では失効させない。</b> その行は失効した瞬間に成行決済を出す——
    /// 「戻ってきた自分の建玉」と「他人の建玉（S2・人手）」は純額から区別できないため、
    /// <b>一度観測した超過を取り戻して即座に売る</b>のは 7 巡目 BLK-7-2（T-10-411）そのものである。
    /// 到達済みの行は据え置いたまま <see cref="SoftwareStopOutcome.CloseStalled"/> と
    /// <see cref="SoftwareStopOutcome.ProtectionSuspended"/> で人手へ回す（無音にはしない）。
    /// </para>
    /// </summary>
    private static ProtectiveStopOrder Expire(ProtectiveStopOrder row, DateTimeOffset now)
    {
        if (row.TriggeredAt is not null)
        {
            return row.ExternalReductionObservations == 0 && row.ExternalReductionAbsences == 0
                ? row
                : row with { ExternalReductionObservations = 0, ExternalReductionAbsences = 0, UpdatedAt = now };
        }

        var absences = row.ExternalReductionAbsences + 1;
        return absences < ExternalReductionConfirmations
            ? row with { ExternalReductionObservations = 0, ExternalReductionAbsences = absences, UpdatedAt = now }
            : row with
            {
                PendingExternalReduction = 0,
                ExternalReductionObservations = 0,
                ExternalReductionAbsences = 0,
                UpdatedAt = now,
            };
    }

    /// <summary>
    /// FR-10, #820 の 8 巡目監査, IADR-0344 追記(8) 決定3: <b>主張はあるのに 1 株も動かせない行</b>
    /// （<see cref="ProtectiveStopOrder.IsProtectionSuspended"/>）が猶予を過ぎたら Critical を<b>1 行 1 回</b>出す。
    /// <para>
    /// 行は <c>Active</c>・帳簿も無傷なので、状態や帳簿だけを見る検査はすべて通る。
    /// <b>到達の有無に依らず</b>知らせる——未到達の行には
    /// <see cref="SoftwareStopOutcome.CloseStalled"/>（到達からの猶予）が効かないため、無音のまま保護が失われる。
    /// </para>
    /// <para>
    /// 🔴 <b>#820 の 10 巡目監査, IADR-0344 追記(9) 決定5: ソフトウェア逆指値（S1）の行にだけ効かせる。</b>
    /// S0 の行も <see cref="ProtectiveStopOrder.IsProtectionSuspended"/> になり得る（「全部か 0 か」の割り当てで
    /// 主張の全量が未確定の観測に打ち消される）が、<b>S0 の保護はブローカーに実在する逆指値</b>であり、
    /// 帳簿の主張が一時的に打ち消されても<b>建玉は現に守られている</b>——
    /// 「ソフトウェア逆指値が 1 株も決済できない」という <see cref="SoftwareStopOutcome.ProtectionSuspended"/> の
    /// 文面はその行に当たらない。S0 側の帰結は確定時の <see cref="SoftwareStopOutcome.ProtectionReduced"/>
    /// （＋ガードによる逆指値の取消）が既に 1 回残す。
    /// </para>
    /// </summary>
    private static void NotifySuspended(
        List<ProtectiveStopOrder> group,
        IProtectiveStopOrderStore stops,
        ICollection<object>? events,
        DateTimeOffset now)
    {
        foreach (var row in group.ToList())
        {
            if (!row.IsSoftwareStop)
                continue;

            if (!row.IsProtectionSuspended)
            {
                if (row.ProtectionSuspendedSince is not null || row.ProtectionSuspendedNotifiedAt is not null)
                {
                    Replace(
                        group,
                        row with { ProtectionSuspendedSince = null, ProtectionSuspendedNotifiedAt = null, UpdatedAt = now },
                        stops);
                }

                continue;
            }

            if (row.ProtectionSuspendedSince is not { } since)
            {
                Replace(group, row with { ProtectionSuspendedSince = now, UpdatedAt = now }, stops);
                continue;
            }

            if (row.ProtectionSuspendedNotifiedAt is not null || now - since < ProtectionSuspendedGrace)
                continue;

            Replace(group, row with { ProtectionSuspendedNotifiedAt = now, UpdatedAt = now }, stops);
            events?.Add(new SoftwareStopExecuted(
                row.EntryDecisionId, row.Symbol, row.Market, SoftwareStopOutcome.ProtectionSuspended,
                row.ProtectedQuantity, row.TriggerPrice, row.TriggeredPrice ?? row.TriggerPrice, row.Attempt,
                CloseDecisionId: null, CloseOrderId: null, CloseIntent: null, now));
        }
    }

    /// <summary>
    /// FR-10, #820 の 10 巡目監査, IADR-0344 追記(9) 決定3: <b>どの保護記録も主張していない建玉</b>を毎巡回検知して
    /// <see cref="SoftwareStopOutcome.UnattributedPosition"/> を<b>1 回だけ</b>積む。
    /// <para>
    /// 🔴 <b>これは検知であって是正ではない。</b>建玉を売らず・記録も作らず・主張も動かさない。
    /// 武装の前提条件（<c>OrderExecutionAppService</c>）は<b>武装の時点しか見ない</b>ため、
    /// 武装より後に他人の建玉（S2・人手）が現れる経路と、<b>受理後に 0 約定で取り消された決済の残り</b>は、
    /// これまでどのイベントも出さないまま建玉が無保護で残っていた。材料（純額と保護記録）はガードが毎巡回持っている。
    /// </para>
    /// <para>
    /// 🔴 <b>走査は建玉の側から行う。</b> 決済が受理された時点で行は完了するため、
    /// 「受理後に取り消された決済の残り」の配置には <c>Active</c> な行が 1 件も残らない——
    /// Active 行の群を回すだけでは見えない。ただし <b>S1 の足跡がある群に限る</b>
    /// （Active な S1 行、または <see cref="IProtectiveStopOrderStore.FindCompletedSoftwareStops"/> が返す完了済みの行）。
    /// <b>S1 が 1 件も無い構成（実弾・S0 のみ）では 1 バイトも動かない。</b>
    /// </para>
    /// <para>
    /// <b>同じ状態で毎巡回鳴らさない</b>: 群の S1 行のうち<b>作成が最も新しい 1 行</b>が
    /// <see cref="ProtectiveStopOrder.UnattributedNotifiedQuantity"/> /
    /// <see cref="ProtectiveStopOrder.UnattributedNotifiedAt"/> を代表して持ち、
    /// <b>株数が変わったとき</b>か <see cref="UnattributedRenotifyInterval"/> が経ったときだけ出す。
    /// 帰属不明が消えたら両方を <c>null</c> へ戻す（再発したら改めて知らせる）。
    /// </para>
    /// <para>
    /// 🔴 <b>#820 の 11 巡目監査, IADR-0344 追記(10) の残る制約（NB-1・NB-2。追随は #880）</b>:
    /// (1) 外側のループは<b>建玉スナップショットの側</b>を回すため、<b>その銘柄が純額 0 になった巡回では
    /// リセット分岐に到達しない</b>——一度解消したあと<b>再発した同数</b>の帰属不明は
    /// <see cref="UnattributedRenotifyInterval"/> のあいだ黙る。
    /// (2) 呼び出し元のガードは<b>Active な行が 1 件も無い巡回では建玉を照会しない</b>ため、
    /// 「受理後に 0 約定で取り消された決済の残り」がその口座で唯一の S1 の痕跡なら検知が走らない。
    /// </para>
    /// </summary>
    public static void DetectUnattributedPositions(
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        IEnumerable<ProtectiveStopOrder> activeStops,
        IProtectiveStopOrderStore stops,
        IExecutedOrderStore store,
        DateTimeOffset now,
        ICollection<object>? events = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activeStops);
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(store);

        var active = activeStops.Where(s => s.State == ProtectiveStopState.Active).ToList();

        foreach (var (symbol, market, entrySide) in snapshot
            .Where(p => p.Quantity != 0)
            .Select(p => (p.Symbol, p.Market, EntrySide: p.Quantity > 0 ? TradeSide.Buy : TradeSide.Sell))
            .Distinct())
        {
            var net = DirectionalNet(symbol, market, entrySide, snapshot);
            if (net <= 0)
                continue;

            var group = active
                .Where(s => s.Symbol == symbol && s.Market == market && s.EntrySide == entrySide)
                .ToList();
            var completed = stops.FindCompletedSoftwareStops(symbol, market, entrySide, SentCloseScanLimit);

            // S1 の足跡が無い群（実弾・S0 のみ）には触れない。
            if (!group.Any(s => s.IsSoftwareStop) && completed.Count == 0)
                continue;

            // 群を代表して記録を持つ行（作成が最も新しい S1 行）。完了済みでも構わない（状態は変えない）。
            var anchor = group
                .Where(s => s.IsSoftwareStop)
                .Concat(completed)
                .OrderByDescending(s => s.CreatedAt)
                .ThenByDescending(s => s.EntryDecisionId)
                .First();

            // 帳簿の主張（一時的な観測で揺れない側）で引き、さらに「純額に含まれるが帳簿に現れない株数」を除く。
            var unattributed = net
                - group.Sum(s => s.ProtectedQuantity)
                - SharesAccountedElsewhere(symbol, market, entrySide, group, stops, store);

            if (unattributed <= 0)
            {
                if (anchor.UnattributedNotifiedQuantity is not null || anchor.UnattributedNotifiedAt is not null)
                {
                    stops.Save(anchor with
                    {
                        UnattributedNotifiedQuantity = null,
                        UnattributedNotifiedAt = null,
                        UpdatedAt = now,
                    });
                }

                continue;
            }

            if (anchor.UnattributedNotifiedQuantity == unattributed
                && anchor.UnattributedNotifiedAt is { } notifiedAt
                && now - notifiedAt < UnattributedRenotifyInterval)
            {
                continue;
            }

            stops.Save(anchor with
            {
                UnattributedNotifiedQuantity = unattributed,
                UnattributedNotifiedAt = now,
                UpdatedAt = now,
            });
            events?.Add(new SoftwareStopExecuted(
                anchor.EntryDecisionId, symbol, market, SoftwareStopOutcome.UnattributedPosition,
                unattributed, anchor.TriggerPrice, anchor.TriggeredPrice ?? anchor.TriggerPrice, anchor.Attempt,
                CloseDecisionId: null, CloseOrderId: null, CloseIntent: null, now));
        }
    }

    /// <summary>
    /// 🔴 #820 の 6 巡目監査・7 巡目監査, IADR-0344 追記(6)・追記(7):
    /// <b>純額の中に含まれているが、Active 行の帳簿には現れていない株数</b>。
    /// 超過が見かけ上消えた巡回で「本当に建玉が戻ったのか、それとも別の理由で純額が主張を上回っているのか」を分ける。
    /// <para>
    /// (1) <b>送信済みで建玉照会にまだ反映されていない決済</b>。決済レグの DecisionId は
    /// <see cref="ProtectiveStopIds.SoftwareCloseDecisionId"/> から決定的に導けるので、S1 の行
    /// （群の Active ＋ 同じ銘柄・方向の完了済み）の試行 1..<c>Attempt</c> を引き当て、非終端のレグの
    /// 未約定数量を合計する。<b>残保護数量が 0 になった行はその巡回で完了する</b>ため、完了済みも見なければ
    /// 受理・未約定の決済がまるごと見えなくなる。
    /// </para>
    /// <para>
    /// (2) <b>確定前の新規エントリーの<u>約定済み</u>株数</b>（<c>RemainingProtected</c> が null の S1 行）。
    /// 🔴 <b>承認数量ではなく約定数量で数える</b>（#820 の 7 巡目監査 BLK-7-1 の是正）。承認数量で数えると、
    /// <b>1 株も約定していない新規エントリーが 1 件あるだけで</b>「建玉が戻った」の判定が効かなくなり、
    /// 建玉が実在する行の観測が確定してしまう。
    /// </para>
    /// <para>
    /// 🔴 <b>S0 の逆指値レグは数えない。</b> 滞留中の逆指値は「送信済みの決済」ではなく<b>まだ約定していない保護注文</b>で、
    /// その株数は S0 行の主張が既に覆っている（数えると二重に引く）。
    /// </para>
    /// <para>
    /// 🔴 <b>持ち分（残保護数量）の計算には使わない。</b> この集計は<b>観測を数え続けてよいかの門</b>にだけ使う
    /// ——追記(4) で撤去した「毎巡回の引き直し」を復活させるものではない。
    /// </para>
    /// </summary>
    private static int SharesAccountedElsewhere(
        string symbol,
        Market market,
        TradeSide entrySide,
        List<ProtectiveStopOrder> group,
        IProtectiveStopOrderStore stops,
        IExecutedOrderStore store)
    {
        var sent = 0;
        foreach (var row in group
            .Where(s => s.IsSoftwareStop)
            .Concat(stops.FindCompletedSoftwareStops(symbol, market, entrySide, SentCloseScanLimit)))
        {
            for (var attempt = 1; attempt <= row.Attempt; attempt++)
            {
                var leg = store.FindByDecisionId(
                    ProtectiveStopIds.SoftwareCloseDecisionId(row.EntryDecisionId, attempt));
                if (leg is not null && OrderStatusLifecycle.IsPending(leg.Status))
                    sent += Math.Max(0, leg.Quantity - leg.FilledQuantity);
            }
        }

        var unconfirmedEntryFills = group
            .Where(s => s.IsSoftwareStop && s.RemainingProtected is null)
            .Sum(s => Math.Max(0, store.FindByDecisionId(s.EntryDecisionId)?.FilledQuantity ?? 0));

        return sent + unconfirmedEntryFills;
    }

    private static void Replace(
        List<ProtectiveStopOrder> group, ProtectiveStopOrder updated, IProtectiveStopOrderStore stops)
    {
        stops.Save(updated);
        group[group.FindIndex(s => s.EntryDecisionId == updated.EntryDecisionId)] = updated;
    }

    /// <summary>
    /// #820 の 4 巡目監査, IADR-0344 追記(4): <b>確定</b>だけを行う（建玉の照会が要らない）。
    /// エントリーの発注記録が終端になった S1 行の残保護数量を、その約定数量で確定して保存する。
    /// 記録が無い・まだ終端でない行は<b>未確定のまま</b>（主張 0）で、割り当ての対象にもしない。
    /// <para>
    /// ガードは巡回の<b>最初</b>にこれを通す——S0 行の建玉残は S1 行の残保護数量を差し引いて判定するため、
    /// 確定していない S1 行があると S0 が「建玉が自分のもの」と誤認する（#826 項目 3 が 1 巡回ぶん効かなくなる）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<ProtectiveStopOrder> ConfirmEntryFills(
        IEnumerable<ProtectiveStopOrder> activeStops,
        IProtectiveStopOrderStore stops,
        IExecutedOrderStore store,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activeStops);
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(store);

        var rows = activeStops.ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.IsSoftwareStop || row.IsEntryFillConfirmed || row.State != ProtectiveStopState.Active)
                continue;

            var entry = store.FindByDecisionId(row.EntryDecisionId);
            if (entry is null || !OrderStatusLifecycle.IsTerminal(entry.Status))
                continue; // 記録が無い・これから約定し得る → 未確定のまま（主張 0）。

            var confirmed = row with { RemainingProtected = Math.Max(0, entry.FilledQuantity), UpdatedAt = now };
            stops.Save(confirmed);
            rows[i] = confirmed;
        }

        return rows;
    }
}
