import type { ReactNode } from 'react';
import { useState } from 'react';
import { ApiError } from '@foundation/api/ApiError';
import { formatAt, marketLabel, MARKET_OPTIONS } from '@ai-stock-trading/lib/risk/contracts';
import type { MonitoredSymbol, MonitorSettingsChangeEntry } from '@ai-stock-trading/lib/monitor/contracts';
import { monitorChangeTypeLabel } from '@ai-stock-trading/lib/monitor/contracts';
import {
  useAddWatchlistSymbol,
  useRemoveWatchlistSymbol,
  useWatchlist,
  useWatchlistHistory,
} from '@ai-stock-trading/lib/monitor/queries';

// SC-02, FR-13, FR-03, FR-11, UC-06, IADR-0088, IADR-0090: 監視銘柄（watchlist）の一覧表示・追加・削除。
// データ源は MarketMonitorService `/monitor/watchlist`（OwnerOnly・PR #195）。リスク設定（RiskManagementService）とは
// 別サービスのため、本セクションは自前でロード/縮退し、リスク設定の取得可否に連動しない（fail-safe な疎結合・IADR-0090 決定 1）。
// 追加/削除は個別操作 API を消費し（全置換しない・決定 2）、いずれも理由必須。削除は破壊的なため明示確認を要求する（決定 3）。
// 検証(400)・競合(409) はメッセージ表示に留め、破壊的な自動再試行はしない（安全既定）。market は数値 enum を写像する（決定 4）。

type Status = 'loading' | 'ok' | 'notFound' | 'error';
type HistoryStatus = 'loading' | 'ok' | 'unavailable';

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
  // IADR-0288: 取得・更新は TanStack Query（`@ai-stock-trading/lib/monitor/queries`）が持つ。
  const watchlistQuery = useWatchlist();
  const historyQuery = useWatchlistHistory();
  const addSymbol = useAddWatchlistSymbol();
  const removeSymbol = useRemoveWatchlistSymbol();

  // 追加の下書き。
  const [newSymbol, setNewSymbol] = useState('');
  const [newMarket, setNewMarket] = useState<number>(MARKET_OPTIONS[0]?.value ?? 0);
  const [newReason, setNewReason] = useState('');
  const [addError, setAddError] = useState<string | null>(null);
  const [addNotice, setAddNotice] = useState<string | null>(null);

  // 削除の明示確認（対象キー・理由・状態）。pendingKey が null の間は確認パネルを開かない。
  const [pendingKey, setPendingKey] = useState<string | null>(null);
  const [deleteReason, setDeleteReason] = useState('');
  const [deleteError, setDeleteError] = useState<string | null>(null);

  const symbols: MonitoredSymbol[] = watchlistQuery.data ?? [];
  // 404 は不在/秘匿を区別しない（IADR-0009）。BFF 未結線（/monitor/* 未プロキシ）も安全側に縮退する。
  const status: Status = watchlistQuery.isPending
    ? 'loading'
    : watchlistQuery.isError
      ? watchlistQuery.error instanceof ApiError && watchlistQuery.error.kind === 'notFound'
        ? 'notFound'
        : 'error'
      : 'ok';
  // 履歴の取得不能はその領域のみ縮退する（一覧・追加/削除と疎結合）。
  const historyStatus: HistoryStatus = historyQuery.isPending
    ? 'loading'
    : historyQuery.isError
      ? 'unavailable'
      : 'ok';
  const history: MonitorSettingsChangeEntry[] = historyQuery.data ?? [];

  const canAdd = newSymbol.trim() !== '' && newReason.trim() !== '' && !addSymbol.isPending;

  async function handleAdd(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    // 理由必須・銘柄コード必須を送信の前提にする（ボタン無効化と二重の防御・安全既定）。
    if (!canAdd) return;
    setAddError(null);
    setAddNotice(null);
    try {
      // POST /monitor/watchlist（{ symbol, market, reason }）。重複追加・空・未定義 market はサーバ 400（#191）。
      // 成功後の一覧・履歴の再取得は mutation がキャッシュの無効化として行う。破壊的操作はしない。
      await addSymbol.mutateAsync({
        symbol: newSymbol.trim(),
        market: newMarket,
        reason: newReason.trim(),
      });
      setNewSymbol('');
      setNewReason('');
      setAddNotice('追加しました。');
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setAddError(messageOf(err));
    }
  }

  function beginDelete(target: MonitoredSymbol): void {
    // 削除は破壊的なため、行の「削除」では確定せず確認パネルを開く（明示確認・IADR-0090 決定 3）。
    setPendingKey(keyOf(target));
    setDeleteReason('');
    setDeleteError(null);
  }

  function cancelDelete(): void {
    setPendingKey(null);
    setDeleteReason('');
    setDeleteError(null);
  }

  async function confirmDelete(target: MonitoredSymbol): Promise<void> {
    // 理由必須を確定の前提にする（ボタン無効化と二重の防御・安全既定）。
    if (deleteReason.trim() === '' || removeSymbol.isPending) return;
    setDeleteError(null);
    try {
      // DELETE /monitor/watchlist（body に { symbol, market, reason }）。不在削除はサーバ 400（#191）。
      // 成功後の一覧・履歴の再取得は mutation がキャッシュの無効化として行う。
      await removeSymbol.mutateAsync({
        symbol: target.symbol,
        market: target.market,
        reason: deleteReason.trim(),
      });
      setPendingKey(null);
      setDeleteReason('');
      setAddNotice(null);
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（確認パネルは開いたまま・安全既定）。
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
                disabled={deleteReason.trim() === '' || removeSymbol.isPending}
              >
                監視から削除
              </button>
              <button type="button" onClick={cancelDelete} disabled={removeSymbol.isPending}>
                キャンセル
              </button>
              {removeSymbol.isPending && <span role="status">削除中…</span>}
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
              {addSymbol.isPending && <span role="status">追加中…</span>}
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

// FR-11, FR-13: 監視銘柄の変更履歴（新しい順）。取得不能・0 件はその旨を明示する（縮退表示）。
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
