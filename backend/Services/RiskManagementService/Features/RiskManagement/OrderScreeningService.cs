using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-19, FR-20, UC-01, UC-02, ADR-0003: 取引判断（TradeDecisionMade）を発注前に決定的に検証し、
// OrderApproved / OrderRejected を生成する。判定コア RiskEvaluator（ステートレス）に、ホストが保持する
// ロックアウト状態（IADR-0008）を合成する。日次損失上限の新規到達でロックアウトを設定し、含み損が回復しても
// 当日中（翌営業日の解除まで）は新規建てを止め続ける。手仕舞い（Close）はフェイルセーフで常に通す。
//
// FR-10, #428, IADR-0163 決定2: **推定台帳（buyInInferences）は必須依存である。**
// 省略可能引数（既定 `null`）で受けていると、`Program.cs` から引数を削っても**コンパイルが通りテストは
// 全緑のまま強制買戻し由来の 30 日禁止だけが静かに効かなくなる**（既存テストは本サービスを直接構築するため
// 配線の消失を検知しない）。**不在が統制の無効を意味する依存は必須にする**——同じ規律で
// `BrokerPositionsObservedHandler` は既に依存を必須にしている（IADR-0159）。
// **`patternDetector` は省略可能のままである**——「検出器を構成していない」は正当な状態であり、
// `null` の意味が違う（推定台帳の `null` は「30 日禁止が効かない」を意味する）。
//
// FR-10, #935, IADR-0394 決定7: **取引台帳（ledger）も同じ理由で必須依存である。** 損切りした銘柄の同日・同方向の
// 新規建てを止める統制の入力（決済の承認と由来）を読む。省略可能にすると、`Program.cs` から外しても
// コンパイルが通り、その統制だけが静かに効かなくなる（2026-09-23 の買い直しが戻る）。
//
// FR-10, ADR-0016 決定2(a)・決定3・決定9, #967, IADR-0425 決定5・7: **空売り文脈の供給（shortSellContexts）も必須依存である。**
// 新規の売り建ての審査で空売り文脈（借株可否・エクスポージャ）を組み、判定コアへ渡す。不在は「文脈なし＝拒否」へ倒れて
// 安全側ではあるが、10% / 50% の上限が**一度も評価されない状態**（#967 の起点）へ黙って戻る——省略可能にしない。
// 供給元（借株可否の照会）はネットワークを渡るため審査は非同期（ScreenAsync）であり、**同期の入口は持たない**
// （供給元を通らない審査の経路を作らない）。
public sealed class OrderScreeningService(
    IRiskSettingsStore settingsStore,
    PortfolioSnapshotBuilder snapshotBuilder,
    ILockoutStore lockoutStore,
    IClock clock,
    IBusinessCalendar businessCalendar,
    IBuyInInferenceStore buyInInferences,
    IPortfolioLedgerStore ledger,
    ShortSellContextSupplier shortSellContexts,
    IManipulativeOrderPatternDetector? patternDetector = null)
{
    public async Task<ScreeningOutcome> ScreenAsync(TradeDecisionMade decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var intent = decision.Intent;
        var isEntry = intent.PositionEffect == PositionEffect.Open;

        // FR-10, #832, IADR-0407: **承認済みの新規建ての判断の再配送は再審査しない。**
        // 台帳の承認行は自分の OrderApproved の射影であり、行があれば同じ判断の承認を発行済みである。
        // 再審査すると、射影済みの自分自身が未終端の新規建て（IADR-0346）として保有建玉数・日次枠・段階資金へ算入され、
        // 承認済みの判断が拒否へ反転する（同じ DecisionId の OrderRejected が監査・通知へ流れ、審査メトリクスも二重に刻まれる）。
        // **新規建てに限る**——手仕舞い（Close）は再審査して承認を発行し直しても発注執行が DecisionId で止める一方、
        // 抑止すると最初の発行が届かなかった手仕舞いが出ない側へ倒れる（ADR-0009）。読み取りも新規建てだけにして、
        // 台帳の読み取りの失敗が手仕舞いの審査を新たに巻き込まないようにする（損切りの供給と同じ規律）。
        if (isEntry && ledger.FindApprovedPositionEffect(decision.DecisionId) is not null)
        {
            return ScreeningOutcome.ApprovedReplay(
                new OrderScreeningObservation(decision.DecisionId, intent.Mode, []));
        }

        // #337（#249 吸収）, IADR-0246: 日次損失ロックアウトの「当日」は**注文の市場の現地取引日**で解釈する。
        // JST 固定（clock.Today）では ET 10-11 時（セッション中）に日付が変わり、同一の米国セッションの
        // 途中でデイリーストップが解除されていた。導出は TradingDay.Of（単一情報源）。
        var tradingDay = TradingDay.Of(clock.UtcNow, intent.Market);
        var settings = settingsStore.GetCurrent();
        var snapshot = snapshotBuilder.Build();

        // FR-10, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159 決定5:
        // 強制買戻し由来の 30 日禁止を判定コアへ供給する。**空売り文脈が組めないとき（下の供給が null）でも**
        // 禁止期限だけは推定台帳から単独で供給できる（#967 で文脈の供給元は入ったが、借株可否が分からない間は文脈を組まない）。
        // 供給できない値（維持率・エクスポージャ）を 0 で埋めた偽の文脈は作らない（値を発明しない）。
        // #428, IADR-0163 決定2: 台帳は必須依存であり、供給は**常に**組む（禁止が無ければ BanUntil が null）。
        var banUntil = buyInInferences.GetBanUntil(intent.Symbol, intent.Market);
        var buyInBan = new BuyInBanSupply(clock.Today, banUntil);

        // FR-10, ADR-0016 決定2(a)・決定3・決定9, #967, IADR-0425 決定5: **新規の売り建て（空売り）のときだけ**空売り文脈を組む
        // （借株可否の照会はブローカーの枠を使うため、それ以外の注文では照会しない）。借株可否・エクスポージャのどちらかが
        // 分からなければ null であり、判定コアは BorrowUnavailable で拒否する（今と同じ）。判定日は注文の市場の現地取引日。
        var shortSellContext = isEntry && ShortSellEvaluator.IsShortEntry(intent)
            ? await shortSellContexts.SupplyAsync(intent, tradingDay, banUntil, cancellationToken).ConfigureAwait(false)
            : null;

        // FR-10, #935, IADR-0394: 当日の損切りの供給（無し／損切り済み／不明を方向ごとに）。
        // **新規建てのときだけ読む**——手仕舞い（Close）は判定対象外であり、台帳の読み取りの失敗が
        // 手仕舞いの審査を巻き込まないようにする（ADR-0009）。読み取りが例外で終われば新規建ての審査も例外で終わり、
        // 承認は出ない（fail-closed）。
        var stopOuts = isEntry
            ? StopOutProjection.Project(
                ledger.GetCloseApprovals(intent.Symbol, intent.Market, clock.UtcNow - StopOutProjection.Lookback),
                intent.Market,
                clock.UtcNow)
            : null;

        // 判定コア（決定的）を実行し、違反理由を集約する。
        var result = RiskEvaluator.Evaluate(
            intent, settings, snapshot, patternDetector,
            shortSellContext: shortSellContext, buyInBan: buyInBan, stopOuts: stopOuts);
        var reasons = new List<RejectionReason>(result.Reasons);

        // 日次損失上限に「新規到達」したら当日ロックアウトを設定する（翌営業日まで）。
        // RiskEvaluator は実現+含み損の合算で当該時点の到達を判定する（IADR-0008）。
        if (reasons.Contains(RejectionReason.DailyLossLimitReached))
        {
            EngageLockout(tradingDay);
        }
        else if (isEntry && IsLockedOut(tradingDay))
        {
            // ロックアウトは当日中維持する。含み損が回復して RiskEvaluator が到達と判定しなくても、
            // 一度到達した当日は翌営業日の解除まで新規建てを拒否し続ける（デイリーストップの趣旨）。
            // 手仕舞い（Close）は isEntry の短絡で本分岐に入らず、フェイルセーフで常に通す。失効した
            // ロックアウトの掃除（IsLockedOut 内の Clear）は次の新規建て評価時に走れば十分で、Close の
            // 可否には影響しないため、この短絡は意図どおり（掃除が遅れても状態は失効判定で無効化される）。
            reasons.Add(RejectionReason.DailyLossLimitReached);
        }

        // FR-20, FR-11, #387, IADR-0148 決定3: 段階ゲートの「統制違反 0 件」（クラス C 限定）を数えるための観測。
        // **承認でも拒否でも作る**——算入対象の発注先で審査が動いていること自体が「集計が供給されている」根拠であり、
        // 拒否だけを観測すると「違反 0 件」と「そもそも数えていない」を区別できない（#387 の fail-open）。
        // 発注先は**その注文が向いていた先**（intent.Mode）を用いる。クラス分けはここで行わない
        // （単一情報源は RejectionReasonClassification・集計は ControlViolationAggregation）。
        var observation = new OrderScreeningObservation(decision.DecisionId, intent.Mode, reasons);

        if (reasons.Count > 0)
        {
            return ScreeningOutcome.Reject(
                new OrderRejected(decision.DecisionId, intent, reasons, clock.UtcNow), observation);
        }

        // NFR-01, NFR-02, #689, IADR-0307: 取引サイクルの起点を判断から発注執行へ**そのまま**中継する
        // （統制の判定には一切使わない・審査時刻で上書きしない）。上書きすると審査より前の区間が消える。
        // FR-10, ADR-0040 決定1・決定3, #819, IADR-0342 決定3: **承認時点で有効な損切りの実行機構**を載せる。
        // 発注執行は承認が運ぶ値で保護レグを扱う（走行中の設定変更と承認の競合を避ける）。解釈（SIMULATE 限定・
        // 空売りの除外）は発注執行が行い、ここでは設定値をそのまま運ぶ——承認は「どの設定で承認したか」の記録でもある。
        return ScreeningOutcome.Approve(
            new OrderApproved(
                decision.DecisionId, intent, result.ApprovedQuantity, clock.UtcNow,
                decision.CycleTrigger, decision.CycleStartedAt, settings.StopLossMethod),
            observation);
    }

    // #249 / IADR-0246: 当日（tradingDay）は呼び出し側が注文の市場の現地取引日で解決して渡す。
    private bool IsLockedOut(DateOnly tradingDay)
    {
        var lockout = lockoutStore.Get();
        if (lockout is null)
        {
            return false;
        }

        // 翌営業日の解除日に達していれば失効させ、状態を掃除する。
        if (!lockout.IsActiveOn(tradingDay))
        {
            lockoutStore.Clear();
            return false;
        }

        return true;
    }

    private void EngageLockout(DateOnly tradingDay)
    {
        var existing = lockoutStore.Get();
        // 既に当日有効なロックアウトがあれば解除日を延長しない（同日中の重複到達で解除が先送りされるのを防ぐ）。
        if (existing is not null && existing.IsActiveOn(tradingDay))
        {
            return;
        }

        var releaseOn = businessCalendar.NextBusinessDay(tradingDay);
        lockoutStore.Set(new LockoutState(
            releaseOn,
            "日次損失上限到達により当日ロックアウト（翌営業日まで）",
            clock.UtcNow));
    }
}
