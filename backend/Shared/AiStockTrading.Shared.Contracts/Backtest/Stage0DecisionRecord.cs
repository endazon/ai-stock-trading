using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Backtest;

// FR-04, FR-15, ADR-0033 決定2/決定4, IADR-0318: Stage 0 の**記録・再生方式**で受け渡す記録の契約。
//
// ADR-0033（2026-09-05 の利用者裁定）は Stage 0 の評価対象を「取引判断サービスの AI 判断そのもの」と定め、
// 記録（LLM を呼ぶ側）と再生（決定的なシミュレーション側）を分けた。本ファイルはその**境界の契約**である。
//   - 記録する側: TradeDecisionService（LLM 客・プロンプト・費用計測が既にそこにある）
//   - 再生する側: BacktestService（`IBacktestStrategy` は純関数。IADR-0043 の契約を覆さない）
// 両サービスは互いを参照できないため、契約は共有プロジェクトに置く（サービス間の直接参照は禁止）。

/// <summary>
/// FR-04, ADR-0033 決定1: 記録に残す判断の行動。
/// <para>
/// <c>TradeDecisionService.Domain.TradeAction</c> と同じ 3 値だが、**契約側で独立に定義する** ——
/// 契約プロジェクトはサービスの Domain へ依存できず、依存させれば再生側（BacktestService）が
/// 取引判断サービスの Domain を引くことになる。
/// </para>
/// </summary>
public enum Stage0DecisionAction
{
    Hold,
    Buy,
    Sell,
}

/// <summary>
/// FR-04, FR-11, ADR-0003, ADR-0033 決定4: **1 回分の生の判断**。
/// <para>
/// 🔴 ADR-0033 決定4 は「記録には多数決の結果だけでなく**各回の生の判断も残す**」と定める
/// （ADR-0003 の全量ログの規律）。判断の割れ方は Stage 0 の成績のばらつきを説明する一次情報であり、
/// 多数決結果だけを残すと、成績が LLM の非決定性で揺れたのか記録の質で揺れたのかを事後に切り分けられない。
/// </para>
/// </summary>
/// <param name="Attempt">1 始まりの試行番号（多数決の何回目か）。</param>
/// <param name="Unparseable">
/// 構造化出力を解析できなかったか（#290 / IADR-0248 の区別）。true のとき <see cref="Action"/> は
/// 安全既定の Hold であり、**「LLM が見送りを選んだ」のではない**。
/// </param>
/// <param name="EffectiveModelId">
/// FR-15, ADR-0054 決定3, #1196, IADR-0498: この票（本判断 `trade-decision`）に**応答したモデル**（応答が名乗った実効モデル。
/// 構成の希望値ではない）。計測が無い（送信できなかった等）ときは null ＝**不明**であり、再生側はピンと一致したと読まない。
/// 既定 null は二段化より前の記録の形である。
/// </param>
public sealed record Stage0RawDecision(
    int Attempt,
    Stage0DecisionAction Action,
    string Rationale,
    decimal ReferencePrice,
    decimal StopLossDistancePerShare,
    int InputTokens,
    int OutputTokens,
    bool Unparseable,
    string? EffectiveModelId = null);

/// <summary>
/// FR-04, FR-15, ADR-0054 決定3, #1196, IADR-0498: **一次スクリーニング（`trade-decision-screening`）の判断**。
/// <para>
/// 本番は一次で関心なし（Hold）・解析不能なら本判断を呼ばない（`DecisionOrchestrator`）。Stage 0 は本番と同じ二段を通した
/// 判断を評価する（ADR-0054 決定3）ため、記録は一次の結果と一次に応答したモデルを本判断と**別に**持つ。
/// </para>
/// </summary>
/// <param name="Action">一次の方向（Hold は見送り＝本判断へ進まない）。解析不能のときは安全既定の Hold。</param>
/// <param name="Unparseable">構造化出力を解析できなかったか（#290 / IADR-0248 の区別）。true も本判断へ進まない。</param>
/// <param name="EffectiveModelId">一次に応答したモデル（応答が名乗った実効モデル）。null は不明。</param>
public sealed record Stage0ScreeningDecision(
    Stage0DecisionAction Action,
    bool Unparseable,
    string Rationale,
    int InputTokens,
    int OutputTokens,
    string? EffectiveModelId)
{
    /// <summary>本判断へ進んだか（関心あり＝Buy/Sell かつ解析できた）。本番の `ParsedScreening.IsInterested` と同じ規則。</summary>
    public bool Interested => !Unparseable && Action != Stage0DecisionAction.Hold;
}

