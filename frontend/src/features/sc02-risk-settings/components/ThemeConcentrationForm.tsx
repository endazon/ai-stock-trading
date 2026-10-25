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
  StatusBadge,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import { validationMessageOf } from '@ai-stock-trading/lib/validationReasons';
import type { ThemeConcentrationControlView, ThemeView, WatchlistMatch } from '@ai-stock-trading/lib/risk/contracts';
import {
  CONTROL_VALUE_SOURCE_SAVED,
  MARKET_OPTIONS,
  marketLabel,
  normalizeDesignatedSymbol,
  themeDraftErrors,
  themeLoosenings,
  validateDesignatedSymbolInput,
  watchlistMatchOf,
} from '@ai-stock-trading/lib/risk/contracts';
import type { MonitoredSymbol } from '@ai-stock-trading/lib/monitor/contracts';
import { useSaveThemeConcentration, useThemeConcentration } from '@ai-stock-trading/lib/risk/queries';
import { useWatchlist } from '@ai-stock-trading/lib/monitor/queries';

// 🔴 SC-02, FR-10, FR-19, UC-06, ADR-0071 決定 2・5, #1407, IADR-0564 決定 3・6:
// **テーマ（相関の高い銘柄の集まり）の定義（名前・市場・銘柄の一覧）と同時保有の上限の表示・設定。**
//
// 手続きは §5 の他の統制値と同じ（所有者だけ・変更理由は必須・値域〔ADR-0071 決定 2〕の外は拒否・履歴）。**拒否の実効はサーバが持つ。**
// 保存は定義の全体を置き換える（構成の定義と同じく既定を丸ごと置き換える）。
// 画面は**その時点で効いている定義**を表示し、どちらが効いているか（構成／画面で保存した値）と**構成の定義との食い違い**を示す。
//
// **銘柄コードの誤記を防ぐ手段（ADR-0071 決定 5。方式は IADR-0530 決定 1 と同じ）**: 誤記した銘柄はどのテーマにも属さず、上限が黙って効かない。
//   1. 主の経路は監視銘柄からの選択（テーマの市場の監視銘柄だけを出す）。
//   2. 監視銘柄に無い銘柄の手入力は、書式で拒み、監視銘柄と照合し、無ければ警告して**確認の印を付けるまで追加させない**。
//   3. 一覧の各銘柄にも照合の印を出す（構成や API で入った誤記を画面で見つけられる）。
// **緩める変更（テーマ・銘柄を外す・上限を上げる・市場を変える）は列挙して確認を求める**（IADR-0086 決定 3）。

// Market enum（0=日本, 1=米国）。新しいテーマの既定の市場（日本株は無効。ADR-0062）。
const MARKET_UNITED_STATES = 1;

function saveMessageOf(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.kind === 'conflict') {
      return i18n._(msg`競合が発生しました。最新を取得して再試行してください。`);
    }
    if (e.kind === 'validation') {
      // #1465, IADR-0585: 本ユニットの 400 の本文（`{ error, details }`）からサーバの理由を出す。
      return validationMessageOf(e, i18n._(msg`入力内容に誤りがあります。`));
    }
    if (e.kind === 'forbidden') {
      return i18n._(msg`変更する権限がありません。`);
    }
    return e.message;
  }
  return i18n._(msg`保存に失敗しました。`);
}

function matchText(match: WatchlistMatch): string | null {
  switch (match) {
    case 'listed':
      return null;
    case 'otherMarket':
      return i18n._(msg`監視銘柄では市場が違います`);
    case 'notListed':
      return i18n._(msg`監視銘柄にありません`);
    case 'unknown':
      return i18n._(msg`監視銘柄と照合できません`);
  }
}

function summaryOf(themes: readonly ThemeView[]): string {
  if (themes.length === 0) return i18n._(msg`テーマなし`);
  return themes
    .map((t) => `${t.name}（${marketLabel(t.market)}・${i18n._(msg`上限`)} ${t.maxPositions}）: ${t.symbols.join('・')}`)
    .join(' / ');
}

