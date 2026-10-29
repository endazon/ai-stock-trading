using AiStockTrading.Shared.Contracts.Events;
using Wolverine;
using Wolverine.Runtime;

namespace TradeDecisionService.Infrastructure.Steps;

// 🔴 FR-02, NFR-02, #1485, IADR-0593 決定1・2: 定時判断（TradeDecisionMade）を**判断した時点で**送る発行口。
//
// Wolverine はハンドラの中で発行したメッセージ（引数の IMessageBus＝そのメッセージの文脈）を**ハンドラが成功で終わってから**まとめて送る。
// 定時サイクルは 1 通で全銘柄を順に判断するため、1 銘柄目の判断も巡回の末尾まで下流（審査・発注）へ届かず、判断の時点の価格で作った
// 指値が古くなった（PoC 2026-10-27: 判断→承認 35〜125 秒）。ここはランタイムから作る MessageBus（ハンドラの文脈に乗らない）で送り、
// 発行を即時にする（取引判断の失敗の報告口・照会の健全性の報告口と同じ作法）。
//
// 🔴 冪等（IADR-0490 決定2）は変えない: DecisionId は (起点イベント, 市場, 銘柄) から決定的に導いたものを送る。打ち切られた試行の判断は
// もう捨てられない（送った後でサイクルが打ち切られ得る）ので、**同じプロセスで送った DecisionId を覚え**、再試行の巡回はその銘柄を
// 判断し直さない（LLM を二重に呼ばない・二重に送らない）。別のプロセスへの再配送（Pod の再起動）は下流の DecisionId の冪等
// （IADR-0407 / IADR-0529 / IADR-0057）が止める。
//
// singleton（覚えた DecisionId を巡回・試行をまたいで持つ）。覚える数は上限で切る（古いものから忘れる。再試行は同じ配信の中の数分である）。
public sealed class ScheduledDecisionPublisher
{
    /// <summary>覚える DecisionId の上限（巡回あたり判断対象 12 件の前提で、数十巡回ぶん）。</summary>
    public const int RememberedCapacity = 1024;

    private readonly Func<TradeDecisionMade, ValueTask> _send;
    private readonly HashSet<Guid> _published = [];
    private readonly Queue<Guid> _order = new();
    private readonly Lock _gate = new();

    public ScheduledDecisionPublisher(IWolverineRuntime runtime)
        : this(decision => new MessageBus(runtime).PublishAsync(decision))
    {
        ArgumentNullException.ThrowIfNull(runtime);
    }

    public ScheduledDecisionPublisher(Func<TradeDecisionMade, ValueTask> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
    }

    /// <summary>この DecisionId の判断をこのプロセスで既に送ったか（再試行の巡回が判断し直さないために読む）。</summary>
    public bool AlreadyPublished(Guid decisionId)
    {
        lock (_gate)
            return _published.Contains(decisionId);
    }

    /// <summary>
    /// 判断を今すぐ送る。<paramref name="remember"/> が真なら、送れた後に DecisionId を覚える（決定的に導けた DecisionId だけを覚える）。
    /// 送信の例外は呼び出し側（銘柄ごとの catch）へ伝える（覚えない＝次の試行・巡回で判断し直す）。
    /// </summary>
    public async Task PublishAsync(TradeDecisionMade decision, bool remember)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await _send(decision).ConfigureAwait(false);
        if (!remember)
            return;

        lock (_gate)
        {
            if (!_published.Add(decision.DecisionId))
                return;
            _order.Enqueue(decision.DecisionId);
            while (_order.Count > RememberedCapacity)
                _published.Remove(_order.Dequeue());
        }
    }
}
