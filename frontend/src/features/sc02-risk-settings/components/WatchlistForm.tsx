import { useState } from 'react';
import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import {
  Button,
  Input,
  Label,
  Note,
  Panel,
  Select,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tag,
} from '@platform/ui';
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
import { QueryPhase } from '@ai-stock-trading/components/QueryPhase';

// SC-02, FR-13, FR-03, FR-11, UC-06, IADR-0088, IADR-0090: 監視銘柄（watchlist）の一覧表示・追加・削除。
// データ源は MarketMonitorService `/monitor/watchlist`（OwnerOnly・PR #195）。リスク設定（RiskManagementService）とは
// 別サービスのため、本セクションは自前でロード/縮退し、リスク設定の取得可否に連動しない（fail-safe な疎結合・IADR-0090 決定 1）。
// 追加/削除は個別操作 API を消費し（全置換しない・決定 2）、いずれも理由必須。削除は破壊的なため明示確認を要求する（決定 3）。
// 検証(400)・競合(409) はメッセージ表示に留め、破壊的な自動再試行はしない（安全既定）。market は数値 enum を写像する（決定 4）。
//
// UI/UX 改善 2026-09-12（hi-fi モック `sc-02.html` の「監視銘柄（watchlist）」）: `Panel` ＋ `Table` ＋
// `Tag` へ載せ替え、**取得の待ち・失敗は `QueryPhase` に一本化**した。
//
// 🔴 **`isEmpty` は使わない。** 0 件のときも追加フォームは描く必要がある（`isEmpty` は子全体を
// 置き換えてしまう）。**0 件と失敗の区別は保つ**——空の判定は成功したデータに対してだけ行い、
// 「監視銘柄はありません。」は成功経路の中でしか描かれない。

// (Symbol, Market) の同一性キー。区切りは銘柄コードに現れない縦棒を用いる（削除確認の対象特定に使う）。
function keyOf(s: MonitoredSymbol): string {
  return `${s.symbol}|${s.market}`;
}

// ApiError の種別を利用者向けメッセージへ写像する（RiskSettingsPage の saveMessageOf と同方針）。
function messageOf(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.kind === 'conflict') {
      return i18n._(msg`競合が発生しました。最新を取得して再試行してください。`);
    }
    if (e.kind === 'validation') {
      const detail = e.details.length > 0 ? `（${e.details.join(' / ')}）` : '';
      return `${i18n._(msg`入力内容に誤りがあります。`)}${detail}`;
    }
    if (e.kind === 'forbidden') {
      return i18n._(msg`変更する権限がありません。`);
    }
    return e.message;
  }
  return i18n._(msg`操作に失敗しました。`);
}

