namespace OrderExecutionService.Infrastructure.ExternalServices;

// #13, FR-05, ADR-0002: moomoo 取引の薄いポート（SDK 非依存）。写像・状態変換・fail-safe は MoomooBrokerAdapter に集約し、
// 本ポートの実装（MMApiMoomooTradeClient）へ protobuf/コールバックの SDK 固有配線を隔離する。
// 実装は SIMULATE（TrdEnv_Simulate）で OpenD へ接続する（実弾は撃たない・IADR-0016）。
public interface IMoomooTradeClient
{
    Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default);

    Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default);

    Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default);

    // #141, IADR-0092: Reserved 滞留の実照会。発注時に伝播した clientOrderId（remark）で SIMULATE 口座の現在＋履歴注文を
    // 照合し、一致した注文のスナップショットを返す。
    //
    // 契約（fail-safe の要）:
    //   - 一致注文あり → その <see cref="MoomooOrderSnapshot"/> を返す。
    //   - 全対応市場の現在＋履歴を**成功裏に列挙**して一致ゼロ → null（＝確実に未発注）。
    //   - 照会失敗（接続不達・応答異常・部分列挙）→ **例外を送出**する（null を返してはならない）。
    // 呼び出し側（MoomooReservationBrokerProbe）は例外を Indeterminate に倒す。「不明」を null と取り違えると
    // 誤 NotPlaced＝二重発注を招くため、この区別が本メソッドの中核である。
    // <paramref name="reservedAtUtc"/> は履歴照会窓の下限に用いる（窓外の発注済み注文を見落とさないため）。
    Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
        string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default);

    // #292, IADR-0118: SIMULATE 口座の現在建玉を全対応市場について列挙する。
    //
    // 契約（fail-safe の要）:
    //   - 全市場を**成功裏に列挙**できた → その一覧（建玉が無ければ空列）。
    //   - いずれかの市場の照会が失敗（不達・応答異常）→ **例外を送出**する（部分列挙を返してはならない）。
    // 呼び出し側（MoomooBrokerAdapter）は例外を null（＝不明）へ倒す。部分列挙を「全部」と誤ると、
    // 列挙できなかった市場の建玉がすべて乖離として報告される。
    Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default);

    // #375, ADR-0021 決定3: 接続している口座の種別（TrdAcc.AccType）。
    //
    // 契約（fail-safe の要）:
    //   - 種別が判明した → Cash / Margin
    //   - **種別が不明**（TrdAccType_Unknown・TFSA 等の未対応値）→ null
    //   - 照会失敗（不達・応答異常）→ **例外を送出**する（null を返してはならない）
    // 呼び出し側（MoomooBrokerAdapter）は例外も null も「口座種別を確認できていない」へ倒す。
    // **「不明なら信用口座」を返してはならない**——現金口座で GFV 回避ガードが無効のまま回る事故になる。
    Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken cancellationToken = default);
}

// #375, ADR-0021: SDK 非依存の口座種別（TrdAccType の写像）。本システムが扱うのは 2 種のみである（決定2）。
public enum MoomooAccountType { Margin, Cash }

// #292, IADR-0118: SDK 非依存の建玉スナップショット。Quantity は符号付き（+ ロング / − ショート）。
public sealed record MoomooPositionSnapshot(
    string Symbol,
    MoomooMarket Market,
    int Quantity,
    decimal AverageCost);

// #827, IADR-0118: 建玉照会（TrdGetPositionList）の応答 1 行を、照会したヘッダの市場と組にした SDK 非依存の表現。
// PositionTrdMarket は応答行が持つ市場（TrdMarket の値。未設定なら null）。Quantity は moomoo と同じく非負で、
// 方向は IsShort が持つ。SIMULATE はヘッダ市場を問わず同じ建玉を返すため、組にしないと二重計上を判別できない。
// PositionId は応答行の PositionID（HasPositionID が偽なら null）。重複排除の単位をロットにするために持つ。
public sealed record MoomooPositionRow(
    int QueriedTrdMarket,
    int? PositionTrdMarket,
    string Code,
    bool IsShort,
    int Quantity,
    decimal CostPrice,
    ulong? PositionId = null);

// SDK 非依存の発注リクエスト（既定はマーケタブルリミット。SIMULATE は実装側で固定）。
// #141, IADR-0092: Remark は client order id相当（DecisionId）。滞留 Reserved を後から DecisionId で照合するために
// ブローカ注文へ紐づける。null/空なら付与しない（従来挙動）。
// FR-10, #331, IADR-0210: Kind=Stop は保護逆指値（TriggerPrice=発火価格・OrderType_Stop＋AuxPrice）、
// Kind=Market は成行（逆指値が成立しない場合の建玉解消）。Stop/Market では Price を注文へ載せない
// （Stop は発火後成行・Market は板成行であり、指値を送ると意味が変わる）。
// FR-10, #821, IADR-0347: Kind=StopLimit は S3 のストップリミット（TriggerPrice=発火価格＝AuxPrice・Price=指値）、
// Kind=TrailingStop は S3 のトレーリングストップ（TrailValue=トレール幅の絶対額・TrailType_Amount）。
public sealed record MoomooOrderRequest(
    string Symbol,
    MoomooMarket Market,
    MoomooSide Side,
    int Quantity,
    decimal Price,
    string? Remark = null,
    MoomooOrderKind Kind = MoomooOrderKind.Limit,
    decimal? TriggerPrice = null,
    decimal? TrailValue = null);