function cloneThemes(themes: readonly ThemeView[]): ThemeView[] {
  return themes.map((t) => ({ ...t, symbols: [...t.symbols] }));
}

/** 1 つのテーマへ銘柄を足す区画（監視銘柄から選ぶ／手入力は書式・照合・確認の 3 段）。 */
function ThemeSymbolAdder({
  index,
  theme,
  watchlist,
  watchlistPending,
  onAdd,
}: {
  index: number;
  theme: ThemeView;
  watchlist: MonitoredSymbol[] | null;
  watchlistPending: boolean;
  onAdd: (symbol: string) => void;
}) {
  const [pick, setPick] = useState('');
  const [manual, setManual] = useState('');
  const [confirmedKey, setConfirmedKey] = useState<string | null>(null);

  const inTheme = new Set(theme.symbols.map(normalizeDesignatedSymbol));
  const candidates = (watchlist ?? []).filter(
    (w) => w.market === theme.market && !inTheme.has(normalizeDesignatedSymbol(w.symbol)),
  );
  const normalized = normalizeDesignatedSymbol(manual);
  const manualError = manual.trim() === '' ? null : validateDesignatedSymbolInput(manual);
  const duplicate = normalized !== '' && inTheme.has(normalized);
  const match = normalized === '' ? null : watchlistMatchOf(normalized, theme.market, watchlist);
  const key = `${normalized}|${theme.market}`;
  const confirmed = confirmedKey !== null && confirmedKey === key;
  const needsConfirm = !watchlistPending && match !== null && match !== 'listed';
  const canAdd =
    !watchlistPending && normalized !== '' && manualError === null && !duplicate && (!needsConfirm || confirmed);

  return (
    <div className="mt-2 flex flex-wrap items-end gap-2">
      <div>
        <Label htmlFor={`tc-pick-${index}`}>{i18n._(msg`監視銘柄から追加`)}</Label>
        <Select id={`tc-pick-${index}`} value={pick} onChange={(e) => setPick(e.target.value)}>
          <option value="">{i18n._(msg`（選択してください）`)}</option>
          {candidates.map((w) => (
            <option key={w.symbol} value={normalizeDesignatedSymbol(w.symbol)}>
              {w.symbol}
            </option>
          ))}
        </Select>
      </div>
      <Button
        type="button"
        disabled={pick === ''}
        onClick={() => {
          onAdd(pick);
          setPick('');
        }}
      >
        {i18n._(msg`選んだ監視銘柄を追加`)}
      </Button>
      <div>
        <Label htmlFor={`tc-manual-${index}`}>{i18n._(msg`銘柄コード（手入力）`)}</Label>
        <Input id={`tc-manual-${index}`} value={manual} onChange={(e) => setManual(e.target.value)} />
      </div>
      <Button
        type="button"
        disabled={!canAdd}
        onClick={() => {
          if (!canAdd) return;
          onAdd(normalized);
          setManual('');
          setConfirmedKey(null);
        }}
      >
        {i18n._(msg`手入力の銘柄を追加`)}
      </Button>
      {manualError !== null && (
        <p role="alert" className="w-full text-[11px] text-danger">
          {manualError}
        </p>
      )}
      {duplicate && (
        <p role="alert" className="w-full text-[11px] text-danger">
          {i18n._(msg`この銘柄は既にこのテーマにあります。`)}
        </p>
      )}
      {manualError === null && !duplicate && needsConfirm && match !== null && (
        <div role="alert" className="w-full rounded-md border border-danger p-2">
          <p className="text-[11px] text-danger">
            {`⚠ ${normalized}（${marketLabel(theme.market)}）: ${matchText(match) ?? ''}。${i18n._(
              msg`銘柄コードを誤記すると、その銘柄はどのテーマにも属さず、上限が黙って効きません。綴りと市場を確かめてください。`,
            )}`}
          </p>
          <label className="mt-1 flex items-center gap-2 text-[11px]">
            <input type="checkbox" checked={confirmed} onChange={() => setConfirmedKey(confirmed ? null : key)} />
            {i18n._(msg`綴りと市場を確かめた（監視銘柄に無い銘柄としてテーマに入れる）`)}
          </label>
        </div>
      )}
    </div>
  );
}