/** 404（BFF 未結線・不在の秘匿）かどうか。**再試行しても直らない**ため導線を出さない（IADR-0009）。 */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'notFound';
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
      setAddNotice(i18n._(msg`追加しました。`));
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

  const watchlistNotFound = watchlistQuery.isError && isNotFound(watchlistQuery.error);

  return (
    <Panel heading={i18n._(msg`監視銘柄`)}>
      <Note>
        {i18n._(
          msg`監視対象の銘柄を一覧・追加・削除します（FR-03/FR-13）。追加・削除は理由必須です。削除は監視から外す破壊的操作のため、確認のうえ実行します。市場ごとに管理します。`,
        )}
      </Note>

      <QueryPhase
        query={watchlistQuery}
        errorTitle={
          watchlistNotFound
            ? i18n._(msg`監視銘柄設定は利用できません。`)
            : i18n._(msg`監視銘柄の取得に失敗しました。`)
        }
        canRetry={!watchlistNotFound}
      >
        {(symbols: MonitoredSymbol[]) => {
          // 確認中の対象（一覧の再取得で消えていたら確認を閉じる）。
          const pendingTarget =
            pendingKey === null ? undefined : symbols.find((s) => keyOf(s) === pendingKey);
          return (
            <>
              {symbols.length === 0 ? (
                <Note>{i18n._(msg`監視銘柄はありません。`)}</Note>
              ) : (
                <>
                  {/* モックの `.row` に並ぶ銘柄チップ（分類の名前であり状態ではないので `Tag`）。 */}
                  <div className="mb-2 flex flex-wrap gap-1.5">
                    {symbols.map((s) => (
                      <Tag key={`chip-${keyOf(s)}`} tone="accent">
                        {s.symbol}
                      </Tag>
                    ))}
                  </div>
                  <Table aria-label={i18n._(msg`監視銘柄`)}>
                    <TableHead>
                      <TableRow>
                        <TableHeaderCell>{i18n._(msg`銘柄`)}</TableHeaderCell>
                        <TableHeaderCell>{i18n._(msg`市場`)}</TableHeaderCell>
                        <TableHeaderCell>{i18n._(msg`操作`)}</TableHeaderCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {symbols.map((s) => (
                        <TableRow key={keyOf(s)}>
                          <TableCell>{s.symbol}</TableCell>
                          <TableCell>{marketLabel(s.market)}</TableCell>
                          <TableCell>
                            <Button
                              type="button"
                              onClick={() => beginDelete(s)}
                              disabled={pendingKey !== null}
                            >
                              {i18n._(msg`削除`)}
                            </Button>
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </>
              )}

              {pendingTarget && (
                <div
                  role="group"
                  aria-label={i18n._(msg`監視銘柄の削除確認`)}
                  className="mt-2 rounded-md border border-danger p-2"
                >
                  {/* 破壊的操作の確認は**利用者の操作に対する結果通知**であり `role="alert"` を保つ。 */}
                  <p role="alert" className="text-[11px] text-danger">
                    {`${pendingTarget.symbol}（${marketLabel(pendingTarget.market)}）${i18n._(msg`を監視から削除します。理由を入力して確定してください。`)}`}
                  </p>
                  <Label htmlFor="wl-delete-reason">{i18n._(msg`削除理由`)}</Label>
                  {/* 削除理由は確認パネル内でのみ必須にする。追加フォームの送信を妨げないよう独立した入力にする。 */}
                  <Input
                    id="wl-delete-reason"
                    value={deleteReason}
                    onChange={(e) => setDeleteReason(e.target.value)}
                    required
                    className="mt-1 w-full"
                  />
                  <div className="mt-2 flex flex-wrap items-center gap-2">
                    <Button
                      type="button"
                      variant="danger"
                      onClick={() => void confirmDelete(pendingTarget)}
                      disabled={deleteReason.trim() === '' || removeSymbol.isPending}
                    >
                      {i18n._(msg`監視から削除`)}
                    </Button>
                    <Button type="button" onClick={cancelDelete} disabled={removeSymbol.isPending}>
                      {i18n._(msg`キャンセル`)}
                    </Button>
                    {removeSymbol.isPending && <span role="status">{i18n._(msg`削除中…`)}</span>}
                  </div>
                  {deleteError !== null && (
                    <p role="alert" className="mt-2 text-[11px] text-danger">
                      {deleteError}
                    </p>
                  )}
                </div>
              )}

              <form onSubmit={handleAdd} aria-label={i18n._(msg`監視銘柄の追加`)} className="mt-3">
                <fieldset className="border-0 p-0">
                  <legend className="text-[10.5px] text-fg-muted">
                    {i18n._(msg`監視銘柄を追加`)}
                  </legend>
                  <div className="flex flex-wrap items-end gap-2">
                    <div>
                      <Label htmlFor="wl-new-symbol">{i18n._(msg`監視銘柄コード`)}</Label>
                      <Input
                        id="wl-new-symbol"
                        value={newSymbol}
                        onChange={(e) => setNewSymbol(e.target.value)}
                      />
                    </div>
                    <div>
                      <Label htmlFor="wl-new-market">{i18n._(msg`監視銘柄の市場`)}</Label>
                      <Select
                        id="wl-new-market"
                        value={newMarket}
                        onChange={(e) => setNewMarket(Number(e.target.value))}
                      >
                        {MARKET_OPTIONS.map((o) => (
                          <option key={`wl-mk-${o.value}`} value={o.value}>
                            {o.label}
                          </option>
                        ))}
                      </Select>
                    </div>
                    <div>
                      <Label htmlFor="wl-new-reason">{i18n._(msg`追加理由`)}</Label>
                      <Input
                        id="wl-new-reason"
                        value={newReason}
                        onChange={(e) => setNewReason(e.target.value)}
                        required
                      />
                    </div>
                    <Button type="submit" variant="primary" disabled={!canAdd}>
                      {i18n._(msg`監視銘柄を追加`)}
                    </Button>
                    {addSymbol.isPending && <span role="status">{i18n._(msg`追加中…`)}</span>}
                  </div>
                  {addNotice !== null && (
                    <p role="status" className="mt-2 text-[11px] text-success">
                      {addNotice}
                    </p>
                  )}
                  {addError !== null && (
                    <p role="alert" className="mt-2 text-[11px] text-danger">
                      {addError}
                    </p>
                  )}
                </fieldset>
              </form>

              <HistoryView query={historyQuery} />
            </>
          );
        }}
      </QueryPhase>
    </Panel>
  );
}

// FR-11, FR-13: 監視銘柄の変更履歴（新しい順）。取得不能・0 件はその旨を明示する（縮退表示）。
function HistoryView({ query }: { query: ReturnType<typeof useWatchlistHistory> }) {
  return (
    <div className="mt-3">
      <h3 className="text-[10.5px] text-fg-muted">{i18n._(msg`監視銘柄の変更履歴`)}</h3>
      <QueryPhase
        query={query}
        loadingLabel={i18n._(msg`履歴を確認中…`)}
        errorTitle={i18n._(msg`変更履歴は利用できません。`)}
        isEmpty={(rows: MonitorSettingsChangeEntry[]) => rows.length === 0}
        empty={<Note>{i18n._(msg`変更履歴はありません。`)}</Note>}
      >
        {(history: MonitorSettingsChangeEntry[]) => (
          <Table aria-label={i18n._(msg`監視銘柄の変更履歴`)}>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{i18n._(msg`種別`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`変更者`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`理由`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`日時`)}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {history.map((h, i) => (
                <TableRow key={`${i}-${h.changeType}-${h.changedAt}`}>
                  <TableCell>{monitorChangeTypeLabel(h.changeType)}</TableCell>
                  <TableCell>{h.actor}</TableCell>
                  <TableCell>{h.reason}</TableCell>
                  <TableCell>{formatAt(h.changedAt)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </QueryPhase>
    </div>
  );
}
