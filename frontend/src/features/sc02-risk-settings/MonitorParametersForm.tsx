import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import type { MarketMonitorSettings, MonitorSettingsChangeEntry } from '../monitor/contracts';
import {
  COOLDOWN_RANGE_TEXT,
  hoursTextToTimeSpan,
  monitorChangeTypeLabel,
  MONITOR_CHANGE_TYPE_COOLDOWN,
  MONITOR_CHANGE_TYPE_MOVEMENT_THRESHOLD,
  MOVEMENT_THRESHOLD_RANGE_TEXT,
  timeSpanToHoursText,
  validateCooldownHours,
  validateMovementThresholdPercent,
} from '../monitor/contracts';
import { formatAt, percentTextToRatio, ratioToPercentText } from '../risk/contracts';

// SC-02, FR-03, FR-13, FR-11, UC-06, #423, IADR-0155 / IADR-0165 決定2:
// **市場監視パラメータ（変動閾値・クールダウン）の閲覧・変更。**
//
// 2026-08-07 の利用者裁定（質問票 第 13 回 Q12・案 B）により、本節は SC-01 §2 から SC-02 へ移った。
//
//   - **権威は MarketMonitorService である**（`GET /monitor/settings`・`PUT /monitor/settings/{項目}`）。
//     旧記述の「ConfigurationService 由来」は誤りであった。ConfigurationService が持つのは
//     `/assumptions`（全体前提条件）だけである。
//   - planning#33 は画面の責務分界を「**由来サービスが異なるなら別画面**」と裁定した。
//     同じ基準を当てれば変動閾値は SC-02（監視銘柄と同じ MarketMonitorService 由来）に属する。
//   - **監視銘柄とセットで使う値である。**「どの銘柄を、どれだけ動いたら」は 1 つの設定である。
//
// **収集間隔はここにも作らない。** 裁定（Q11）は「画面から変更しない。起動時構成とする」である。
//
// 別サービス（MarketMonitor）を消費するため、リスク設定（RiskManagementService）の取得可否に
// 連動させず独立してロード・縮退する（片方の障害・BFF 未結線を巻き込まない・fail-safe。
// 監視銘柄セクション＝`WatchlistForm` と同じ方針）。
//
// **変動閾値とクールダウンは別々のフォームである。** サーバ側が項目単位の部分更新
// （`PUT /monitor/settings/movement-threshold` と `/cooldown`）を 2 本持つためであり、
// 1 つのフォームで両方を送ると「片方だけ成功した」状態を作れる。アクセシブル名も完全に分ける。

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

export function MonitorParametersForm() {
  const [state, setState] = useState<LoadState>('loading');
  const [current, setCurrent] = useState<MarketMonitorSettings | null>(null);
  const [historyState, setHistoryState] = useState<LoadState>('loading');
  const [history, setHistory] = useState<MonitorSettingsChangeEntry[]>([]);

  async function loadCurrent(): Promise<void> {
    try {
      const data = await apiFetch<MarketMonitorSettings>('/monitor/settings');
      setCurrent(data);
      setState('ok');
    } catch {
      setState('unavailable');
    }
  }

  async function loadHistory(): Promise<void> {
    try {
      const data = await apiFetch<MonitorSettingsChangeEntry[]>('/monitor/settings/history');
      // 市場監視パラメータの変更だけを出す（監視銘柄の追加・削除は隣の節の関心である）。
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

  async function reload(): Promise<void> {
    await loadCurrent();
    await loadHistory();
  }

  return (
    <Section title="市場監視パラメータ（変動閾値・クールダウン）">
      <p>
        監視銘柄をどれだけ動いたら判断へ回すか（変動閾値）と、同一銘柄の再トリガーをどれだけ抑制するか
        （クールダウン）を設定します（FR-03/FR-13）。変更は利用者のみが行え、理由が必須です。
      </p>

      {state === 'loading' && <p role="status">市場監視パラメータを確認中…</p>}
      {state === 'unavailable' && (
        <p role="alert">市場監視パラメータを取得できませんでした。</p>
      )}

      {state === 'ok' && current && (
        <>
          <p>
            現在の変動閾値: <strong>{ratioToPercentText(current.movementThresholdRatio)}%</strong>／
            現在のクールダウン: <strong>{timeSpanToHoursText(current.cooldown)} 時間</strong>
          </p>

          <MovementThresholdForm current={current} onSaved={reload} />
          <CooldownForm current={current} onSaved={reload} />
        </>
      )}

      <MonitorParameterHistoryView state={historyState} history={history} />
    </Section>
  );
}

// FR-03, FR-13: 変動閾値の変更。ワイヤは**比率**（0.03）、画面は**百分率**（3）である。
// 変換は `percentTextToRatio` / `ratioToPercentText`（risk/contracts）だけを通す（IADR-0151 決定1）。
function MovementThresholdForm({
  current,
  onSaved,
}: {
  current: MarketMonitorSettings;
  onSaved: () => Promise<void>;
}) {
  const [percent, setPercent] = useState(() => ratioToPercentText(current.movementThresholdRatio));
  const [reason, setReason] = useState('');
  const [saveState, setSaveState] = useState<SaveState>('idle');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);

  // 現在値に追随して入力を初期化する（自分の保存成功後の再取得・外部変更）。
  useEffect(() => {
    setPercent(ratioToPercentText(current.movementThresholdRatio));
    setReason('');
  }, [current.movementThresholdRatio]);

  const validationError = validateMovementThresholdPercent(percent);
  const blocked = validationError !== null || reason.trim() === '' || saveState === 'saving';

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    // 理由必須・値域内を送信の前提とする（ボタン無効化と二重の防御・安全既定）。
    if (blocked) return;
    const ratio = percentTextToRatio(percent);
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
      setSavedNotice('変動閾値を保存しました。');
      await onSaved();
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveState('error');
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <form onSubmit={handleSubmit} aria-label="変動閾値の変更">
      <div>
        <label htmlFor="movement-threshold">変動閾値 %</label>
        <input
          id="movement-threshold"
          type="number"
          step="any"
          value={percent}
          aria-invalid={validationError !== null}
          aria-describedby="movement-threshold-help"
          onChange={(e) => setPercent(e.target.value)}
        />
        <span id="movement-threshold-help">
          {`前回判断時点の価格に対する変動率です。許容範囲: ${MOVEMENT_THRESHOLD_RANGE_TEXT}`}
        </span>
      </div>

      <div>
        <label htmlFor="movement-threshold-reason">変動閾値の変更理由</label>
        <textarea
          id="movement-threshold-reason"
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          required
        />
      </div>

      {validationError && <p role="alert">{validationError}</p>}

      <button type="submit" disabled={blocked}>
        変動閾値を保存
      </button>
      {saveState === 'saving' && <span role="status">変動閾値を保存中…</span>}
      {savedNotice && <p role="status">{savedNotice}</p>}
      {saveError && <p role="alert">{saveError}</p>}
    </form>
  );
}