// FR-10, #331, IADR-0210: 注文種別（SDK 非依存）。Limit=OrderType_Normal / Stop=OrderType_Stop / Market=OrderType_Market。
// FR-10, #821, IADR-0347: S3 の代替種別を末尾へ足す。StopLimit=OrderType_StopLimit / TrailingStop=OrderType_TrailingStop。
public enum MoomooOrderKind { Limit, Stop, Market, StopLimit, TrailingStop }

// FR-10, FR-11, #821, IADR-0347: OpenD が非成功（retType != 0）を返したことを表す。
//
// 🔴 **retType / retMsg を構造として保つことが本型の存在理由である。** 従来は文字列へ畳んだ
// InvalidOperationException であり、拒否理由はログにしか残らなかった。S3（#821）は「拒否理由を監査台帳へ残すこと」
// 自体が目的であるため、アダプタが理由を取り出して戻り値へ載せられる必要がある。
// **InvalidOperationException 派生のまま**にしてあるのは、既存の捕捉・表明（アダプタの fail-safe・テスト）を
// 1 行も変えずに済ませるためである（メッセージ文字列も従来と同一）。
public sealed class MoomooTradeRequestException(string operation, int retType, string retMsg)
    : InvalidOperationException($"moomoo {operation} が失敗しました（retType={retType}）: {retMsg}")
{
    /// <summary>失敗した OpenD 操作の名（PlaceOrder / CancelOrder 等）。</summary>
    public string Operation { get; } = operation;

    /// <summary>moomoo の retType（RetType_Succeed=0 以外）。</summary>
    public int RetType { get; } = retType;

    /// <summary>moomoo の retMsg（ブローカーが返した拒否理由の原文）。</summary>
    public string RetMsg { get; } = retMsg;
}

public enum MoomooMarket { Japan, UnitedStates }

public enum MoomooSide { Buy, Sell }

// SDK 非依存の注文結果。State は moomoo の注文状態を正規化したもの。
public sealed record MoomooOrderResult(
    string OrderId,
    MoomooOrderState State,
    int FilledQuantity,
    decimal AveragePrice);

// #141, IADR-0092: remark 照合で見つけた注文のスナップショット（SDK 非依存）。滞留 Reserved の終端化に必要な
// 注文実体（銘柄・売買・数量・価格・状態・約定）を持ち、プローブが BrokerOrder（と OrderIntent）へ再構成する。
// PlacedAt/CompletedAt は moomoo の作成/更新時刻（取得できなければ null）。
public sealed record MoomooOrderSnapshot(
    string OrderId,
    MoomooOrderState State,
    string Symbol,
    MoomooMarket Market,
    MoomooSide Side,
    int Quantity,
    decimal Price,
    int FilledQuantity,
    decimal AveragePrice,
    DateTimeOffset? PlacedAt,
    DateTimeOffset? CompletedAt);

// moomoo の注文状態（OrderStatus.* へ写像するための正規化列挙）。
public enum MoomooOrderState
{
    Submitting,
    Submitted,
    Filling,
    FilledPart,
    FilledAll,
    Cancelled,

    /// <summary>
    /// <b>確認できた失敗</b>（OpenD の SubmitFailed / Failed / Disabled / Deleted）。証券会社が受理しなかった
    /// ことが分かっている状態であり、<c>OrderStatus.Rejected</c> へ写る。
    /// </summary>
    Failed,

    /// <summary>
    /// 🔴 FR-05, FR-10, UC-06, #848, IADR-0117（2026-09-19 追記・改定 3）: <b>状態が不明</b>
    /// （OpenD の NONE〈-1〉・TIMEOUT〈4〉・本実装が知らない新コード）。<b><c>Failed</c> と混ぜない。</b>
    /// <para>
    /// 混ぜると <c>OrderStatus.Rejected</c>（＝終端）へ畳まれ、リスク管理の取引台帳が
    /// <b>状態が不明なまま建玉の押さえを解く</b>（二重決済で意図しないショート化）。#848 以前は
    /// 「台帳に約定を載せないだけ」で無害だったが、拒否が在庫解放の引き金になった時点で
    /// <b>fail-safe の向きが反転した</b>。不明は非終端（<c>OrderStatus.Accepted</c>）へ写し、
    /// 約定追跡（<c>OrderFillPoller</c>）に引き直させて本当の状態へ解決させる。
    /// </para>
    /// </summary>
    Unknown,
}
