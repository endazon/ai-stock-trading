import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import type {
  RiskStatusView,
  SettingsChangeEntry,
  StageGateStatus,
  StageTransition,
} from '../risk/contracts';
import {
  activeControlLabel,
  brokerProviderLabel,
  CHANGE_TYPE_BROKER_PROVIDER,
  criterionLabel,
  formatAt,
  isInternalPaper,
  ratioPercent,
  stageLabel,
  transitionKindLabel,
  withdrawalReasonLabel,
} from '../risk/contracts';
import { PaperModeBanner } from '../shared/PaperModeBanner';
import { PAPER_REFERENCE_LABEL } from '../shared/paperMode';

// SC-03, FR-10, FR-20, UC-06, ADR-0008, ADR-0009, IADR-0084: 承認・統制状態参照画面（参照専用）。
// データ源は BFF `/bff/risk-controls/status`・`/bff/risk-controls/stage-gate`（RiskManagementService・OwnerOnly）。
// 破壊的操作（pause/resume・kill switch・段階遷移承認）は #165 の Discord Bot 側と役割分担し、本画面には置かない
// （統制入口の一元化・安全既定）。統制状態・段階ゲートの各領域は独立に縮退する（一方の失敗が他方を巻き込まない）。

type Status = 'loading' | 'ok' | 'notFound' | 'error';
type StageGateState = 'loading' | 'ok' | 'unavailable';
type HistoryState = 'loading' | 'ok' | 'unavailable';

export function ControlStatusPage() {
  const [status, setStatus] = useState<Status>('loading');
  const [view, setView] = useState<RiskStatusView | null>(null);
  const [gateState, setGateState] = useState<StageGateState>('loading');
  const [gate, setGate] = useState<StageGateStatus | null>(null);
  // FR-20 (2), SC-03, #334: 発注先の変更履歴（日時・変更前後・理由）。設定変更履歴から発注先だけを絞る。
  const [historyState, setHistoryState] = useState<HistoryState>('loading');
  const [providerHistory, setProviderHistory] = useState<SettingsChangeEntry[]>([]);

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

  async function loadProviderHistory(): Promise<void> {
    try {
      const data = await apiFetch<SettingsChangeEntry[]>('/risk-controls/settings/history');
      setProviderHistory((data ?? []).filter((h) => h.changeType === CHANGE_TYPE_BROKER_PROVIDER));
      setHistoryState('ok');
    } catch {
      // 履歴の取得不能はその領域のみ縮退（統制状態・段階ゲートと疎結合）。
      setHistoryState('unavailable');
    }
  }

  useEffect(() => {
    void loadStatus();
    void loadStageGate();
    void loadProviderHistory();
  }, []);

  return (
    <section>
      {/* FR-12, #334: 内蔵 paper 稼働中の警告バナー（画面上部に常時表示。05_screens 共通規約）。 */}
      <PaperModeBanner provider={view?.brokerProvider} />
      <h1>統制状態</h1>
      <p>
        取引統制（緊急停止・日次損失ロックアウト・一時停止）と運用段階の現況を参照します（UC-06 の統制状態の閲覧面）。統制の変更・段階の承認は
        Discord からのみ行えます（本画面は参照専用）。
      </p>

      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'notFound' && <p>統制状態は利用できません。</p>}
      {status === 'error' && <p role="alert">統制状態の取得に失敗しました。</p>}
      {status === 'ok' && view && <StatusView view={view} />}

      <StageGateView state={gateState} gate={gate} paper={isInternalPaper(view?.brokerProvider)} />
      <ProviderHistoryView state={historyState} history={providerHistory} />
    </section>
  );
}

