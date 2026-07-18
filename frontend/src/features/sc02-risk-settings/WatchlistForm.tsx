import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import { formatAt, marketLabel, MARKET_OPTIONS } from '../risk/contracts';
import type { MonitoredSymbol, MonitorSettingsChangeEntry } from '../monitor/contracts';
import { monitorChangeTypeLabel } from '../monitor/contracts';

// SC-02, FR-13, FR-03, FR-11, UC-06, IADR-0088, IADR-0090: 監視銘柄（watchlist）の一覧表示・追加・削除。
// データ源は MarketMonitorService `/monitor/watchlist`（OwnerOnly・PR #195）。リスク設定（RiskManagementService）とは
// 別サービスのため、本セクションは自前でロード/縮退し、リスク設定の取得可否に連動しない（fail-safe な疎結合・IADR-0090 決定 1）。
// 追加/削除は個別操作 API を消費し（全置換しない・決定 2）、いずれも理由必須。削除は破壊的なため明示確認を要求する（決定 3）。
// 検証(400)・競合(409) はメッセージ表示に留め、破壊的な自動再試行はしない（安全既定）。market は数値 enum を写像する（決定 4）。

type Status = 'loading' | 'ok' | 'notFound' | 'error';
type HistoryStatus = 'loading' | 'ok' | 'unavailable';
type OpState = 'idle' | 'saving' | 'error';

// (Symbol, Market) の同一性キー。区切りは銘柄コードに現れない縦棒を用いる（削除確認の対象特定に使う）。
function keyOf(s: MonitoredSymbol): string {
  return `${s.symbol}|${s.market}`;
}

// ApiError の種別を利用者向けメッセージへ写像する（RiskSettingsPage の saveMessageOf と同方針）。
function messageOf(e: unknown): string {
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
  return '操作に失敗しました。';
}

