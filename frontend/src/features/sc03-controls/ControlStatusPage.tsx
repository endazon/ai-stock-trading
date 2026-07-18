import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import type { RiskStatusView, StageGateStatus, StageTransition } from '../risk/contracts';
import {
  activeControlLabel,
  criterionLabel,
  formatAt,
  modeLabel,
  ratioPercent,
  stageLabel,
  transitionKindLabel,
  withdrawalReasonLabel,
} from '../risk/contracts';

// SC-03, FR-10, FR-20, UC-06, ADR-0008, ADR-0009, IADR-0084: 承認・統制状態参照画面（参照専用）。
// データ源は BFF `/bff/risk-controls/status`・`/bff/risk-controls/stage-gate`（RiskManagementService・OwnerOnly）。
// 破壊的操作（pause/resume・kill switch・段階遷移承認）は #165 の Discord Bot 側と役割分担し、本画面には置かない
// （統制入口の一元化・安全既定）。統制状態・段階ゲートの各領域は独立に縮退する（一方の失敗が他方を巻き込まない）。

type Status = 'loading' | 'ok' | 'notFound' | 'error';
type StageGateState = 'loading' | 'ok' | 'unavailable';

export function ControlStatusPage() {
  const [status, setStatus] = useState<Status>('loading');
  const [view, setView] = useState<RiskStatusView | null>(null);
  const [gateState, setGateState] = useState<StageGateState>('loading');
  const [gate, setGate] = useState<StageGateStatus | null>(null);

  async function loadStatus(): Promise<void> {
    try {
      const data = await apiFetch<RiskStatusView>('/risk-controls/status');
      setView(data);
      setStatus('ok');
    } catch (e: unknown) {
      // 404 は不在/秘匿を区別しない（IADR-0009）。BFF 未登録も安全側に縮退。
      setStatus(e instanceof ApiError && e.kind === 'notFound' ? 'notFound' : 'error');
    }
  }

  async function loadStageGate(): Promise<void> {
    try {
      const data = await apiFetch<StageGateStatus>('/risk-controls/stage-gate');
      setGate(data);
      setGateState('ok');
    } catch {
      // 段階ゲートの取得不能はその領域のみ縮退（統制状態と疎結合）。
      setGateState('unavailable');
    }
  }

  useEffect(() => {
    void loadStatus();
    void loadStageGate();
  }, []);

  return (
    <section>
      <h1>統制状態</h1>
      <p>
        取引統制（緊急停止・日次損失ロックアウト・一時停止）と運用段階の現況を参照します（UC-06 の統制状態の閲覧面）。統制の変更・段階の承認は
        Discord からのみ行えます（本画面は参照専用）。
      </p>

      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'notFound' && <p>統制状態は利用できません。</p>}
      {status === 'error' && <p role="alert">統制状態の取得に失敗しました。</p>}
      {status === 'ok' && view && <StatusView view={view} />}

      <StageGateView state={gateState} gate={gate} />
    </section>
  );
}

// FR-10, ADR-0009: 3 統制・段階・当日損益・上限使用率・ポジションの集約表示（参照専用）。
function StatusView({ view }: { view: RiskStatusView }) {
  return (
    <>
      <Section title="取引統制">
        <p>
          成立中で最優先の統制: <strong>{activeControlLabel(view.activeControl)}</strong>／新規建て:{' '}
          <strong>{view.newEntriesBlocked ? '停止中' : '可'}</strong>
        </p>
        <dl>
          <dt>緊急停止</dt>
          <dd>{view.killSwitchEngaged ? '作動中' : '解除'}</dd>
          <dt>日次損失ロックアウト</dt>
          <dd>
            {view.dailyLossLockoutActive
              ? `有効（解除予定 ${formatAt(view.lockoutReleaseOn)}）`
              : '無効'}
          </dd>
          <dt>一時停止</dt>
          <dd>{view.tradingPaused ? '停止中' : '稼働'}</dd>
          <dt>運用段階</dt>
          <dd>{stageLabel(view.stage)}</dd>
        </dl>
      </Section>

      <Section title="当日損益">
        <dl>
          <dt>実現損益</dt>
          <dd>{view.dailyRealizedPnl}</dd>
          <dt>含み損益</dt>
          <dd>{view.unrealizedPnl}</dd>
          <dt>合計</dt>
          <dd>{view.dailyPnl}</dd>
        </dl>
      </Section>

      <Section title="上限使用率">
        <table aria-label="上限使用率">
          <thead>
            <tr>
              <th>項目</th>
              <th>現在</th>
              <th>上限</th>
              <th>使用率</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>1日発注金額</td>
              <td>{view.dailyOrderedAmount}</td>
              <td>{view.maxDailyOrderAmount}</td>
              <td>{ratioPercent(view.dailyOrderedAmount, view.maxDailyOrderAmount)}</td>
            </tr>
            <tr>
              <td>ドローダウン</td>
              <td>{view.drawdownRatio}</td>
              <td>{view.maxDrawdownRatio}</td>
              <td>{ratioPercent(view.drawdownRatio, view.maxDrawdownRatio)}</td>
            </tr>
            <tr>
              <td>保有銘柄数</td>
              <td>{view.openPositionCount}</td>
              <td>{view.maxOpenPositions}</td>
              <td>{ratioPercent(view.openPositionCount, view.maxOpenPositions)}</td>
            </tr>
          </tbody>
        </table>
        <p>資金: {view.capital}</p>
      </Section>
    </>
  );
}