// FR-20 (2), SC-03, #334: 発注先の変更履歴（日時・変更前後・理由）。**本画面は参照専用**であり、
// 変更は SC-02 で行う（05_screens「変更操作を持つ画面は SC-02 だけである」）。
function ProviderHistoryView({
  state,
  history,
}: {
  state: HistoryState;
  history: SettingsChangeEntry[];
}) {
  return (
    <Section title={`発注先の変更履歴（${state === 'ok' ? history.length : '—'}）`}>
      <p>発注先の変更は「リスク設定」画面（SC-02）で行います。本画面は参照専用です。</p>
      {state === 'loading' && <p role="status">発注先の変更履歴を確認中…</p>}
      {state === 'unavailable' && <p>発注先の変更履歴は利用できません。</p>}
      {state === 'ok' && history.length === 0 && <p>発注先の変更履歴はありません。</p>}
      {state === 'ok' && history.length > 0 && (
        <table aria-label="発注先の変更履歴">
          <thead>
            <tr>
              <th>日時</th>
              <th>変更前</th>
              <th>変更後</th>
              <th>変更者</th>
              <th>理由</th>
            </tr>
          </thead>
          <tbody>
            {history.map((h, i) => (
              <tr key={`${i}-${h.changedAt}`}>
                <td>{formatAt(h.changedAt)}</td>
                <td>{h.before ?? '—'}</td>
                <td>{h.after ?? '—'}</td>
                <td>{h.actor}</td>
                <td>{h.reason}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Section>
  );
}

// FR-10, ADR-0009: 3 統制・段階・当日損益・上限使用率・ポジションの集約表示（参照専用）。
function StatusView({ view }: { view: RiskStatusView }) {
  // FR-12, 05_screens 共通規約: 内蔵 paper 稼働中は統制状態のカード類にも `paper` である旨のラベルを付す
  // （数字だけが独り歩きすると、擬似約定の成績を実績と取り違えるため）。
  const paper = isInternalPaper(view.brokerProvider);
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
          {/* INDEX 決定 46 / 05_screens: 運用段階と発注先は**独立した 2 軸**であり、1 行に混ぜて表示しない。 */}
          <dt>運用段階</dt>
          <dd>{stageLabel(view.stage)}</dd>
          <dt>発注先</dt>
          <dd>{brokerProviderLabel(view.brokerProvider)}</dd>
        </dl>
      </Section>

      <Section title={paper ? `当日損益（${PAPER_REFERENCE_LABEL}）` : '当日損益'}>
        <dl>
          <dt>実現損益</dt>
          <dd>{view.dailyRealizedPnl}</dd>
          <dt>含み損益</dt>
          <dd>{view.unrealizedPnl}</dd>
          <dt>合計</dt>
          <dd>{view.dailyPnl}</dd>
        </dl>
      </Section>

      <Section title={paper ? `上限使用率（${PAPER_REFERENCE_LABEL}）` : '上限使用率'}>
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
function StageGateView({
  state,
  gate,
  paper,
}: {
  state: StageGateState;
  gate: StageGateStatus | null;
  paper: boolean;
}) {
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
    <Section title={paper ? `段階ゲート（${PAPER_REFERENCE_LABEL}）` : '段階ゲート'}>
      <dl>
        <dt>現段階</dt>
        <dd>{stageLabel(gate.currentStage)}</dd>
        {/* FR-20, #334: 段階が定める**既定の**発注先。現在の発注先（上の「発注先」行）とは別物である。 */}
        <dt>段階の既定発注先</dt>
        <dd>{brokerProviderLabel(gate.currentSettings.mode)}</dd>
      </dl>

      <Stage1ProgressView gate={gate} />

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

// FR-20, SC-03, #334, IADR-0142: Stage 1 の進捗。**moomoo SIMULATE の約定のみを集計**し、
// 内蔵 paper 稼働により算入されなかった営業日数を**併記する**（05_screens SC-03）。
// 「経過 42 / 60 営業日（paper 稼働により 3 日を除外）」——算入されなかった期間があること自体が
// 見えないと、進捗の数字を説明できなくなる。
function Stage1ProgressView({ gate }: { gate: StageGateStatus }) {
  const progress = gate.stage1Progress;
  const criteria = gate.stage1Criteria;
  if (!progress || !criteria) {
    // 応答に進捗が含まれない（BFF 未追随・旧版サーバ）場合は領域のみ縮退する。
    return <p>Stage 1 の進捗は利用できません。</p>;
  }
  const excluded = progress.excludedInternalPaperDays;
  return (
    <>
      <h3>Stage 1 の進捗</h3>
      <p>
        経過 {progress.qualifiedTradingDays} / {criteria.targetTradingDays} 営業日
        {excluded > 0 ? `（paper 稼働により ${excluded} 日を除外）` : ''}／ 取引 {progress.tradeCount} /{' '}
        {criteria.minimumTradeCount} 件
      </p>
      <p>
        moomoo SIMULATE（moomoo のデモ環境）の約定のみを集計しています。内蔵 paper
        の約定・稼働日数は算入されません。累計 {criteria.maximumTradingDays}{' '}
        営業日を経ても取引件数に届かない場合は Stage 0 へ差し戻します。
      </p>
    </>
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