// FR-03, FR-13: クールダウンの変更。ワイヤは **`TimeSpan` 文字列**（`"00:15:00"`）、画面は**時間**である。
// 変換は `hoursTextToTimeSpan` / `timeSpanToHoursText`（monitor/contracts）だけを通す。
function CooldownForm({
  current,
  onSaved,
}: {
  current: MarketMonitorSettings;
  onSaved: () => Promise<void>;
}) {
  const [hours, setHours] = useState(() => timeSpanToHoursText(current.cooldown));
  const [reason, setReason] = useState('');
  const [saveState, setSaveState] = useState<SaveState>('idle');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);

  useEffect(() => {
    setHours(timeSpanToHoursText(current.cooldown));
    setReason('');
  }, [current.cooldown]);

  const validationError = validateCooldownHours(hours);
  const blocked = validationError !== null || reason.trim() === '' || saveState === 'saving';

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    if (blocked) return;
    const cooldown = hoursTextToTimeSpan(hours);
    // 読めない値は送らない（**黙って "00:00:00" を送らない**。0 は「抑制なし」という別の設定である）。
    if (cooldown === null) return;

    setSaveState('saving');
    setSaveError(null);
    setSavedNotice(null);
    try {
      await apiFetch<MarketMonitorSettings>('/monitor/settings/cooldown', {
        method: 'PUT',
        json: { cooldown, reason: reason.trim() },
      });
      setReason('');
      setSaveState('idle');
      setSavedNotice('クールダウンを保存しました。');
      await onSaved();
    } catch (err: unknown) {
      setSaveState('error');
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <form onSubmit={handleSubmit} aria-label="クールダウンの変更">
      <div>
        <label htmlFor="monitor-cooldown">クールダウン 時間</label>
        <input
          id="monitor-cooldown"
          type="number"
          step="any"
          value={hours}
          aria-invalid={validationError !== null}
          aria-describedby="monitor-cooldown-help"
          onChange={(e) => setHours(e.target.value)}
        />
        <span id="monitor-cooldown-help">
          {`同一銘柄の再トリガーを抑制する時間です（0 は抑制なし）。許容範囲: ${COOLDOWN_RANGE_TEXT}`}
        </span>
      </div>

      <div>
        <label htmlFor="monitor-cooldown-reason">クールダウンの変更理由</label>
        <textarea
          id="monitor-cooldown-reason"
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          required
        />
      </div>

      {validationError && <p role="alert">{validationError}</p>}

      <button type="submit" disabled={blocked}>
        クールダウンを保存
      </button>
      {saveState === 'saving' && <span role="status">クールダウンを保存中…</span>}
      {savedNotice && <p role="status">{savedNotice}</p>}
      {saveError && <p role="alert">{saveError}</p>}
    </form>
  );
}

// FR-11, FR-13: 市場監視パラメータの変更履歴（新しい順）。取得不能・0 件はその旨を明示する（縮退表示）。
function MonitorParameterHistoryView({
  state,
  history,
}: {
  state: LoadState;
  history: MonitorSettingsChangeEntry[];
}) {
  return (
    <div>
      <h3>市場監視パラメータの変更履歴（{state === 'ok' ? history.length : '—'}）</h3>
      {state === 'loading' && <p role="status">市場監視パラメータの変更履歴を確認中…</p>}
      {state === 'unavailable' && <p>市場監視パラメータの変更履歴は利用できません。</p>}
      {state === 'ok' && history.length === 0 && <p>市場監視パラメータの変更履歴はありません。</p>}
      {state === 'ok' && history.length > 0 && (
        <table aria-label="市場監視パラメータの変更履歴">
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
    <details open style={{ margin: '0.75rem 0' }} aria-label={title}>
      <summary style={{ cursor: 'pointer', fontWeight: 600 }}>{title}</summary>
      <div style={{ marginTop: '0.5rem' }}>{children}</div>
    </details>
  );
}
