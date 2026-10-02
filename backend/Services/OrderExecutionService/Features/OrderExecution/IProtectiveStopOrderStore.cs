using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

// FR-10, #331, IADR-0210 決定6: 保護逆指値レグの記録ストア。EntryDecisionId につき高々 1 行
// （最新試行のみ）。ProtectiveStopGuard の巡回対象（Active）の洗い出しの権威。実運用では PostgreSQL。
public interface IProtectiveStopOrderStore
{
    /// <summary>
    /// 保存する（EntryDecisionId で upsert。再発注は同キーの上書き＝試行の置き換え）。
    /// <para>
    /// 🔴 #833 項目3, IADR-0396: <b>無条件の上書き</b>である（版は保存先の値から 1 進める）。読んだ写しが古くても書く。
    /// 並行に進んだ状態を巻き戻してはならない経路は <see cref="TrySave"/>（または <c>Update</c> 拡張）を使う。
    /// 無条件のまま残しているのは、ブローカーへの操作（逆指値の再発注・取消）の<b>後</b>に書く常駐ガードの経路である
    /// ——そこで保存を落とすと、実在する注文と記録が食い違う（IADR-0396 の残る制約）。
    /// </para>
    /// </summary>
    void Save(ProtectiveStopOrder stop);

    /// <summary>
    /// 🔴 FR-10, #833 項目3, IADR-0396: <b>楽観並行の保存</b>。保存先の版が <paramref name="stop"/> の
    /// <see cref="ProtectiveStopOrder.Version"/>（この写しを読んだ時点の版）と一致するときだけ書き、版を 1 進めて true を返す。
    /// 一致しない（並行に誰かが書いた）・行が無いときは<b>何も書かずに</b> false を返す。
    /// <para>
    /// false の後の <see cref="Find"/> は保存先の最新を返す（読み直して判断をやり直すため）。
    /// </para>
    /// <para>
    /// 既定の実装は「読んで比べてから書く」で<b>原子的ではない</b>（試験用の包み型のためのもの）。
    /// 本番のストア（EF・インメモリ）は原子的に上書きしている。
    /// </para>
    /// </summary>
    bool TrySave(ProtectiveStopOrder stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        var current = Find(stop.EntryDecisionId);
        if (current is null || current.Version != stop.Version)
            return false;

        Save(stop);
        return true;
    }

    /// <summary>EntryDecisionId で引く（無ければ null）。</summary>
    ProtectiveStopOrder? Find(Guid entryDecisionId);

