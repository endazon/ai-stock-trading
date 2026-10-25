import { useState } from 'react';
import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import {
  Button,
  Input,
  Label,
  Note,
  Panel,
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
import type { MarketMinutesView, PreCloseControlsView, PreCloseDraft } from '@ai-stock-trading/lib/risk/contracts';
import {
  CONTROL_VALUE_SOURCE_SAVED,
  marketLabel,
  minutesOf,
  preCloseLoosenings,
  validateMinutesInput,
} from '@ai-stock-trading/lib/risk/contracts';
import { usePreCloseControls, useSavePreCloseControls } from '@ai-stock-trading/lib/risk/queries';

// 🔴 SC-02, FR-10, FR-19, UC-06, ADR-0070 決定 5, #1407, IADR-0564 決定 3・6:
// **閉場前の新規建ての締め切り N（市場ごと）と閉場前の手仕舞いの有効・無効と M（市場ごと）の表示・設定。**
//
// 手続きは §5 の他の統制値と同じ（05_screens「設定変更の一般則」）: 所有者だけ・変更理由は必須・値域の外は拒否・履歴を残す。
// **拒否の実効はサーバが持つ**（画面の検証は即時提示のため）。値域の上限はサーバの応答から受け取る（定数を二重に持たない）。
//
// 画面は**その時点で効いている値**を表示する（ADR-0070 決定 5）。効いている値は「画面で保存した値があれば保存値・無ければ構成の値」
// （IADR-0564 決定 2）であり、どちらが効いているかと、**保存値と構成の値の食い違い**を示す（保存した後に構成を変えても効かないことを読めるようにする）。
// 🔴 手仕舞いは**画面で無効にしても方針の「閉場前の手仕舞い: 有効」の行で有効になり得る**（ADR-0070 決定 2）。その旨を示す。
// 統制を緩める変更（N・M を縮める・手仕舞いを無効にする）は列挙して確認を求める（IADR-0086 決定 3 と同じ規律）。

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

function draftOf(view: PreCloseControlsView): PreCloseDraft {
  const toText = (rows: readonly MarketMinutesView[]) =>
    Object.fromEntries(rows.map((r) => [r.market, String(r.minutes)])) as Record<number, string>;
  return {
    cutoff: toText(view.effectiveCutoff.minutes),
    flattenEnabled: view.effectiveFlatten.enabled,
    flatten: toText(view.effectiveFlatten.minutes),
  };
}

function minutesText(rows: readonly MarketMinutesView[]): string {
  return rows.map((r) => `${marketLabel(r.market)} ${r.minutes} 分`).join(' / ');
}

function SourceBadge({ source }: { source: number }) {
  return source === CONTROL_VALUE_SOURCE_SAVED ? (
    <StatusBadge tone="success">{i18n._(msg`画面で保存した値が効いています`)}</StatusBadge>
  ) : (
    <StatusBadge tone="neutral">{i18n._(msg`構成の値が効いています`)}</StatusBadge>
  );
}

export function PreCloseControlsForm({ view }: { view: PreCloseControlsView }) {
  const save = useSavePreCloseControls();
  const [draft, setDraft] = useState<PreCloseDraft>(() => draftOf(view));
  const [reason, setReason] = useState('');
  const [confirmLoosening, setConfirmLoosening] = useState(false);
  const [confirmedSignature, setConfirmedSignature] = useState('');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);

  // 現在値が実際に変わったときだけ初期化する（#498 / #539: useEffect で行わない。描画中に同期的に調整する）。
  const signature = JSON.stringify(view);
  const [syncedSignature, setSyncedSignature] = useState(signature);
  if (syncedSignature !== signature) {
    setSyncedSignature(signature);
    setDraft(draftOf(view));
    setReason('');
    setConfirmLoosening(false);
  }

  const markets = view.effectiveCutoff.minutes.map((r) => r.market);
  const errors: string[] = [];
  for (const market of markets) {
    const max = minutesOf(view.cutoffMaxMinutes, market) ?? 0;
    const cutoffError = validateMinutesInput(draft.cutoff[market] ?? '', `${marketLabel(market)}の締め切り N`, max);
    if (cutoffError !== null) errors.push(cutoffError);
    const flattenError = validateMinutesInput(
      draft.flatten[market] ?? '',
      `${marketLabel(market)}の手仕舞いの M`,
      view.flattenMaxMinutes,
    );
    if (flattenError !== null) errors.push(flattenError);
  }

  const changed =
    JSON.stringify(draft) !== JSON.stringify(draftOf(view));
  const loosenings = errors.length === 0 ? preCloseLoosenings(view, draft) : [];
  const looseningSignature = loosenings.join('｜');
  const looseningConfirmed = confirmLoosening && confirmedSignature === looseningSignature;
  const blocked =
    errors.length > 0 ||
    !changed ||
    reason.trim() === '' ||
    save.isPending ||
    (loosenings.length > 0 && !looseningConfirmed);

  function setMinutes(kind: 'cutoff' | 'flatten', market: number, value: string): void {
    setDraft({ ...draft, [kind]: { ...draft[kind], [market]: value } });
  }

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    if (blocked) return;
    setSaveError(null);
    setSavedNotice(null);
    try {
      await save.mutateAsync({
        cutoffMinutes: markets.map((m) => ({ market: m, minutes: Number(draft.cutoff[m]) })),
        flattenEnabled: draft.flattenEnabled,
        flattenMinutes: markets.map((m) => ({ market: m, minutes: Number(draft.flatten[m]) })),
        reason: reason.trim(),
      });
      setSavedNotice(i18n._(msg`閉場前の統制を保存しました。`));
    } catch (err: unknown) {
      // 400/409 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <Panel heading={i18n._(msg`閉場前の統制（変更）`)}>
      <Note>
        {i18n._(
          msg`閉場の N 分前以降の新規建て（買い増し・売り増しを含む）は審査で拒否します。閉場前の手仕舞いが有効なら、閉場の M 分前以降に保有の全量を成行で手仕舞います（方針の「持ち越し可:」の行の銘柄は残します）。手仕舞いと損切りは止めません。`,
        )}
      </Note>

      <div className="mt-2 flex flex-wrap items-center gap-2 text-[11px]">
        <span>{i18n._(msg`締め切り:`)}</span>
        <SourceBadge source={view.cutoffSource} />
        <span>{i18n._(msg`手仕舞い:`)}</span>
        <SourceBadge source={view.flattenSource} />
      </div>
      {view.cutoffDrift && (
        <Note tone="err">
          {`${i18n._(msg`締め切りの保存値が構成の値と食い違っています（画面で保存した値が効いています）。構成の値:`)} ${minutesText(view.configuredCutoff.minutes)}`}
        </Note>
      )}
      {view.flattenDrift && (
        <Note tone="err">
          {`${i18n._(msg`手仕舞いの保存値が構成の値と食い違っています（画面で保存した値が効いています）。構成の値:`)} ${
            view.configuredFlatten.enabled ? i18n._(msg`有効`) : i18n._(msg`無効`)
          } / ${minutesText(view.configuredFlatten.minutes)}`}
        </Note>
      )}

      <form onSubmit={handleSubmit} aria-label={i18n._(msg`閉場前の統制の変更`)}>
        <Table aria-label={i18n._(msg`市場ごとの閉場前の統制`)}>
          <TableHead>
            <TableRow>
              <TableHeaderCell>{i18n._(msg`市場`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`締め切り N（閉場の何分前から新規建てを拒否するか）`)}</TableHeaderCell>
              <TableHeaderCell>{i18n._(msg`手仕舞いの M（閉場の何分前から手仕舞うか）`)}</TableHeaderCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {markets.map((market) => {
              const max = minutesOf(view.cutoffMaxMinutes, market) ?? 0;
              return (
                <TableRow key={market}>
                  <TableCell>{marketLabel(market)}</TableCell>
                  <TableCell>
                    <Label htmlFor={`pc-cutoff-${market}`} className="sr-only">
                      {`${marketLabel(market)}${i18n._(msg`の締め切り N（分）`)}`}
                    </Label>
                    <Input
                      id={`pc-cutoff-${market}`}
                      type="number"
                      min={1}
                      max={max}
                      value={draft.cutoff[market] ?? ''}
                      onChange={(e) => setMinutes('cutoff', market, e.target.value)}
                    />
                    <span className="mt-1 block text-[10.5px] text-fg-muted">{`1〜${max} ${i18n._(msg`分`)}`}</span>
                  </TableCell>
                  <TableCell>
                    <Label htmlFor={`pc-flatten-${market}`} className="sr-only">
                      {`${marketLabel(market)}${i18n._(msg`の手仕舞いの M（分）`)}`}
                    </Label>
                    <Input
                      id={`pc-flatten-${market}`}
                      type="number"
                      min={1}
                      max={view.flattenMaxMinutes}
                      value={draft.flatten[market] ?? ''}
                      onChange={(e) => setMinutes('flatten', market, e.target.value)}
                    />
                    <span className="mt-1 block text-[10.5px] text-fg-muted">{`1〜${view.flattenMaxMinutes} ${i18n._(msg`分`)}`}</span>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>

        <label className="mt-2 flex items-center gap-2 text-[11px]">
          <input
            type="checkbox"
            checked={draft.flattenEnabled}
            onChange={() => setDraft({ ...draft, flattenEnabled: !draft.flattenEnabled })}
          />
          {i18n._(msg`閉場前の手仕舞いを有効にする（設定）`)}
        </label>
        <p className="mt-1 text-[10.5px] text-fg-muted">
          {i18n._(
            msg`ここで無効にしても、確定した方針に「閉場前の手仕舞い: 有効」の行があれば手仕舞いは有効になります（方針で無効にはできません）。`,
          )}
        </p>

        <div className="mt-2">
          <Label htmlFor="pc-reason">{i18n._(msg`閉場前の統制の変更理由`)}</Label>
          <Input id="pc-reason" value={reason} onChange={(e) => setReason(e.target.value)} required />
        </div>

        {errors.length > 0 && (
          <p role="alert" className="mt-2 text-[11px] text-danger">
            {errors.join(' ')}
          </p>
        )}

        {loosenings.length > 0 && (
          <div role="alert" className="mt-2 rounded-md border border-danger p-2">
            <p className="text-[11px] text-danger">
              {`${i18n._(msg`次の変更は閉場前の統制を緩めます。内容を確認してください:`)} ${loosenings.join('、')}`}
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
            {i18n._(msg`閉場前の統制を保存`)}
          </Button>
          {save.isPending && <span role="status">{i18n._(msg`閉場前の統制を保存中…`)}</span>}
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
 * 閉場前の統制の区画（取得・縮退つき）。取得に失敗しても他の区画を巻き込まない（独立して縮退する）。
 * 🔴 取得できないときに既定値で入力欄を出さない（効いている値が分からないまま保存させない）。
 */
export function PreCloseControlsFormSection() {
  const query = usePreCloseControls();
  if (query.isPending) {
    return (
      <Panel heading={i18n._(msg`閉場前の統制（変更）`)}>
        <p role="status" className="text-[11px] text-fg-muted">
          {i18n._(msg`閉場前の統制を読み込み中…`)}
        </p>
      </Panel>
    );
  }
  if (query.isError) {
    return (
      <Panel heading={i18n._(msg`閉場前の統制（変更）`)}>
        <Note tone="err">{i18n._(msg`閉場前の統制を取得できません。効いている値が分からないため、変更できません。`)}</Note>
      </Panel>
    );
  }
  return <PreCloseControlsForm view={query.data} />;
}
