import type { ReactNode } from 'react';
import type { ShortSellingStatusView } from '@ai-stock-trading/lib/risk/contracts';
import {
  availabilityAmountText,
  availabilityCountText,
  availabilityRatioText,
  formatAmount,
  formatAt,
  isNotSupplied,
  marketLabel,
  positionSideLabel,
  ratioToPercentDisplay,
} from '@ai-stock-trading/lib/risk/contracts';

// SC-03, FR-10, UC-06, ADR-0016（決定3・決定7・決定9・決定15）, #340, IADR-0154:
// 「維持率・空売りの現況」。**画面の最上位に置く**（3 統制の表より上）。
//
// 計画（05_screens SC-03）の原文: 「**維持率**——決定 7 の閾値に対する現況。**本画面の最上位に置く。**
// マージンコールは口座を失う唯一の経路であり、現物取引には存在しなかった指標である。」
//
// **供給が無い指標を「正常値」に見せない。** 維持率・借株料の累計・自動縮小の発動履歴は、現時点で
// ブローカー照会の供給元が無い。0 や「—」だけで描くと、画面は正常に働いている統制として見える
// （#403 の `ControlViolationCount` 既定 0 が「違反なし」に見えた fail-open と同型）。
// 供給可否の判定は**サーバの応答（MetricAvailability）に従う**——画面へ「未供給」と書き込むと、
// 供給元が入った日に画面が嘘をつき続ける。

export type ShortSellingState = 'loading' | 'ok' | 'unavailable';

export function ShortSellingStatusSection({
  state,
  view,
}: {
  state: ShortSellingState;
  view: ShortSellingStatusView | null;
}) {
  if (state === 'loading') {
    return (
      <Section title="維持率・空売りの現況">
        <p role="status">維持率・空売りの現況を確認中…</p>
      </Section>
    );
  }
  if (state === 'unavailable' || !view) {
    // 取得失敗は領域のみ縮退する（統制状態・段階ゲートと疎結合）。**「問題なし」に見せない。**
    return (
      <Section title="維持率・空売りの現況">
        <p role="alert">維持率・空売りの現況を取得できませんでした。値が無いのではなく、確認できていません。</p>
      </Section>
    );
  }

  return (
    <Section title="維持率・空売りの現況">
      <MaintenanceMarginView view={view} />
      <ShortExposureView view={view} />
      <PositionsView view={view} />
      <BuyInCountView view={view} />
      <AutoReductionView view={view} />
    </Section>
  );
}

// ADR-0016 決定7: 維持率の現況。**本画面で最初に読まれるべき値**である。
function MaintenanceMarginView({ view }: { view: ShortSellingStatusView }) {
  const unsupplied = isNotSupplied(view.maintenanceMarginAvailability);
  return (
    <div>
      <h3>維持率</h3>
      {/* 未供給は role="alert" で出す。マージンコールは口座を失う唯一の経路であり、
          「値が取れていない」ことは正常な状態ではない。 */}
      <p role={unsupplied ? 'alert' : undefined}>
        現在の維持率:{' '}
        <strong>
          {availabilityRatioText(view.maintenanceMarginAvailability, view.maintenanceMarginRatio)}
        </strong>
      </p>
      {unsupplied && (
        <p role="alert">
          維持率はブローカーの口座照会に由来しますが、供給元がまだ実装されていません（moomoo SIMULATE
          では照会 API 自体が利用できず、実弾口座での読み取りが要ります）。
          <strong>この統制は現在まったく働いていません。</strong>
          {/* ADR-0016 決定7（2026-08-07 追記）・05_screens SC-03: **Stage 1 の全期間にわたって表示できない。**
              「いつか直るバグ」と読まれると、利用者は待ってしまい、統制が無いまま運用が進む。 */}
          <strong>
            この値は Stage 1（moomoo SIMULATE）の全期間にわたって表示できません。
            これは不具合ではなく、供給が無いという事実の表示です。
          </strong>
        </p>
      )}
      <dl>
        {/* 適用される閾値は**建玉の株価に依存する**（自前 40% と規制要求の厳しい方）。
            スナップショットが無ければ決められないため、設定値と分けて表示する。 */}
        <dt>適用される閾値</dt>
        <dd>
          {availabilityRatioText(
            view.maintenanceMarginAvailability,
            view.appliedMaintenanceMarginThreshold,
          )}
        </dd>
        <dt>回復目標（閾値 + {ratioToPercentDisplay(view.maintenanceRecoveryTargetOffset)}）</dt>
        <dd>
          {availabilityRatioText(
            view.maintenanceMarginAvailability,
            view.appliedMaintenanceRecoveryTarget,
          )}
        </dd>
        <dt>設定上の維持率閾値</dt>
        <dd>{ratioToPercentDisplay(view.configuredMaintenanceMarginThreshold)}</dd>
      </dl>
      <p>
        実際に適用される閾値は、設定上の閾値と規制上の要求（$5.00 ÷ 株価 と 30% の大きい方）の
        <strong>厳しい方</strong>であり、保有建玉のうち最も厳しいものが口座へ適用されます。
        回復目標は<strong>適用される閾値に連動</strong>します（設定値からの固定値ではありません）。
      </p>
    </div>
  );
}