    /// <summary>
    /// Active な記録を古い順に最大 <paramref name="batchSize"/> 件返す（ProtectiveStopGuard の巡回対象）。
    /// </summary>
    IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize);

    /// <summary>
    /// FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定4: Active なソフトウェア逆指値（S1）のうち、銘柄・市場・エントリー方向が
    /// 一致するものを古い順に返す（損切りライン到達の突き合わせ対象）。
    /// </summary>
    IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(string symbol, Market market, TradeSide entrySide);

    /// <summary>
    /// 🔴 FR-10, #879, IADR-0424 決定1: Active な記録のうち、銘柄・市場・エントリー方向が一致するものを<b>機構を問わず・件数の上限なしで</b>
    /// 古い順に返す（照会不明で見送る決済の建玉の保護の記録）。
    /// <para>
    /// <see cref="FindActive"/>（古い順に上限つき）を絞り込んで使うと、Active 行が上限を超えたとき<b>この銘柄の新しい行が落ち</b>、
    /// 保護の記録が「無い」「少ない」と読まれる（PR #999 の監査 N1）。本番のストア（EF・インメモリ）は条件つきの問い合わせで上書きする。
    /// 既定の実装（試験用の包み型のためのもの）は上限なしで全件を読んでから絞る。
    /// </para>
    /// </summary>
    IReadOnlyList<ProtectiveStopOrder> FindActiveFor(string symbol, Market market, TradeSide entrySide) =>
        FindActive(int.MaxValue)
            .Where(s => s.State == ProtectiveStopState.Active
                && s.Symbol == symbol && s.Market == market && s.EntrySide == entrySide)
            .ToList();

    /// <summary>
    /// 🔴 FR-10, ADR-0040 決定1, #1048, IADR-0481 決定1: 銘柄・市場・エントリー方向が一致する記録のうち<b>完了していないもの</b>
    /// （<see cref="ProtectiveStopState.Active"/> と、送信結果待ちの <see cref="ProtectiveStopState.AwaitingEntry"/>）を、
    /// <b>機構を問わず・件数の上限なしで</b>古い順に返す（同じ銘柄に別の手法の建玉を併存させないための照合）。
    /// <para>
    /// AwaitingEntry を含めるのは、エントリーの送信結果が不明のまま突合を待つ S0 / S3 の建玉が実在し得るためである
    /// （含めないと、その間に別の手法で建てられる）。
    /// 既定の実装（試験用の包み型のためのもの）は Active な記録だけを返す（<see cref="FindActiveFor"/>）。
    /// 本番のストア（EF・インメモリ）は状態で絞る問い合わせで上書きする。
    /// </para>
    /// </summary>
    IReadOnlyList<ProtectiveStopOrder> FindOpenFor(string symbol, Market market, TradeSide entrySide) =>
        FindActiveFor(symbol, market, entrySide);

    /// <summary>
    /// 🔴 FR-10, ADR-0050 決定1, #1121, IADR-0466 決定2: 銘柄・市場・エントリー方向が一致する記録を<b>状態も機構も問わず</b>、
    /// 更新が新しい順に最大 <paramref name="limit"/> 件返す。
    /// <para>
    /// 用途は<b>処理中の決済が保護の機構の出したものか</b>を見分けることだけである（S1 の決済の前に判断の手仕舞いを取り消す段。
    /// 保護レグ〔S0 / S3 の逆指値〕・他の S1 の決済・失効した逆指値の成行手仕舞いは取り消さない）。完了した記録の逆指値が
    /// まだ生きていることがある（IADR-0461 の残余）ため、Active だけでは足りない。
    /// </para>
    /// <para>
    /// 既定の実装（試験用の包み型のためのもの）は Active な記録だけを返す（<see cref="FindActiveFor"/>）。
    /// 本番のストア（EF・インメモリ）は状態を問わない問い合わせで上書きする。
    /// </para>
    /// </summary>
    IReadOnlyList<ProtectiveStopOrder> FindRecentFor(string symbol, Market market, TradeSide entrySide, int limit) =>
        FindActiveFor(symbol, market, entrySide).OrderByDescending(s => s.UpdatedAt).Take(limit).ToList();

    /// <summary>
    /// FR-10, #820 の 6 巡目監査・7 巡目監査, IADR-0344 追記(6)・追記(7): <b>完了済み</b>のソフトウェア逆指値（S1）のうち、
    /// 銘柄・市場・エントリー方向が一致するものを<b>更新が新しい順</b>に最大 <paramref name="limit"/> 件返す。
    /// <para>
    /// 🔴 <b>用途は「外部要因の観測を数え続けてよいか」の門だけ</b>である（<see cref="ProtectiveStopNetting"/>）。
    /// 決済を送って残保護数量が 0 になった行は同じ巡回で完了するが、その決済が<b>受理・未約定</b>のあいだ
    /// 建玉照会はまだ減っていない——完了した行を見ずに「建玉が戻った」と読むと、観測が確定できず
    /// 幽霊行が帳簿の主張を保ち続ける。持ち分（残保護数量）の計算にはこの結果を使わない。
    /// </para>
    /// </summary>
    IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
        string symbol, Market market, TradeSide entrySide, int limit);

    /// <summary>
    /// 🔴 FR-10, #880, IADR-0412 決定2: <b>帰属不明の建玉を通知済み</b>の印
    /// （<see cref="ProtectiveStopOrder.UnattributedNotifiedQuantity"/> / <see cref="ProtectiveStopOrder.UnattributedNotifiedAt"/>
    /// のいずれか）を持つ行を、<b>状態を問わず</b>更新が新しい順に最大 <paramref name="limit"/> 件返す。
    /// <para>
    /// 用途は帰属不明の検知（<see cref="ProtectiveStopNetting.DetectUnattributedPositions"/>）が
    /// <b>建玉照会から消えた（純額 0 の）群も訪れて印をリセットする</b>ことだけである。
    /// 建玉スナップショットの側だけを回すと、解消したあと再発した同数の帰属不明が再通知の間隔（60 分）のあいだ黙る。
    /// </para>
    /// </summary>
    IReadOnlyList<ProtectiveStopOrder> FindUnattributedNotified(int limit);
}