/// <summary>
/// FR-04, FR-15, ADR-0033 決定2: **1 判断時点分**の記録（銘柄 × AsOf）。
/// </summary>
/// <param name="AsOf">判断時点（その日の終値までの情報だけで判断した日）。再生はこの日付で記録を引く。</param>
/// <param name="InputFingerprint">
/// 入力の指紋（プロンプト全文の SHA-256）。**プロンプト本文そのものは載せない** ——
/// 保有ポジション・資金残枠等の機微を含み、記録は再生のために配布されるためである
/// （全量の本文は FR-11 の LLM ログが <c>LlmGateway:LogPrompts</c> で担う。IADR-0061 決定1）。
/// 同じ入力で記録し直したかを突き合わせる用途に限る。
/// </param>
/// <param name="SignedQuantity">
/// 多数決結果に対応する目標注文数量（+ 買い / − 売り / 0 は見送り）。再生はこの値を
/// <c>BacktestOrder</c> へ写す（再生側でサイジングを再計算しない＝決定性を保つ）。
/// FR-10, #1209, IADR-0506: ただし再生の時点で新規建てになる注文は、本番と同じ 2 統制（最小の名目額・判断由来の決済の後の
/// 同日・同方向）に当たれば写さない（数量は変えずに見送る）。
/// </param>
/// <param name="CostJpy">この判断時点で実際に発生した LLM 費用（円）。多数決の全回分の合計。</param>
/// <param name="AsOfInputs">
/// FR-15, ADR-0036 決定1, #749, IADR-0387: **as-of 入力の再構成可否の申告**（必須の 3 種すべてを覆うこと。
/// FR-04, ADR-0044 決定 3, #1034: 監視銘柄の節を含む記録は (e) 当時の監視銘柄も申告する。`Stage0AsOfInputs.DeclarableKinds`）。
/// <para>
/// 🔴 **`null` は「未申告」であり「すべて再構成できた」ではない。** 未申告の記録は判定を組ませない
/// （`Stage0ReplayEvaluation` が `InputCompletenessNotDeclared` で遮断する）。既定を `null` にしているのは、
/// **旧記録・手書きの記録が黙って充足側へ倒れないようにする**ためである（fail-closed 側の既定）。
/// </para>
/// <para>
/// 再構成できなかった項目を 1 つでも持つ判断は、**Stage 0 の判定母集団から除かれる**（ADR-0036 決定1）。
/// 除くのは合否の集計からであって、記録することそのものは止めない（同決定「『外す』は『走らせない』ではない」）。
/// </para>
/// </param>
/// <param name="Screening">
/// FR-15, ADR-0054 決定3, #1196, IADR-0498: **一次スクリーニングの判断**。一次で見送ったときは <see cref="RawDecisions"/> が空
/// （本判断を呼んでいない）。
/// <para>
/// 🔴 **`null` は「一次を記録していない」（二段化より前の記録）であり「一次を通過した」ではない。** その記録で組んだ評価は、
/// 本番の二段の系を測っていない（ADR-0054 決定3）ため、再生側は**評価不能**として判定を組まない（合格にも不合格にも数えない）。
/// 既定を `null` にしているのは、旧記録・手書きの記録が黙って二段の記録へ倒れないようにするためである（<see cref="AsOfInputs"/> と同じ向き）。
/// </para>
/// </param>
/// <param name="EntryBelowMinimumNotional">
/// FR-10, #1176, IADR-0495 決定1, #1209, IADR-0506: 記録器が、この判断を<b>新規建てとして</b>発注すれば名目額（数量 × 参照価格・基準通貨）が
/// 最小の名目額（equity × <c>Sizing:MinEntryNotionalRatio</c>。既定 1%）に満たないと判定したか。本番の判定（サイジングの直後・
/// 理由 <c>SizedBelowMinimumNotional</c>）と同じ関数・同じしきい値の構成で判定する。
/// <para>
/// 🔴 <b>数量（<see cref="SignedQuantity"/>）は 0 にしない。</b>記録器は保有を知らず（保有なしの枝だけを記録する）、再生ではこの注文が
/// 建玉の決済として働くことがある。本番は決済に名目額の判定を掛けないため、適用は建玉を知る再生側が新規建てにだけ行う。
/// </para>
/// <para>
/// <c>null</c> は「判定していない」（Hold・数量 0・本項目より前の記録）。再生は null の記録に名目額の判定を掛けない。
/// </para>
/// </param>
public sealed record Stage0DecisionRecord(
    string Symbol,
    Market Market,
    DateOnly AsOf,
    string InputFingerprint,
    string ModelId,
    int VoteCount,
    IReadOnlyList<Stage0RawDecision> RawDecisions,
    Stage0DecisionAction MajorityAction,
    string MajorityRationale,
    int SignedQuantity,
    decimal CostJpy,
    int InputTokens,
    int OutputTokens,
    IReadOnlyList<Stage0AsOfInputStatus>? AsOfInputs = null,
    Stage0ScreeningDecision? Screening = null,
    bool? EntryBelowMinimumNotional = null);

/// <summary>記録集合が対象とした銘柄。</summary>
public sealed record Stage0RecordedSymbol(string Symbol, Market Market);

/// <summary>
/// FR-15, FR-20, ADR-0033 決定2/決定3, IADR-0281: **記録集合**。再生側はこの単位で受け取り、
/// 期間・銘柄・カットオフ日が評価の構成と整合するかを検査してから評価へ進む。
/// </summary>
/// <param name="LlmTrainingCutoff">
/// 記録に用いた LLM 学習カットオフ日（ADR-0033 決定3）。**再生側の構成値と一致しなければ評価しない** ——
/// 別のカットオフ前提で採った記録を、いま構成されているカットオフの検証結果として使えば、
/// 検証条件①（汚染対策）が確認されていないのに満たしたことになる。
/// </param>
/// <param name="StrategyId">
/// 戦略の同一性（IADR-0281 決定3 の「戦略の変更」を機械判定する鍵）。
/// <see cref="Stage0StrategyIdentity"/> が記録の**内容から**導出する。
/// </param>
public sealed record Stage0DecisionRecordSet(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<Stage0RecordedSymbol> Symbols,
    DateOnly LlmTrainingCutoff,
    DateTimeOffset CreatedAt,
    string ModelId,
    string StrategyId,
    IReadOnlyList<Stage0DecisionRecord> Records);
