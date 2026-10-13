import { useState } from 'react';
import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import {
  Button,
  Input,
  Kv,
  KvItem,
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
import type { HighVolatilitySettings, HighVolatilitySymbol } from '@ai-stock-trading/lib/risk/contracts';
import {
  equityAmountText,
  HIGH_VOLATILITY_RATIO_RANGE_TEXT,
  highVolatilityLoosenings,
  highVolatilityRatioInputToWire,
  marketLabel,
  MARKET_OPTIONS,
  normalizeDesignatedSymbol,
  validateDesignatedSymbolInput,
  validateHighVolatilityRatioInput,
  watchlistMatchOf,
  wireToHighVolatilityRatioInput,
} from '@ai-stock-trading/lib/risk/contracts';
import type { WatchlistMatch } from '@ai-stock-trading/lib/risk/contracts';
import { useSaveHighVolatility } from '@ai-stock-trading/lib/risk/queries';
import { useWatchlist } from '@ai-stock-trading/lib/monitor/queries';

// 🔴 SC-02, FR-10, UC-06, ADR-0066 決定1・決定2, ADR-0063 決定1・決定2, #1291, IADR-0530:
// **高ボラティリティ銘柄の明示指定（銘柄と市場の一覧）と区分の上限（equity 比）の表示・設定。**
//
// 手続きは §5 の他の統制値と同じ（05_screens「設定変更の一般則」）:
//   - 変更できるのは所有者だけ（サーバの OwnerOnly）。変更理由は必須。値域（1% 以上 25% 以下）の外は拒否。履歴を残す。
//   - **拒否の実効はサーバが持つ。** 画面の検証は即時提示のためであり、その代わりにならない。
//   - 経路は既存の `PUT /risk-controls/settings/high-volatility` を BFF 経由で使う（全置換。比率と一覧を一緒に送る）。
//
// **銘柄コードの誤記を防ぐ手段（ADR-0066 決定2。方式は IADR-0530 で選んだ）:**
//   1. **主の経路は監視銘柄からの選択**（銘柄と市場を一緒に選ぶので、どちらの誤記も起きない）。
//   2. **監視銘柄に無い銘柄も指定できる**（先回りの指定を残す。ADR-0066 §検討した選択肢 2）。ただし
//      入力は大文字へ正規化し、使えない文字を拒み、**監視銘柄に無いことを警告し、確認の印が無ければ追加させない**。
//      同じ銘柄コードが別の市場で監視銘柄にあるときは「市場の取り違え」として個別に警告する。
//   3. **保存済みの指定も監視銘柄と突き合わせて印を出す**（API で直接入れた誤記を画面で見つけられる）。
//   監視銘柄の取得に失敗したときは突き合わせられないことを明示し、手入力の追加には同じ確認を求める（黙って通さない）。
//
// **緩める変更（区分の上限を上げる・明示指定を外す）は確認を求める**（取引ガードの危険な緩和と同じ規律・IADR-0086 決定3）。
// 明示指定は厳しい側へだけ効くので、外すと区分から出る（自動判定〔ATR 比 ≥ 4%〕で入る銘柄は外せない。ADR-0063 決定1）。

function saveMessageOf(e: unknown): string {
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
  return i18n._(msg`保存に失敗しました。`);
}

function keyOf(s: HighVolatilitySymbol): string {
  return `${normalizeDesignatedSymbol(s.symbol)}|${s.market}`;
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

export function HighVolatilityForm({
  current,
  equity,
}: {
  /** 現在保存されている統制値（`GET /risk-controls/settings` の `highVolatility`）。 */
  current: HighVolatilitySettings;
  /** 現在の equity（実額の併記に使う。未供給は null）。 */
  equity: number | null;
}) {
  const save = useSaveHighVolatility();
  const watchlistQuery = useWatchlist();
  // 監視銘柄が取れていなければ null（照合できない）。0 件は「取れて空」であり null と区別する。
  const watchlist = watchlistQuery.isSuccess ? watchlistQuery.data : null;
  // 取得中は照合の結果を出さない（「照合中」と出す）。取得前に「照合できません」の警告を点滅させない（AI レビュー指摘）。
  const watchlistPending = watchlistQuery.isPending;

  const [ratio, setRatio] = useState(() => wireToHighVolatilityRatioInput(current.maxOrderAmountRatio));
  const [designated, setDesignated] = useState<HighVolatilitySymbol[]>(() => [...current.designatedSymbols]);
  const [reason, setReason] = useState('');
  const [confirmLoosening, setConfirmLoosening] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);
  // 監視銘柄からの選択（値は keyOf）。
  const [pick, setPick] = useState('');
  // 手入力の下書き。
  const [manualSymbol, setManualSymbol] = useState('');
  const [manualMarket, setManualMarket] = useState<number>(MARKET_OPTIONS[0]?.value ?? 0);
  // 手入力の確認の印は「確かめた銘柄・市場の組」に紐づける（印を付けた後に別の銘柄・市場へ書き換えたら印は外れる。AI レビュー指摘）。
  const [confirmedUnlistedKey, setConfirmedUnlistedKey] = useState<string | null>(null);

  // 現在値が実際に変わったときだけ初期化する（#498 / #539: useEffect で行わない。描画中に同期的に調整する）。
  const currentSignature = JSON.stringify(current);
  const [syncedSignature, setSyncedSignature] = useState(currentSignature);
  if (syncedSignature !== currentSignature) {
    setSyncedSignature(currentSignature);
    setRatio(wireToHighVolatilityRatioInput(current.maxOrderAmountRatio));
    setDesignated([...current.designatedSymbols]);
    setReason('');
    setConfirmLoosening(false);
  }

  const designatedKeys = new Set(designated.map(keyOf));
  const candidates = (watchlist ?? []).filter((w) => !designatedKeys.has(keyOf(w)));

  const ratioError = validateHighVolatilityRatioInput(ratio);
  const wireRatio = ratioError === null ? highVolatilityRatioInputToWire(ratio) : null;
  const loosenings = highVolatilityLoosenings(current, wireRatio, designated);
  // 確認は「その時点の緩める変更の一覧」に紐づける（増えたら確認を外す。GuardForm と同じ規律）。
  const looseningSignature = loosenings.join('｜');
  const [confirmedSignature, setConfirmedSignature] = useState('');
  const looseningConfirmed = confirmLoosening && confirmedSignature === looseningSignature;

  const manualNormalized = normalizeDesignatedSymbol(manualSymbol);
  const manualError = manualSymbol.trim() === '' ? null : validateDesignatedSymbolInput(manualSymbol);
  const manualMatch = manualNormalized === '' ? null : watchlistMatchOf(manualNormalized, manualMarket, watchlist);
  const manualDuplicate = manualNormalized !== '' && designatedKeys.has(`${manualNormalized}|${manualMarket}`);
  const manualKey = `${manualNormalized}|${manualMarket}`;
  const confirmUnlisted = confirmedUnlistedKey !== null && confirmedUnlistedKey === manualKey;
  const manualNeedsConfirm = !watchlistPending && manualMatch !== null && manualMatch !== 'listed';
  // 取得中は照合の結果が出るまで追加させない（確認なしで通る窓を作らない）。
  const canAddManual =
    !watchlistPending &&
    manualNormalized !== '' &&
    manualError === null &&
    !manualDuplicate &&
    (!manualNeedsConfirm || confirmUnlisted);

  const changed =
    wireRatio !== current.maxOrderAmountRatio ||
    JSON.stringify(designated.map(keyOf)) !== JSON.stringify(current.designatedSymbols.map(keyOf));
  const blocked =
    ratioError !== null ||
    !changed ||
    reason.trim() === '' ||
    save.isPending ||
    (loosenings.length > 0 && !looseningConfirmed);

  function addPicked(): void {
    const target = candidates.find((w) => keyOf(w) === pick);
    if (target === undefined) return;
    setDesignated([...designated, { symbol: normalizeDesignatedSymbol(target.symbol), market: target.market }]);
    setPick('');
  }

  function addManual(): void {
    if (!canAddManual) return;
    setDesignated([...designated, { symbol: manualNormalized, market: manualMarket }]);
    setManualSymbol('');
    setConfirmedUnlistedKey(null);
  }

  function remove(index: number): void {
    setDesignated(designated.filter((_, i) => i !== index));
  }

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    if (blocked || wireRatio === null) return;
    setSaveError(null);
    setSavedNotice(null);
    try {
      await save.mutateAsync({
        maxOrderAmountRatio: wireRatio,
        designatedSymbols: designated.map((s) => ({ symbol: normalizeDesignatedSymbol(s.symbol), market: s.market })),
        reason: reason.trim(),
      });
      setSavedNotice(i18n._(msg`高ボラティリティ銘柄の設定を保存しました。`));
    } catch (err: unknown) {
      // 400/409 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <Panel heading={i18n._(msg`高ボラティリティ銘柄（変更）`)}>
      <Note>
        {i18n._(
          msg`区分の銘柄（ATR(14) ÷ 参照価格が 4% 以上、または下の一覧で明示指定した銘柄）の新規建ては、1 注文あたり equity の次の比率までに抑えます（区分外の 1 注文上限との小さい方）。`,
        )}
        <strong>{i18n._(msg`明示指定は厳しい側へだけ効きます`)}</strong>
        {i18n._(msg`（ATR で区分に入る銘柄は、ここで外しても区分から出ません）。`)}
      </Note>

      <form onSubmit={handleSubmit} aria-label={i18n._(msg`高ボラティリティ銘柄の設定の変更`)}>
        <Kv columns={2}>
          <KvItem
            label={<Label htmlFor="hv-ratio">{i18n._(msg`区分の 1 注文あたりの発注金額上限 %`)}</Label>}
          >
            <Input
              id="hv-ratio"
              type="number"
              step="0.1"
              value={ratio}
              invalid={ratioError !== null}
              aria-describedby="hv-ratio-help"
              onChange={(e) => setRatio(e.target.value)}
              className="w-full border-0 bg-transparent p-0"
            />
            <span id="hv-ratio-help" className="mt-1 block text-[10.5px] text-fg-muted">
              {`${i18n._(msg`許容範囲:`)} ${HIGH_VOLATILITY_RATIO_RANGE_TEXT} / ${i18n._(msg`現在の equity での実額:`)} ${equityAmountText(equity, wireRatio)}`}
            </span>
          </KvItem>
          <KvItem label={<Label htmlFor="hv-reason">{i18n._(msg`高ボラティリティ銘柄の変更理由`)}</Label>}>
            <Input
              id="hv-reason"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              required
              className="w-full border-0 bg-transparent p-0"
            />
          </KvItem>
        </Kv>
        <p className="mt-1 text-[10.5px] text-fg-muted">
          {i18n._(msg`下端（1%）ちょうどでも、数量の端数切り捨てでほぼ常に見送りになります。`)}
        </p>

        {ratioError !== null && (
          <p role="alert" className="mt-2 text-[11px] text-danger">
            {ratioError}
          </p>
        )}

        <h4 className="mt-3 text-xs font-semibold">{i18n._(msg`明示指定の銘柄`)}</h4>
        {watchlist === null && !watchlistQuery.isPending && (
          <Note tone="err">
            {i18n._(
              msg`監視銘柄を取得できないため、銘柄コードを監視銘柄と照合できません。手入力の追加には確認が要ります。`,
            )}
          </Note>
        )}
        {designated.length === 0 ? (
          <p className="text-[11px] text-fg-muted">{i18n._(msg`明示指定はありません。`)}</p>
        ) : (
          <Table aria-label={i18n._(msg`明示指定の銘柄の一覧`)}>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{i18n._(msg`銘柄コード`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`市場`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`監視銘柄との照合`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`操作`)}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {designated.map((s, i) => {
                const note = matchText(watchlistMatchOf(normalizeDesignatedSymbol(s.symbol), s.market, watchlist));
                return (
                  <TableRow key={`${keyOf(s)}#${i}`}>
                    <TableCell>{s.symbol}</TableCell>
                    <TableCell>{marketLabel(s.market)}</TableCell>
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
                        onClick={() => remove(i)}
                        aria-label={`${s.symbol}（${marketLabel(s.market)}）${i18n._(msg`の明示指定を外す`)}`}
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

        <fieldset className="mt-2 rounded-md border border-border p-2">
          <legend className="text-[11px]">{i18n._(msg`監視銘柄から追加`)}</legend>
          <div className="flex flex-wrap items-end gap-2">
            <div>
              <Label htmlFor="hv-pick">{i18n._(msg`監視銘柄`)}</Label>
              <Select id="hv-pick" value={pick} onChange={(e) => setPick(e.target.value)}>
                <option value="">{i18n._(msg`（選択してください）`)}</option>
                {candidates.map((w) => (
                  <option key={keyOf(w)} value={keyOf(w)}>
                    {`${w.symbol}（${marketLabel(w.market)}）`}
                  </option>
                ))}
              </Select>
            </div>
            <Button type="button" onClick={addPicked} disabled={pick === ''}>
              {i18n._(msg`選んだ監視銘柄を追加`)}
            </Button>
          </div>
        </fieldset>

        <fieldset className="mt-2 rounded-md border border-border p-2">
          <legend className="text-[11px]">{i18n._(msg`監視銘柄に無い銘柄を追加（先回りの指定）`)}</legend>
          <div className="flex flex-wrap items-end gap-2">
            <div>
              <Label htmlFor="hv-manual-symbol">{i18n._(msg`銘柄コード（手入力）`)}</Label>
              <Input id="hv-manual-symbol" value={manualSymbol} onChange={(e) => setManualSymbol(e.target.value)} />
            </div>
            <div>
              <Label htmlFor="hv-manual-market">{i18n._(msg`市場（手入力）`)}</Label>
              <Select
                id="hv-manual-market"
                value={manualMarket}
                onChange={(e) => setManualMarket(Number(e.target.value))}
              >
                {MARKET_OPTIONS.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </div>
            <Button type="button" onClick={addManual} disabled={!canAddManual}>
              {i18n._(msg`手入力の銘柄を追加`)}
            </Button>
          </div>
          {manualError !== null && (
            <p role="alert" className="mt-2 text-[11px] text-danger">
              {manualError}
            </p>
          )}
          {manualDuplicate && (
            <p role="alert" className="mt-2 text-[11px] text-danger">
              {i18n._(msg`この銘柄と市場は既に明示指定されています。`)}
            </p>
          )}
          {manualError === null && !manualDuplicate && manualNeedsConfirm && manualMatch !== null && (
            <div role="alert" className="mt-2 rounded-md border border-danger p-2">
              <p className="text-[11px] text-danger">
                {`⚠ ${manualNormalized}（${marketLabel(manualMarket)}）: ${matchText(manualMatch) ?? ''}。${i18n._(
                  msg`銘柄コードを誤記すると、指定はどの注文にも一致せず黙って効きません。綴りと市場を確かめてください。`,
                )}`}
              </p>
              <label className="mt-1 flex items-center gap-2 text-[11px]">
                <input
                  type="checkbox"
                  checked={confirmUnlisted}
                  onChange={() => setConfirmedUnlistedKey(confirmUnlisted ? null : manualKey)}
                />
                {i18n._(msg`綴りと市場を確かめた（監視銘柄に無い銘柄として指定する）`)}
              </label>
            </div>
          )}
        </fieldset>

        {loosenings.length > 0 && (
          <div role="alert" className="mt-2 rounded-md border border-danger p-2">
            <p className="text-[11px] text-danger">
              {`${i18n._(msg`次の変更は高ボラティリティ銘柄の統制を緩めます。内容を確認してください:`)} ${loosenings.join('、')}`}
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
            {i18n._(msg`高ボラティリティ銘柄の設定を保存`)}
          </Button>
          {save.isPending && <span role="status">{i18n._(msg`高ボラティリティ銘柄の設定を保存中…`)}</span>}
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
