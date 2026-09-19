using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-05, FR-10, ADR-0002（OpenD 常駐・SPOF・INDEX 決定 33「再起動中は発注不可」）, #331, IADR-0211:
// 承認済み注文を**発注せずに見送った**。注文はブローカーに存在しないため注文状態
// （OrderStatus。受付・約定・失注・取消・拒否）を持たず、本イベントが唯一の記録である。
//
// - **キューイングしない**: 見送った注文は破棄され、自動では再発注されない（再発注は次の取引判断からのみ）。
// - **「拒否」と別集計**: OrderStatus.Rejected は「証券会社が受理しなかった状態」であり（FR-05・planning#60）、
//   見送り（届いてすらいない）を混ぜると集計が接続障害で汚染される。監査台帳の EventType も別になる。
public record OrderDispatchForgone(
    Guid DecisionId,
    OrderIntent Intent,
    OrderDispatchForgoneReason Reason,
    DateTimeOffset OccurredAt);

// #331, IADR-0210 決定1 / IADR-0211: 見送りの理由。いずれも**発注前**に確定する。
public enum OrderDispatchForgoneReason
{
    /// <summary>ブローカー（OpenD）へ到達できない（接続確立の失敗＝確実に未発注）。ADR-0002/0024 の SPOF。</summary>
    BrokerUnavailable,

    /// <summary>Open 注文に損切り価格（StopLossPrice）が無い。逆指値を張れない建玉は持たない（FR-10）。</summary>
    StopLossPriceMissing,

    /// <summary>ブローカーが逆指値の発注能力（IProtectiveOrderBroker）を持たない。同上（fail-closed）。</summary>
    StopOrderUnsupported,

    /// <summary>
    /// FR-10, ADR-0040 決定1, #819, IADR-0342 決定4: 承認が S0 以外の損切り実行機構を運んでいるのに、
    /// 発注先が moomoo SIMULATE ではない（実弾・内蔵 paper）。<b>S0 以外は SIMULATE でしか選べない</b>ため、
    /// 読み替えずに発注しない（fail-closed）。<b>末尾へ追加する</b>（序数 3。メトリクスのタグ・監査 payload の整数が往来する）。
    /// </summary>
    StopLossMethodNotPermitted,

    /// <summary>
    /// 🔴 FR-10, FR-05, ADR-0016, #864, IADR-0355 決定2: 決済（Close）だが、<b>ブローカーに決済方向の建玉が
    /// 1 株も無い</b>。台帳の建玉は実在せず（#849 の乖離）、送れば<b>保有 0 からの売り＝裸の新規ショート</b>になる。
    /// 空売りは方針で禁止であり、決済として通る注文にはショート建玉の規律も空売り統制も効かない。
    /// <b>末尾へ追加する</b>（序数 4。メトリクスのタグ・監査 payload の整数が往来する）。
    /// </summary>
    BrokerPositionAbsent,

    /// <summary>
    /// 🔴 FR-10, FR-05, ADR-0016, #864, IADR-0355 決定3: 決済（Close）だが、<b>ブローカーの建玉を照会できない
    /// （null＝不明）</b>。空列（建玉ゼロ）と取り違えず、不明のままでは送らない（fail-closed）。
    /// 選ばなかった側（不明でも送る）の害は IADR-0355 決定3 に明記した。<b>末尾へ追加する</b>（序数 5）。
    /// </summary>
    BrokerPositionsIndeterminate,

    /// <summary>
    /// FR-10, ADR-0040 決定1（S1）, #820 の 8 巡目監査, IADR-0344 追記(8) 決定4・追記(9) 決定1・追記(11) 決定1:
    /// S1 で新規建てを武装しようとしたが、同一銘柄・同方向に<b>帰属不明の建玉</b>がある、
    /// またはそれが無いことを確かめられない（建玉を照会できない）。
    /// <para>
    /// 判定は <b>純額 − 主張 &gt; 0</b>。主張は Active な保護記録が<b>その巡回で実際に動かせる株数</b>
    /// （帳簿の主張ではない＝追記(9) 決定1）を合計した値であり、🔴 <b>建玉照会の「前」と「後」の両方で読んだ
    /// 小さい方</b>を採る（追記(11) 決定1）——照会は発注先への往復であり、その待ちのあいだに主張は
    /// <b>増える側にも減る側にも動く</b>ため、片方の時点だけを採ると逆側の窓で帰属不明が過少に読まれる。
    /// </para>
    /// <para>
    /// S1 の行が守る株数はブローカーの<b>純額</b>からしか測れず、他人の建玉（S2・人手・S0 の発注窓）と
    /// 自分の建玉を区別できない。帰属不明の建玉がある銘柄で武装すると、その建玉を
    /// <b>S1 の損切りラインで売る</b>（監査が実測）。「保護レグを張れない Open では建玉を持たない」
    /// （IADR-0210 決定1）に合わせ、<b>建玉を持たずに見送る</b>。<b>末尾へ追加する</b>（序数 6。
    /// 🔴 #864 が序数 4・5 を先に取ったため、本値は 4 から 6 へ繰り下げた——**先にマージされた側が確保する**）。
    /// </para>
    /// </summary>
    UnattributedPosition,
}
