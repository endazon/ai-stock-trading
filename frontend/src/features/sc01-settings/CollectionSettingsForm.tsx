import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import type { MarketMonitorSettings, MonitorSettingsChangeEntry } from '../monitor/contracts';
import {
  monitorChangeTypeLabel,
  MONITOR_CHANGE_TYPE_COOLDOWN,
  MONITOR_CHANGE_TYPE_MOVEMENT_THRESHOLD,
  MOVEMENT_THRESHOLD_RANGE_TEXT,
  validateMovementThresholdPercent,
} from '../monitor/contracts';
import {
  formatAt,
  METRIC_NOT_SUPPLIED_TEXT,
  percentTextToRatio,
  ratioToPercentText,
} from '../risk/contracts';

// SC-01 §2, FR-13, FR-03, UC-06, #340, IADR-0155: 収集パラメータ（変動閾値）の閲覧・変更。
//
// 計画（05_screens SC-01 §2）は「変動閾値・収集間隔（ConfigurationService 由来）の閲覧・変更」を求める。
// 実装の現況は次のとおりであり、**本節は差を隠さずに画面へ書く**。
//
//   変動閾値 … **供給がある。** ただし由来は ConfigurationService ではなく MarketMonitorService
//              （`/monitor/settings`）である。計画の「ConfigurationService 由来」は実装と食い違う（環流済み）。
//   収集間隔 … **供給が無い。** 起動時構成（`Collection:PollIntervalSeconds` / `Monitor:PollIntervalSeconds`）
//              であり、読み書きするエンドポイントも設定ストアも存在しない。
//              **入力欄を作らない**——動かない入力欄は「変更できたつもり」を生み、統制設定では
//              「設定したはずの値と実際の値が違う」という最も危険な状態になる。
//
// 別サービス（MarketMonitor）を消費するため、SC-01 §1（ConfigurationService `/assumptions`）の
// 取得可否に連動させず独立してロード・縮退する（片方の障害・BFF 未結線を巻き込まない・fail-safe）。

type LoadState = 'loading' | 'ok' | 'unavailable';
type SaveState = 'idle' | 'saving' | 'error';

// ApiError の種別を利用者向けメッセージへ写像する（SC-01 §1・SC-02 と同方針）。
function saveMessageOf(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.kind === 'conflict') {
      return '競合が発生しました。最新を取得して再試行してください。';
    }
    if (e.kind === 'validation') {
      const detail = e.details.length > 0 ? `（${e.details.join(' / ')}）` : '';
      return `入力内容に誤りがあります。${detail}`;
    }
    if (e.kind === 'forbidden') {
      return '変更する権限がありません。';
    }
    return e.message;
  }
  return '保存に失敗しました。';
}