export function WatchlistForm() {
  const [status, setStatus] = useState<Status>('loading');
  const [symbols, setSymbols] = useState<MonitoredSymbol[]>([]);
  const [historyStatus, setHistoryStatus] = useState<HistoryStatus>('loading');
  const [history, setHistory] = useState<MonitorSettingsChangeEntry[]>([]);

  // 追加の下書き。
  const [newSymbol, setNewSymbol] = useState('');
  const [newMarket, setNewMarket] = useState<number>(MARKET_OPTIONS[0]?.value ?? 0);
  const [newReason, setNewReason] = useState('');
  const [addState, setAddState] = useState<OpState>('idle');
  const [addError, setAddError] = useState<string | null>(null);
  const [addNotice, setAddNotice] = useState<string | null>(null);

  // 削除の明示確認（対象キー・理由・状態）。pendingKey が null の間は確認パネルを開かない。
  const [pendingKey, setPendingKey] = useState<string | null>(null);
  const [deleteReason, setDeleteReason] = useState('');
  const [deleteState, setDeleteState] = useState<OpState>('idle');
  const [deleteError, setDeleteError] = useState<string | null>(null);

  async function loadWatchlist(): Promise<void> {
    try {
      const data = await apiFetch<MonitoredSymbol[]>('/monitor/watchlist');
      // 想定外の形（配列でない）は空扱いにする（画面を壊さない・fail-safe）。
      setSymbols(Array.isArray(data) ? data : []);
      setStatus('ok');
    } catch (e: unknown) {
      // 404 は不在/秘匿を区別しない（IADR-0009）。BFF 未結線（/monitor/* 未プロキシ）も安全側に縮退する。
      setStatus(e instanceof ApiError && e.kind === 'notFound' ? 'notFound' : 'error');
    }
  }

  async function loadHistory(): Promise<void> {
    try {
      const data = await apiFetch<MonitorSettingsChangeEntry[]>('/monitor/watchlist/history');
      setHistory(Array.isArray(data) ? data : []);
      setHistoryStatus('ok');
    } catch {
      // 履歴の取得不能はその領域のみ縮退する（一覧・追加/削除と疎結合）。
      setHistoryStatus('unavailable');
    }
  }

  useEffect(() => {
    void loadWatchlist();
    void loadHistory();
  }, []);

  const canAdd = newSymbol.trim() !== '' && newReason.trim() !== '' && addState !== 'saving';

  async function handleAdd(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    // 理由必須・銘柄コード必須を送信の前提にする（ボタン無効化と二重の防御・安全既定）。
    if (!canAdd) return;
    setAddState('saving');
    setAddError(null);
    setAddNotice(null);
    try {
      // POST /monitor/watchlist（{ symbol, market, reason }）。重複追加・空・未定義 market はサーバ 400（#191）。
      await apiFetch<MonitoredSymbol[]>('/monitor/watchlist', {
        method: 'POST',
        json: { symbol: newSymbol.trim(), market: newMarket, reason: newReason.trim() },
      });
      // 成功後は下書きを消し、一覧・履歴を再取得して最新化する。破壊的操作はしない。
      setNewSymbol('');
      setNewReason('');
      setAddState('idle');
      setAddNotice('追加しました。');
      await loadWatchlist();
      await loadHistory();
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setAddState('error');
      setAddError(messageOf(err));
    }
  }

  function beginDelete(target: MonitoredSymbol): void {
    // 削除は破壊的なため、行の「削除」では確定せず確認パネルを開く（明示確認・IADR-0090 決定 3）。
    setPendingKey(keyOf(target));
    setDeleteReason('');
    setDeleteState('idle');
    setDeleteError(null);
  }

  function cancelDelete(): void {
    setPendingKey(null);
    setDeleteReason('');
    setDeleteError(null);
  }

  async function confirmDelete(target: MonitoredSymbol): Promise<void> {
    // 理由必須を確定の前提にする（ボタン無効化と二重の防御・安全既定）。
    if (deleteReason.trim() === '' || deleteState === 'saving') return;
    setDeleteState('saving');
    setDeleteError(null);
    try {
      // DELETE /monitor/watchlist（body に { symbol, market, reason }）。不在削除はサーバ 400（#191）。
      await apiFetch<MonitoredSymbol[]>('/monitor/watchlist', {
        method: 'DELETE',
        json: { symbol: target.symbol, market: target.market, reason: deleteReason.trim() },
      });
      // 成功後は確認を閉じ、一覧・履歴を再取得する。
      setPendingKey(null);
      setDeleteReason('');
      setDeleteState('idle');
      setAddNotice(null);
      await loadWatchlist();
      await loadHistory();
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（確認パネルは開いたまま・安全既定）。
      setDeleteState('error');
      setDeleteError(messageOf(err));
    }
  }

  // 確認中の対象（一覧の再取得で消えていたら確認を閉じる）。
  const pendingTarget = pendingKey === null ? undefined : symbols.find((s) => keyOf(s) === pendingKey);

  return (
    <Section title="監視銘柄">
      <p>
        監視対象の銘柄を一覧・追加・削除します（FR-03/FR-13）。追加・削除は理由必須です。削除は監視から外す破壊的操作のため、
        確認のうえ実行します。市場ごとに管理します。
      </p>

      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'notFound' && <p>監視銘柄設定は利用できません。</p>}
      {status === 'error' && <p role="alert">監視銘柄の取得に失敗しました。</p>}

      {status === 'ok' && (
        <>
          {symbols.length === 0 ? (
            <p>監視銘柄はありません。</p>
          ) : (
            <table aria-label="監視銘柄">
              <thead>
                <tr>
                  <th>銘柄</th>
                  <th>市場</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {symbols.map((s) => (
                  <tr key={keyOf(s)}>
                    <td>{s.symbol}</td>
                    <td>{marketLabel(s.market)}</td>
                    <td>
                      <button type="button" onClick={() => beginDelete(s)} disabled={pendingKey !== null}>
                        削除
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {pendingTarget && (
            <div role="group" aria-label="監視銘柄の削除確認">
              <p role="alert">
                {pendingTarget.symbol}（{marketLabel(pendingTarget.market)}）を監視から削除します。理由を入力して確定してください。
              </p>
              <label htmlFor="wl-delete-reason">削除理由</label>
              {/* 削除理由は確認パネル内でのみ必須にする。追加フォームの送信を妨げないよう独立した入力にする。 */}
              <textarea
                id="wl-delete-reason"
                value={deleteReason}
                onChange={(e) => setDeleteReason(e.target.value)}
                required
              />
              <button
                type="button"
                onClick={() => void confirmDelete(pendingTarget)}
                disabled={deleteReason.trim() === '' || deleteState === 'saving'}
              >
                監視から削除
              </button>
              <button type="button" onClick={cancelDelete} disabled={deleteState === 'saving'}>
                キャンセル
              </button>
              {deleteState === 'saving' && <span role="status">削除中…</span>}
              {deleteError && <p role="alert">{deleteError}</p>}
            </div>
          )}

          <form onSubmit={handleAdd} aria-label="監視銘柄の追加">
            <fieldset>
              <legend>監視銘柄を追加</legend>
              <div>
                <label htmlFor="wl-new-symbol">監視銘柄コード</label>
                <input id="wl-new-symbol" value={newSymbol} onChange={(e) => setNewSymbol(e.target.value)} />
              </div>
              <div>
                <label htmlFor="wl-new-market">監視銘柄の市場</label>
                <select id="wl-new-market" value={newMarket} onChange={(e) => setNewMarket(Number(e.target.value))}>
                  {MARKET_OPTIONS.map((o) => (
                    <option key={`wl-mk-${o.value}`} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </select>
              </div>
              <div>
                <label htmlFor="wl-new-reason">追加理由</label>
                <textarea id="wl-new-reason" value={newReason} onChange={(e) => setNewReason(e.target.value)} required />
              </div>
              <button type="submit" disabled={!canAdd}>
                監視銘柄を追加
              </button>
              {addState === 'saving' && <span role="status">追加中…</span>}
              {addNotice && <p role="status">{addNotice}</p>}
              {addError && <p role="alert">{addError}</p>}
            </fieldset>
          </form>

          <HistoryView status={historyStatus} history={history} />
        </>
      )}
    </Section>
  );
}

// FR-11, ADR-0007: 監視銘柄の変更履歴（新しい順）。取得不能・0 件はその旨を明示する（縮退表示）。
function HistoryView({ status, history }: { status: HistoryStatus; history: MonitorSettingsChangeEntry[] }) {
  return (
    <details open style={{ margin: '0.5rem 0' }}>
      <summary style={{ cursor: 'pointer', fontWeight: 600 }}>
        監視銘柄の変更履歴（{status === 'ok' ? history.length : '—'}）
      </summary>
      <div style={{ marginTop: '0.5rem' }}>
        {status === 'loading' && <p role="status">履歴を確認中…</p>}
        {status === 'unavailable' && <p>変更履歴は利用できません。</p>}
        {status === 'ok' && history.length === 0 && <p>変更履歴はありません。</p>}
        {status === 'ok' && history.length > 0 && (
          <table aria-label="監視銘柄の変更履歴">
            <thead>
              <tr>
                <th>種別</th>
                <th>変更者</th>
                <th>理由</th>
                <th>日時</th>
              </tr>
            </thead>
            <tbody>
              {history.map((h, i) => (
                <tr key={`${i}-${h.changeType}-${h.changedAt}`}>
                  <td>{monitorChangeTypeLabel(h.changeType)}</td>
                  <td>{h.actor}</td>
                  <td>{h.reason}</td>
                  <td>{formatAt(h.changedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </details>
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