export function ThemeConcentrationForm({ view }: { view: ThemeConcentrationControlView }) {
  const save = useSaveThemeConcentration();
  const watchlistQuery = useWatchlist();
  const watchlist = watchlistQuery.isSuccess ? watchlistQuery.data : null;
  const watchlistPending = watchlistQuery.isPending;

  const [themes, setThemes] = useState<ThemeView[]>(() => cloneThemes(view.effective));
  // PR #1409 のレビュー: 区画の key は位置ではなく安定した番号にする（途中のテーマを外したとき、後ろの区画の入力の下書きが前へずれない）。
  const [themeIds, setThemeIds] = useState<number[]>(() => view.effective.map((_, i) => i));
  const [nextThemeId, setNextThemeId] = useState(view.effective.length);
  const [reason, setReason] = useState('');
  const [confirmLoosening, setConfirmLoosening] = useState(false);
  const [confirmedSignature, setConfirmedSignature] = useState('');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);

  // 現在値が実際に変わったときだけ初期化する（#498 / #539: useEffect で行わない）。
  const signature = JSON.stringify(view);
  const [syncedSignature, setSyncedSignature] = useState(signature);
  if (syncedSignature !== signature) {
    setSyncedSignature(signature);
    setThemes(cloneThemes(view.effective));
    setThemeIds(view.effective.map((_, i) => i));
    setNextThemeId(view.effective.length);
    setReason('');
    setConfirmLoosening(false);
  }

  const errors = themeDraftErrors(themes);
  const changed = JSON.stringify(themes) !== JSON.stringify(view.effective);
  const loosenings = themeLoosenings(view.effective, themes);
  const looseningSignature = loosenings.join('｜');
  const looseningConfirmed = confirmLoosening && confirmedSignature === looseningSignature;
  const blocked =
    errors.length > 0 ||
    !changed ||
    reason.trim() === '' ||
    save.isPending ||
    (loosenings.length > 0 && !looseningConfirmed);

  function update(index: number, next: Partial<ThemeView>): void {
    setThemes(themes.map((t, i) => (i === index ? { ...t, ...next } : t)));
  }

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    if (blocked) return;
    setSaveError(null);
    setSavedNotice(null);
    try {
      await save.mutateAsync({
        themes: themes.map((t) => ({
          name: t.name.trim(),
          market: t.market,
          symbols: t.symbols.map(normalizeDesignatedSymbol),
          maxPositions: t.maxPositions,
        })),
        reason: reason.trim(),
      });
      setSavedNotice(i18n._(msg`テーマの定義を保存しました。`));
    } catch (err: unknown) {
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <Panel heading={i18n._(msg`テーマごとの同時保有の上限（変更）`)}>
      <Note>
        {i18n._(
          msg`同じテーマの銘柄は、保有中と未約定の新規建てを合わせて上限の数までしか持てません（同じ銘柄の買い増しは数を増やしません）。どのテーマにも属さない銘柄には掛かりません。方針の文では変えられません。`,
        )}
      </Note>
      <div className="mt-2 flex flex-wrap items-center gap-2 text-[11px]">
        {view.source === CONTROL_VALUE_SOURCE_SAVED ? (
          <StatusBadge tone="success">{i18n._(msg`画面で保存した定義が効いています`)}</StatusBadge>
        ) : (
          <StatusBadge tone="neutral">{i18n._(msg`構成の定義が効いています`)}</StatusBadge>
        )}
      </div>
      {view.drift && (
        <Note tone="err">
          {`${i18n._(msg`保存した定義が構成の定義と食い違っています（画面で保存した定義が効いています）。構成の定義:`)} ${summaryOf(view.configured)}`}
        </Note>
      )}
      {watchlist === null && !watchlistPending && (
        <Note tone="err">
          {i18n._(msg`監視銘柄を取得できないため、銘柄コードを監視銘柄と照合できません。手入力の追加には確認が要ります。`)}
        </Note>
      )}

      <form onSubmit={handleSubmit} aria-label={i18n._(msg`テーマの定義の変更`)}>
        {themes.length === 0 && (
          <p className="mt-2 text-[11px] text-fg-muted">{i18n._(msg`テーマはありません（上限は掛かりません）。`)}</p>
        )}
        {themes.map((theme, index) => (
          <fieldset key={themeIds[index] ?? `new-${index}`} className="mt-2 rounded-md border border-border p-2">
            <legend className="text-[11px]">{`${i18n._(msg`テーマ`)} ${index + 1}`}</legend>
            <div className="flex flex-wrap items-end gap-2">
              <div>
                <Label htmlFor={`tc-name-${index}`}>{i18n._(msg`テーマの名前`)}</Label>
                <Input id={`tc-name-${index}`} value={theme.name} onChange={(e) => update(index, { name: e.target.value })} />
              </div>
              <div>
                <Label htmlFor={`tc-market-${index}`}>{i18n._(msg`テーマの市場`)}</Label>
                <Select
                  id={`tc-market-${index}`}
                  value={theme.market}
                  onChange={(e) => update(index, { market: Number(e.target.value) })}
                >
                  {MARKET_OPTIONS.map((o) => (
                    <option key={o.value} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </Select>
              </div>
              <div>
                <Label htmlFor={`tc-max-${index}`}>{i18n._(msg`同時保有の上限（銘柄数）`)}</Label>
                <Input
                  id={`tc-max-${index}`}
                  type="number"
                  min={1}
                  value={Number.isFinite(theme.maxPositions) ? String(theme.maxPositions) : ''}
                  onChange={(e) => update(index, { maxPositions: e.target.value.trim() === '' ? Number.NaN : Number(e.target.value) })}
                />
              </div>
              <Button
                type="button"
                onClick={() => {
                  setThemes(themes.filter((_, i) => i !== index));
                  setThemeIds(themeIds.filter((_, i) => i !== index));
                }}
                aria-label={`${theme.name || `${i18n._(msg`テーマ`)} ${index + 1}`}${i18n._(msg`を外す`)}`}
              >
                {i18n._(msg`テーマを外す`)}
              </Button>
            </div>

            {theme.symbols.length === 0 ? (
              <p className="mt-2 text-[11px] text-fg-muted">{i18n._(msg`銘柄がありません。`)}</p>
            ) : (
              <Table aria-label={`${theme.name}${i18n._(msg`の銘柄の一覧`)}`}>
                <TableHead>
                  <TableRow>
                    <TableHeaderCell>{i18n._(msg`銘柄コード`)}</TableHeaderCell>
                    <TableHeaderCell>{i18n._(msg`監視銘柄との照合`)}</TableHeaderCell>
                    <TableHeaderCell>{i18n._(msg`操作`)}</TableHeaderCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {theme.symbols.map((symbol, si) => {
                    const note = matchText(watchlistMatchOf(normalizeDesignatedSymbol(symbol), theme.market, watchlist));
                    return (
                      <TableRow key={`${symbol}#${si}`}>
                        <TableCell>{symbol}</TableCell>
                        <TableCell>
                          {watchlistPending ? (
                            <StatusBadge tone="neutral">{i18n._(msg`照合中`)}</StatusBadge>
                          ) : note === null ? (
                            <StatusBadge tone="success">{i18n._(msg`監視銘柄にあります`)}</StatusBadge>
                          ) : (
                            <StatusBadge tone="warning">{note}</StatusBadge>
                          )}
                        </TableCell>
                        <TableCell>
                          <Button
                            type="button"
                            size="sm"
                            onClick={() => update(index, { symbols: theme.symbols.filter((_, j) => j !== si) })}
                            aria-label={`${symbol}${i18n._(msg`をテーマから外す`)}`}
                          >
                            {i18n._(msg`外す`)}
                          </Button>
                        </TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
            )}

            <ThemeSymbolAdder
              index={index}
              theme={theme}
              watchlist={watchlist}
              watchlistPending={watchlistPending}
              onAdd={(symbol) => update(index, { symbols: [...theme.symbols, symbol] })}
            />
          </fieldset>
        ))}

        <div className="mt-2">
          <Button
            type="button"
            onClick={() => {
              setThemes([...themes, { name: '', market: MARKET_UNITED_STATES, symbols: [], maxPositions: 1 }]);
              setThemeIds([...themeIds, nextThemeId]);
              setNextThemeId(nextThemeId + 1);
            }}
          >
            {i18n._(msg`テーマを足す`)}
          </Button>
        </div>

        <div className="mt-2">
          <Label htmlFor="tc-reason">{i18n._(msg`テーマの定義の変更理由`)}</Label>
          <Input id="tc-reason" value={reason} onChange={(e) => setReason(e.target.value)} required />
        </div>

        {errors.length > 0 && (
          <p role="alert" className="mt-2 text-[11px] text-danger">
            {errors.join(' ')}
          </p>
        )}

        {loosenings.length > 0 && (
          <div role="alert" className="mt-2 rounded-md border border-danger p-2">
            <p className="text-[11px] text-danger">
              {`${i18n._(msg`次の変更はテーマの上限を緩めます。内容を確認してください:`)} ${loosenings.join('、')}`}
            </p>
            <label className="mt-1 flex items-center gap-2 text-[11px]">
              <input
                type="checkbox"
                checked={looseningConfirmed}
                onChange={() => {
                  setConfirmLoosening(!looseningConfirmed);
                  setConfirmedSignature(looseningSignature);
                }}
              />
              {i18n._(msg`上記の緩める変更を確認しました`)}
            </label>
          </div>
        )}

        <div className="mt-2 flex flex-wrap items-center gap-3">
          <Button type="submit" variant="primary" disabled={blocked}>
            {i18n._(msg`テーマの定義を保存`)}
          </Button>
          {save.isPending && <span role="status">{i18n._(msg`テーマの定義を保存中…`)}</span>}
        </div>
        {savedNotice !== null && (
          <p role="status" className="mt-2 text-[11px] text-success">
            {savedNotice}
          </p>
        )}
        {saveError !== null && (
          <p role="alert" className="mt-2 text-[11px] text-danger">
            {saveError}
          </p>
        )}
      </form>
    </Panel>
  );
}

/**
 * テーマごとの同時保有の上限の区画（取得・縮退つき）。取得に失敗しても他の区画を巻き込まない（独立して縮退する）。
 * 🔴 取得できないときに既定値で入力欄を出さない（効いている値が分からないまま保存させない）。
 */
export function ThemeConcentrationFormSection() {
  const query = useThemeConcentration();
  if (query.isPending) {
    return (
      <Panel heading={i18n._(msg`テーマごとの同時保有の上限（変更）`)}>
        <p role="status" className="text-[11px] text-fg-muted">
          {i18n._(msg`テーマごとの同時保有の上限を読み込み中…`)}
        </p>
      </Panel>
    );
  }
  if (query.isError) {
    return (
      <Panel heading={i18n._(msg`テーマごとの同時保有の上限（変更）`)}>
        <Note tone="err">{i18n._(msg`テーマごとの同時保有の上限を取得できません。効いている値が分からないため、変更できません。`)}</Note>
      </Panel>
    );
  }
  return <ThemeConcentrationForm view={query.data} />;
}
