using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Application.Services;

// FR-10, FR-19, FR-20, UC-06, ADR-0003, ADR-0007, ADR-0008: リスク管理設定（ガード・上限・段階）の変更。利用者のみ（アクター・理由必須）。
// 変更は前後値つきで履歴に記録する（ガード設定は ADR-0007「変更は利用者のみ・変更履歴を記録」。上限は FR-10、段階は ADR-0008 が同じ規律を課す）。生成AI・自動処理は本サービスを
// 呼ばない（呼び出し側の権限はホスト層の Keycloak 認可で担保・Slice B）。
public sealed class RiskSettingsService(
    IRiskSettingsStore store,
    ISettingsChangeLog changeLog,
    IClock clock,
    IBrokerAccountObservationStore accountObservations)
{
    public RiskManagementSettings GetCurrent() => store.GetCurrent();

    /// <summary>
    /// FR-19, UC-06, ADR-0007: 取引ガードを変更する。
    /// <para>
    /// #375, ADR-0021 決定4-4: <b>現金口座では信用買い・空売りを選べない。</b> 口座が対応しない商品種別を
    /// 有効化しようとする要求は <see cref="ArgumentException"/>（HTTP 400）で拒否し、
    /// <b>設定を一切変更せず履歴も残さない</b>——拒否された要求を履歴に積むと、実際には起きていない変更が
    /// 監査上の事実になる（IADR-0141 / IADR-0151 決定2 と同じ規律）。
    /// </para>
    /// <para>
    /// 判定は<b>ブローカーへ照会した口座種別</b>で行う（決定3）。観測が無ければ<b>制限しない</b>——
    /// 設定の保存は発注そのものではなく、観測が無い状態で設定変更まで止めると
    /// 「統制を厳しくする方向の変更」も道連れに止まる。**発注側は同じ状態で新規建てを止めており**
    /// （<c>BrokerAccountTypeUnverified</c>）、安全性は発注側が担保する。
    /// </para>
    /// </summary>
    public void UpdateGuard(TradingGuardSettings guard, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(guard);
        RequireActorAndReason(actor, reason);
        ThrowIfUnsupportedByAccount(guard);

        var current = store.GetCurrent();
        Save(current with { Guard = guard }, current.Guard, guard, SettingsChangeType.Guard, actor, reason);
    }

    // #375, ADR-0021 決定4-4/決定5: 口座が対応しない商品種別（現金口座での信用買い・空売り）の有効化を弾く。
    // 「設定できてしまうが発注はできない」状態を作らない——計画は「**選べなくする**」と定めている。
    private void ThrowIfUnsupportedByAccount(TradingGuardSettings guard)
    {
        if (accountObservations.GetCurrent() is not { } account)
        {
            return;
        }

        var unsupported = guard.EnabledProductTypes
            .Where(p => !AccountTypePolicy.Supports(account.AccountType, p))
            .ToList();
        if (unsupported.Count > 0)
        {
            throw new ArgumentException(
                $"口座種別 {account.AccountType} では商品種別 {string.Join(" / ", unsupported)} を有効にできません"
                    + "（現金口座では株を借りられないため信用買い・空売りが成立しません。ADR-0021 決定4-4）。",
                nameof(guard));
        }
    }

    /// <summary>
    /// FR-10, SC-02, UC-06, #362, IADR-0151 決定2: リスク上限を変更する。
    /// <para>
    /// <b>値域は <see cref="RiskLimitBounds"/> が単独で決める</b>（画面とサーバで規則を二重に書かない）。
    /// 範囲外なら <see cref="ArgumentException"/> を投げ、<b>設定を一切変更せず履歴も残さない</b>——
    /// 拒否された要求を履歴に積むと、実際には起きていない変更が監査上の事実になる（IADR-0141 と同じ規律）。
    /// エンドポイントは事前に <see cref="RiskLimitBounds.Validate"/> を呼んで 400 の details を組み立てるが、
    /// ここでも検査するのは<b>呼び出し口が将来増えても不変条件を保つため</b>である。
    /// </para>
    /// </summary>
    public void UpdateLimits(RiskLimitSettings limits, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(limits);
        RequireActorAndReason(actor, reason);
        RiskLimitBounds.ThrowIfOutOfRange(limits);

        var current = store.GetCurrent();
        Save(current with { Limits = limits }, current.Limits, limits, SettingsChangeType.Limits, actor, reason);
    }

    public void UpdateStage(StageSettings stage, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(stage);
        RequireActorAndReason(actor, reason);

        // FR-20, UC-06, #434, IADR-0161 決定3 / IADR-0163: 書き込み経路は allow-list を通す。
        //
        // IADR-0161 は発注先の読み書きを**意図的に非対称**に設計した——読み取りは黙って内蔵 paper へ
        // 倒し（例外にすると設定行全体が失われ、リスク判定そのものが止まる）、書き込みは拒否する
        // （黙って倒すと「実弾を選んだのに paper になった」という**説明のつかない状態遷移**が生まれる）。
        // `PUT /settings/broker-provider` は `BrokerProviderChange.Evaluate` がこれを担うが、
        // **段階の既定発注先（`Stage.Mode`）は同じ `BrokerProvider` 型でありながら素通りしていた**。
        //
        // 検証は **`BrokerProviderResolution.IsKnown`**（どの値が既知かの単一情報源）を通す。
        // ここに独自の判定を書くと allow-list が 2 か所になり、#434 と同型の穴が再発する。
        //
        // **エンドポイントではなくサービスに置く。** 本件はまさに「一方の経路にだけ関門があった」ことが
        // 問題であり、関門は全経路の下流に置かなければ意味がない（`UpdateGuard` と同じ形）。
        if (!BrokerProviderResolution.IsKnown(stage.Mode))
        {
            throw new ArgumentException(
                "段階の既定発注先は 0=内蔵 paper / 1=moomoo REAL / 2=moomoo SIMULATE のいずれかを指定してください。",
                nameof(stage));
        }

        var current = store.GetCurrent();
        // FR-20, #334, IADR-0140: 段階だけを差し替える。**発注先（BrokerProvider）には触れない**——
        // 2 軸は独立であり、段階の変更が発注先を自動で動かしてはならない（`with` が他プロパティを保つ）。
        Save(current with { Stage = stage }, current.Stage, stage, SettingsChangeType.Stage, actor, reason);
    }

    /// <summary>
    /// FR-20, FR-13, SC-02, #334, IADR-0140 / IADR-0141: 発注先（Broker Provider）を変更する。
    /// <para>
    /// 受理条件は <see cref="BrokerProviderChange.Evaluate"/> が単独で決める（画面とサーバで規則を二重に
    /// 書かない）。<b>受理しない場合は設定を一切変更せず、履歴も残さない</b>——拒否された要求を履歴に
    /// 積むと、実際には起きていない変更が監査上の事実になる。
    /// </para>
    /// <para>
    /// <b>段階には触れない。</b>発注先の変更が段階を自動で動かしてはならない（2 軸の独立）。
    /// </para>
    /// </summary>
    /// <returns>判定結果（受理可否・拒否理由の全件・実弾切替か・段階ゲートを飛ばすか）。</returns>
    public BrokerProviderChangeAssessment UpdateBrokerProvider(BrokerProviderChangeRequest request, string actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var current = store.GetCurrent();
        var assessment = BrokerProviderChange.Evaluate(request, current.Stage);
        if (!assessment.Accepted)
        {
            return assessment;
        }

        Save(
            current with { BrokerProvider = request.Target },
            current.BrokerProvider,
            request.Target,
            SettingsChangeType.BrokerProviderChanged,
            actor,
            request.Reason);
        return assessment;
    }

    /// <summary>
    /// FR-20, FR-13, SC-02, UC-06, #423, IADR-0164 決定4/決定5: **Stage 1 の最小取引件数**（§4.1 条件 3）を変更する。
    /// <para>
    /// 値域は <see cref="Stage1TradeCountBounds"/> が単独で決める（1〜1000）。範囲外なら
    /// <see cref="ArgumentOutOfRangeException"/> を投げ、<b>設定を一切変更せず履歴も残さない</b>
    /// （<see cref="UpdateLimits"/> と同じ規律）。
    /// </para>
    /// <para>
    /// <b>100 件未満でも受理する。</b> 裁定は「警告は設定を妨げない。下げた事実が記録に残ることを
    /// 担保する」と定めている。警告は <see cref="Stage1GateCriteria.BelowStatisticalBasis"/> が宣言し、
    /// 画面と昇格承認（Discord）がそれに従う。**ここで拒否してはならない。**
    /// </para>
    /// <para>
    /// <b>段階・発注先には触れない。</b> 変えられるのは条件 3 の件数だけであり、条件 1・条件 2・
    /// §4.3 の打ち切り規則には及ばない（`with` が他プロパティを保つ）。
    /// </para>
    /// </summary>
    public void UpdateStage1MinimumTradeCount(int minimumTradeCount, string actor, string reason)
    {
        RequireActorAndReason(actor, reason);
        Stage1TradeCountBounds.ThrowIfOutOfRange(minimumTradeCount);

        var current = store.GetCurrent();
        Save(
            current with { Stage1MinimumTradeCount = minimumTradeCount },
            current.Stage1MinimumTradeCount,
            minimumTradeCount,
            SettingsChangeType.Stage1MinimumTradeCountChanged,
            actor,
            reason);
    }

    private void Save(
        RiskManagementSettings updated,
        object before,
        object after,
        SettingsChangeType changeType,
        string actor,
        string reason)
    {
        var now = clock.UtcNow;
        store.Save(updated);
        changeLog.Record(new SettingsChangeEntry(
            actor, changeType, reason, now,
            Before: before.ToString(), After: after.ToString()));
    }

    private static void RequireActorAndReason(string actor, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }
}
