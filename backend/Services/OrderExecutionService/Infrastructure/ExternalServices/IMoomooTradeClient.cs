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

// SDK 非依存の発注リクエスト（既定はマーケタブルリミット。SIMULATE は実装側で固定）。
// #141, IADR-0092: Remark は client order id相当（DecisionId）。滞留 Reserved を後から DecisionId で照合するために
// ブローカ注文へ紐づける。null/空なら付与しない（従来挙動）。
// FR-10, #331, IADR-0210: Kind=Stop は保護逆指値（TriggerPrice=発火価格・OrderType_Stop＋AuxPrice）、
// Kind=Market は成行（逆指値が成立しない場合の建玉解消）。Stop/Market では Price を注文へ載せない
// （Stop は発火後成行・Market は板成行であり、指値を送ると意味が変わる）。
public sealed record MoomooOrderRequest(
    string Symbol,
    MoomooMarket Market,
    MoomooSide Side,
    int Quantity,
    decimal Price,
    string? Remark = null,
    MoomooOrderKind Kind = MoomooOrderKind.Limit,
    decimal? TriggerPrice = null);

// FR-10, #331, IADR-0210: 注文種別（SDK 非依存）。Limit=OrderType_Normal / Stop=OrderType_Stop / Market=OrderType_Market。
public enum MoomooOrderKind { Limit, Stop, Market }

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
    Failed,
}