// ADR-0016 決定9: 空売り比率（空売り建玉の合計 ÷ 建玉総額）。分母は時価であり取得原価ではない。
function ShortExposureView({ view }: { view: ShortSellingStatusView }) {
  const unsupplied = isNotSupplied(view.shortExposureAvailability);
  return (
    <div>
      <h3>空売り比率</h3>
      <p role={unsupplied ? 'alert' : undefined}>
        現在の空売り比率:{' '}
        <strong>{availabilityRatioText(view.shortExposureAvailability, view.shortExposureRatio)}</strong>
        ／上限: {ratioToPercentDisplay(view.shortExposureRatioCap)}
      </p>
      {unsupplied && (
        <p role="alert">
          空売り比率の分母（建玉総額）は<strong>時価</strong>であり、建玉の現在値が揃わなければ算出できません。
          取得原価で代用した値は表示しません（別物の比率になり、上限の判定を誤らせるためです）。
        </p>
      )}
    </div>
  );
}

// ADR-0016 決定15: 保有ポジションに**建玉の方向（ロング / ショート）**と**借株料の累計**を加える。
function PositionsView({ view }: { view: ShortSellingStatusView }) {
  return (
    <div>
      <h3>保有ポジション（方向・借株料）</h3>
      <p>
        借株料の累計:{' '}
        <strong>
          {availabilityAmountText(view.borrowFeeAvailability, view.totalAccruedBorrowFeeUsd)}
        </strong>
      </p>
      {isNotSupplied(view.borrowFeeAvailability) && (
        <p role="alert">
          借株料の累計を記録する経路がまだありません。<strong>0 ではなく「不明」です</strong>——
          費用が発生していないことを意味しません。
        </p>
      )}
      {view.positions.length === 0 ? (
        <p>保有ポジションはありません。</p>
      ) : (
        <table aria-label="保有ポジション（方向・借株料）">
          <thead>
            <tr>
              <th>銘柄</th>
              <th>市場</th>
              <th>方向</th>
              <th>数量</th>
              <th>平均取得単価</th>
              <th>評価額</th>
              <th>借株料累計</th>
            </tr>
          </thead>
          <tbody>
            {view.positions.map((p) => (
              <tr key={`${p.symbol}-${p.market}`}>
                <td>{p.symbol}</td>
                <td>{marketLabel(p.market)}</td>
                <td>{positionSideLabel(p.side)}</td>
                <td>{p.quantity}</td>
                <td>{formatAmount(p.averageEntryPrice)}</td>
                <td>{availabilityAmountText(p.marketValueAvailability, p.marketValueUsd)}</td>
                <td>{availabilityAmountText(p.borrowFeeAvailability, p.accruedBorrowFeeUsd)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

// FR-10, SC-03, ADR-0016 決定15, #424, IADR-0162: 強制買戻しの発生回数。
//
// 計画（05_screens SC-03 の供給元の表・2026-08-07 追加）は本項目に **「0 件と表示してはならない」**と
// 名指しで注記した。**供給が無いことは「強制買戻しが起きていない」ことを意味しない。**
//
// 供給されている場合の 0 は**正当な測定結果**であり、そのまま「0」と描く（`availabilityCountText`）。
// 「0 なら未供給だろう」と画面が推測すると、正当な 0 と未供給が区別できなくなる——**供給可否は
// サーバの宣言（`buyInCountAvailability`）だけで決める。**
function BuyInCountView({ view }: { view: ShortSellingStatusView }) {
  const unsupplied = isNotSupplied(view.buyInCountAvailability);
  return (
    <div>
      <h3>強制買戻しの発生回数</h3>
      <p role={unsupplied ? 'alert' : undefined}>
        強制買戻しの発生回数:{' '}
        <strong>{availabilityCountText(view.buyInCountAvailability, view.buyInCount)}</strong>
      </p>
      {unsupplied && (
        <p role="alert">
          強制買戻しの発生回数を取得できません。事後推定の台帳はありますが、
          <strong>推定が起きたときにしか行が書かれない</strong>ため、件数 0 は
          「ブローカー建玉の観測が一度も届いていない」状態と区別できません。
          <strong>0 件ではなく「不明」です</strong>——強制買戻しが起きていないことを意味しません。
        </p>
      )}
    </div>
  );
}

// FR-10, UC-06, 05_screens SC-03（2026-08-02 追加）: 維持率割れによる自動縮小の現況。
//
// **3 統制（「止める」統制）とは性質が異なる。** 3 統制は新規建てを止めるだけだが、本統制は
// **すでに建てた玉を決済する**。**利用者の承認を待たず、AI を介在させずに動く。**
// 計画は「3 統制と同じ枠に並べると誤読されるため、視覚的に区別すること」と明記している。
// したがって本節は 3 統制の表とは別の枠（枠線つきの region）に置く。
function AutoReductionView({ view }: { view: ShortSellingStatusView }) {
  const unsupplied = isNotSupplied(view.reductionHistoryAvailability);
  return (
    <section
      aria-label="維持率割れによる自動縮小（動かす統制）"
      style={{ border: '2px solid #b45309', padding: '0.75rem', margin: '0.75rem 0' }}
    >
      <h3>維持率割れによる自動縮小 —— 「動かす」統制</h3>
      <p role="alert">
        <strong>
          本統制は、緊急停止・日次損失ロックアウト・一時停止（新規建てを「止める」3 統制）とは性質が異なります。
          すでに建てた玉を決済します。利用者の承認を待たず、AI を介さずに動きます。
        </strong>
      </p>
      <p>
        縮小の対象は<strong>必要証拠金が最も大きい建玉から</strong>です（維持率の回復効率が最も高い順であり、
        含み損の大きい順ではありません）。回復目標へ届く最小限だけを決済します。
      </p>
      <h4>直近の発動履歴</h4>
      {unsupplied ? (
        <p role="alert">
          発動履歴を取得できません。維持率の供給元が無いため、<strong>本統制はまだ一度も発動し得ません</strong>。
          「発動なし」ではなく「記録する経路がない」状態です。
        </p>
      ) : view.reductionHistory.length === 0 ? (
        <p>発動履歴はありません。</p>
      ) : (
        <table aria-label="維持率割れ自動縮小の発動履歴">
          <thead>
            <tr>
              <th>発動日時</th>
              <th>決済前の維持率</th>
              <th>閾値</th>
              <th>回復目標</th>
              <th>決済後の維持率</th>
              <th>決済した建玉（必要証拠金の降順）</th>
            </tr>
          </thead>
          <tbody>
            {view.reductionHistory.map((r) => (
              <tr key={r.executedAt}>
                <td>{formatAt(r.executedAt)}</td>
                <td>{ratioToPercentDisplay(r.ratioBefore)}</td>
                <td>{ratioToPercentDisplay(r.threshold)}</td>
                <td>{ratioToPercentDisplay(r.recoveryTarget)}</td>
                <td>{r.ratioAfter === null ? '（全量決済）' : ratioToPercentDisplay(r.ratioAfter)}</td>
                <td>
                  {r.legs
                    .map(
                      (l) =>
                        `${l.symbol}（${positionSideLabel(l.positionSide)} ${l.quantity} 株・必要証拠金 ${formatAmount(l.requiredMarginUsd)}）`,
                    )
                    .join('、')}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <details open style={{ margin: '0.75rem 0' }}>
      <summary style={{ cursor: 'pointer', fontWeight: 600 }}>{title}</summary>
      <div style={{ marginTop: '0.5rem' }}>{children}</div>
    </details>
  );
}