// FR-20, ADR-0008: 段階ゲート現況（現段階・設定・昇格評価・撤退評価・遷移履歴）。参照専用。
function StageGateView({ state, gate }: { state: StageGateState; gate: StageGateStatus | null }) {
  if (state === 'loading') {
    return (
      <Section title="段階ゲート">
        <p role="status">段階ゲートを確認中…</p>
      </Section>
    );
  }
  if (state === 'unavailable' || !gate) {
    return (
      <Section title="段階ゲート">
        <p>段階ゲートは利用できません。</p>
      </Section>
    );
  }
  return (
    <Section title="段階ゲート">
      <dl>
        <dt>現段階</dt>
        <dd>{stageLabel(gate.currentStage)}</dd>
        <dt>モード</dt>
        <dd>{modeLabel(gate.currentSettings.mode)}</dd>
        <dt>資金上限</dt>
        <dd>{gate.currentSettings.capitalCap}</dd>
      </dl>

      <h3>昇格評価</h3>
      <p>
        昇格先:{' '}
        {gate.promotion.targetStage === null ? '（最上段・昇格先なし）' : stageLabel(gate.promotion.targetStage)}／
        判定: <strong>{gate.promotion.eligible ? '昇格可' : '不可'}</strong>
      </p>
      {gate.promotion.unmetCriteria.length > 0 && (
        <p>未充足基準: {gate.promotion.unmetCriteria.map(criterionLabel).join('、')}</p>
      )}

      <h3>撤退評価</h3>
      {gate.withdrawal.triggered ? (
        <p role="alert">
          撤退基準に到達（
          {gate.withdrawal.reason === null ? '理由不明' : withdrawalReasonLabel(gate.withdrawal.reason)}）／
          新規建て停止: <strong>{gate.withdrawal.haltNewEntries ? 'あり' : 'なし'}</strong>／
          降格提案:{' '}
          {gate.withdrawal.proposedStage === null ? '—' : stageLabel(gate.withdrawal.proposedStage)}
        </p>
      ) : (
        <p>撤退基準への到達はありません。</p>
      )}

      <h3>遷移履歴</h3>
      <TransitionHistory history={gate.history} />
    </Section>
  );
}

// FR-20: 段階遷移履歴（新しい順）。承認による昇格・差し戻しの監査。
function TransitionHistory({ history }: { history: StageTransition[] }) {
  if (history.length === 0) {
    return <p>遷移履歴はありません。</p>;
  }
  // 台帳は追記順（古い→新しい）。表示は新しい順に反転する（元配列は変更しない）。
  const rows = [...history].reverse();
  return (
    <table aria-label="段階遷移履歴">
      <thead>
        <tr>
          <th>連番</th>
          <th>種別</th>
          <th>遷移</th>
          <th>承認者</th>
          <th>理由</th>
          <th>日時</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((t) => (
          <tr key={t.sequence}>
            <td>{t.sequence}</td>
            <td>{transitionKindLabel(t.kind)}</td>
            <td>{`${stageLabel(t.fromStage)} → ${stageLabel(t.toStage)}`}</td>
            <td>{t.approvedBy}</td>
            <td>{t.reason}</td>
            <td>{formatAt(t.occurredAtUtc)}</td>
          </tr>
        ))}
      </tbody>
    </table>
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