export function CollectionSettingsForm() {
  const [state, setState] = useState<LoadState>('loading');
  const [current, setCurrent] = useState<MarketMonitorSettings | null>(null);
  const [thresholdPercent, setThresholdPercent] = useState('');
  const [reason, setReason] = useState('');
  const [saveState, setSaveState] = useState<SaveState>('idle');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);
  const [historyState, setHistoryState] = useState<LoadState>('loading');
  const [history, setHistory] = useState<MonitorSettingsChangeEntry[]>([]);

  async function loadCurrent(): Promise<void> {
    try {
      const data = await apiFetch<MarketMonitorSettings>('/monitor/settings');
      setCurrent(data);
      // ワイヤは比率（0.03）、画面は百分率（3）。変換は risk/contracts の 2 関数だけを通す（IADR-0151 決定1）。
      setThresholdPercent(ratioToPercentText(data.movementThresholdRatio));
      setState('ok');
    } catch {
      setState('unavailable');
    }
  }

  async function loadHistory(): Promise<void> {
    try {
      const data = await apiFetch<MonitorSettingsChangeEntry[]>('/monitor/settings/history');
      // 収集パラメータの変更だけを出す（監視銘柄の追加・削除は SC-02 の関心）。
      setHistory(
        (data ?? []).filter(
          (h) =>
            h.changeType === MONITOR_CHANGE_TYPE_MOVEMENT_THRESHOLD
            || h.changeType === MONITOR_CHANGE_TYPE_COOLDOWN,
        ),
      );
      setHistoryState('ok');
    } catch {
      setHistoryState('unavailable');
    }
  }

  useEffect(() => {
    void loadCurrent();
    void loadHistory();
  }, []);

  const validationError = validateMovementThresholdPercent(thresholdPercent);
  const reasonMissing = reason.trim() === '';
  const blocked = validationError !== null || reasonMissing || saveState === 'saving';

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    // 理由必須・値域内を送信の前提とする（ボタン無効化と二重の防御・安全既定）。
    if (blocked) return;
    const ratio = percentTextToRatio(thresholdPercent);
    // 読めない値は送らない（**黙って 0 を送らない**。0 はすべての変動で発火する危険側の設定である）。
    if (ratio === null) return;

    setSaveState('saving');
    setSaveError(null);
    setSavedNotice(null);
    try {
      await apiFetch<MarketMonitorSettings>('/monitor/settings/movement-threshold', {
        method: 'PUT',
        json: { movementThresholdRatio: ratio, reason: reason.trim() },
      });
      setReason('');
      setSaveState('idle');
      setSavedNotice('保存しました。');
      await loadCurrent();
      await loadHistory();
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveState('error');
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <Section title="収集パラメータ（変動閾値・収集間隔）">
      {state === 'loading' && <p role="status">収集パラメータを確認中…</p>}
      {/* SC-01 §2, #424, IADR-0162: 取得失敗も**供給が無い**状態の 1 つである（05_screens 共通規約）。
          「値が無い」のではなく「確認できていない」ことを規約の文言で明示する。 */}
      {state === 'unavailable' && (
        <p role="alert">
          収集パラメータを<strong>{METRIC_NOT_SUPPLIED_TEXT}</strong>。値が無いのではなく、確認できていません。
        </p>
      )}

      {state === 'ok' && current && (
        <form onSubmit={handleSubmit} aria-label="収集パラメータの変更">
          <p>
            現在の変動閾値: <strong>{ratioToPercentText(current.movementThresholdRatio)}%</strong>／
            現在のクールダウン: <strong>{current.cooldown}</strong>
          </p>
          <div>
            <label htmlFor="movement-threshold">変動閾値 %</label>
            <input
              id="movement-threshold"
              type="number"
              step="any"
              value={thresholdPercent}
              aria-invalid={validationError !== null}
              aria-describedby="movement-threshold-help"
              onChange={(e) => setThresholdPercent(e.target.value)}
            />
            <span id="movement-threshold-help">
              {`前回判断時点の価格に対する変動率です。許容範囲: ${MOVEMENT_THRESHOLD_RANGE_TEXT}`}
            </span>
          </div>

          <div>
            <label htmlFor="collection-reason">収集パラメータの変更理由</label>
            <textarea
              id="collection-reason"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              required
            />
          </div>

          {validationError && <p role="alert">{validationError}</p>}

          <button type="submit" disabled={blocked}>
            変動閾値を保存
          </button>
          {saveState === 'saving' && <span role="status">保存中…</span>}
          {savedNotice && <p role="status">{savedNotice}</p>}
          {saveError && <p role="alert">{saveError}</p>}
        </form>
      )}

      {/* SC-01 §2, #340, IADR-0155: **収集間隔は供給が無い。**
          動かない入力欄を置くと「変更できたつもり」を生む。統制設定では「設定したはずの値と実際の値が
          違う」ことが最も危険であるため、入力欄は作らず、変更できない事実を明示する。 */}
      <div>
        <h3>収集間隔</h3>
        {/* `role="note"` とし `alert` にはしない。**常時表示される静的な注記**であり、
            画面の他の警告（取得失敗・入力エラー）と同じ緊急度で読ませると警告全体が軽くなる。 */}
        <p role="note">
          現在の収集間隔: <strong>{METRIC_NOT_SUPPLIED_TEXT}</strong>。
          <strong>本画面から閲覧・変更できません。</strong>
          現在の実装では起動時の構成値（情報収集サービス・市場監視サービスのポーリング間隔）であり、
          値を読み書きする経路がありません。変更には運用側での再構成が必要です。
        </p>
      </div>

      <CollectionHistoryView state={historyState} history={history} />
    </Section>
  );
}

// FR-11, FR-13: 収集パラメータの変更履歴（新しい順）。取得不能・0 件はその旨を明示する（縮退表示）。
function CollectionHistoryView({
  state,
  history,
}: {
  state: LoadState;
  history: MonitorSettingsChangeEntry[];
}) {
  return (
    <div>
      <h3>収集パラメータの変更履歴（{state === 'ok' ? history.length : '—'}）</h3>
      {state === 'loading' && <p role="status">収集パラメータの変更履歴を確認中…</p>}
      {state === 'unavailable' && <p>収集パラメータの変更履歴は利用できません。</p>}
      {state === 'ok' && history.length === 0 && <p>収集パラメータの変更履歴はありません。</p>}
      {state === 'ok' && history.length > 0 && (
        <table aria-label="収集パラメータの変更履歴">
          <thead>
            <tr>
              <th>種別</th>
              <th>変更前</th>
              <th>変更後</th>
              <th>変更者</th>
              <th>理由</th>
              <th>日時</th>
            </tr>
          </thead>
          <tbody>
            {history.map((h, i) => (
              <tr key={`${i}-${h.changeType}-${h.changedAt}`}>
                <td>{monitorChangeTypeLabel(h.changeType)}</td>
                <td>{h.before ?? '—'}</td>
                <td>{h.after ?? '—'}</td>
                <td>{h.actor}</td>
                <td>{h.reason}</td>
                <td>{formatAt(h.changedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
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
