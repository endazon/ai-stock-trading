import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import type {
  BannedSymbol,
  RiskLimitSettings,
  RiskManagementSettings,
  SettingsChangeEntry,
  TradingGuardSettings,
} from '../risk/contracts';
import {
  changeTypeLabel,
  formatAt,
  marketLabel,
  modeLabel,
  MARKET_OPTIONS,
  PRODUCT_TYPE_MARGIN,
  PRODUCT_TYPE_OPTIONS,
  stageLabel,
} from '../risk/contracts';

// SC-02, FR-13, FR-19, FR-20, UC-06, ADR-0007, IADR-0084, IADR-0086: リスク設定画面（リスク上限・ガードの閲覧/変更）。
// データ源は BFF `/bff/risk-controls/settings`（RiskManagementService・OwnerOnly）。変更は利用者のみ・理由必須。
// リスク上限（limits 8 項目・#186）に加え、取引ガード（guard・#188/IADR-0086）を変更できる。段階（stage）は参照表示に留める
// （段階変更は段階ゲート承認フロー＝#20/#165 Bot 側と重複するため直接は開かない）。検証(400)・競合(409)はメッセージ表示し、
// 破壊的な自動再試行はしない（安全既定）。ガードの危険な緩和は明示確認を要求する（fail-safe）。

// フォームは文字列で保持し、送信時に数値へ変換する（type=number 制御入力の往復問題を避ける・SC-01 と同方針）。
interface FormModel {
  maxOrderAmount: string;
  maxDailyOrderAmount: string;
  maxOpenPositions: string;
  dailyLossLimitRatio: string;
  perTradeRiskRatio: string;
  maxDrawdownRatio: string;
  losingStreakThreshold: string;
  losingStreakSizeFactor: string;
}

type Status = 'loading' | 'ok' | 'notFound' | 'error';
type HistoryStatus = 'loading' | 'ok' | 'unavailable';
type SaveState = 'idle' | 'saving' | 'error';

// 各上限フィールドの表示ラベル（順序は表示順）。<label> と入力検証の警告文の対応に用いる。
const FIELD_LABELS: Record<keyof FormModel, string> = {
  maxOrderAmount: '1注文金額上限',
  maxDailyOrderAmount: '1日発注金額上限',
  maxOpenPositions: '保有銘柄数上限',
  dailyLossLimitRatio: '日次損失上限（資金比）',
  perTradeRiskRatio: '1取引リスク（資金比）',
  maxDrawdownRatio: '最大ドローダウン上限（比）',
  losingStreakThreshold: '連敗しきい値',
  losingStreakSizeFactor: '連敗時サイズ縮小係数',
};

function toForm(l: RiskLimitSettings): FormModel {
  return {
    maxOrderAmount: String(l.maxOrderAmount),
    maxDailyOrderAmount: String(l.maxDailyOrderAmount),
    maxOpenPositions: String(l.maxOpenPositions),
    dailyLossLimitRatio: String(l.dailyLossLimitRatio),
    perTradeRiskRatio: String(l.perTradeRiskRatio),
    maxDrawdownRatio: String(l.maxDrawdownRatio),
    losingStreakThreshold: String(l.losingStreakThreshold),
    losingStreakSizeFactor: String(l.losingStreakSizeFactor),
  };
}

// 空欄・非数値は「無効」とし、黙って 0 送信しない（安全既定）。実効な範囲検証はサーバ側 400 が担う。
function isValidNumber(s: string): boolean {
  if (s.trim() === '') return false;
  return Number.isFinite(Number(s));
}

function invalidFieldLabels(f: FormModel): string[] {
  return (Object.keys(FIELD_LABELS) as (keyof FormModel)[])
    .filter((k) => !isValidNumber(f[k]))
    .map((k) => FIELD_LABELS[k]);
}

function num(s: string): number {
  const n = Number(s);
  return Number.isFinite(n) ? n : 0;
}

function fromForm(f: FormModel): RiskLimitSettings {
  return {
    maxOrderAmount: num(f.maxOrderAmount),
    maxDailyOrderAmount: num(f.maxDailyOrderAmount),
    maxOpenPositions: num(f.maxOpenPositions),
    dailyLossLimitRatio: num(f.dailyLossLimitRatio),
    perTradeRiskRatio: num(f.perTradeRiskRatio),
    maxDrawdownRatio: num(f.maxDrawdownRatio),
    losingStreakThreshold: num(f.losingStreakThreshold),
    losingStreakSizeFactor: num(f.losingStreakSizeFactor),
  };
}

// ApiError の種別を利用者向けメッセージへ写像する（SC-01 と同方針）。
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

export function RiskSettingsPage() {
  const [status, setStatus] = useState<Status>('loading');
  const [current, setCurrent] = useState<RiskManagementSettings | null>(null);
  const [form, setForm] = useState<FormModel | null>(null);
  const [reason, setReason] = useState('');
  const [historyStatus, setHistoryStatus] = useState<HistoryStatus>('loading');
  const [history, setHistory] = useState<SettingsChangeEntry[]>([]);
  const [saveState, setSaveState] = useState<SaveState>('idle');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);

  async function loadCurrent(): Promise<void> {
    try {
      const data = await apiFetch<RiskManagementSettings>('/risk-controls/settings');
      setCurrent(data);
      setForm(toForm(data.limits));
      setStatus('ok');
    } catch (e: unknown) {
      // 404 は不在/秘匿を区別しない（IADR-0009）。BFF 未登録も安全側に縮退。
      setStatus(e instanceof ApiError && e.kind === 'notFound' ? 'notFound' : 'error');
    }
  }

  async function loadHistory(): Promise<void> {
    try {
      const data = await apiFetch<SettingsChangeEntry[]>('/risk-controls/settings/history');
      setHistory(data ?? []);
      setHistoryStatus('ok');
    } catch {
      // 履歴の取得不能はその領域のみ縮退（設定表示・変更と疎結合）。
      setHistoryStatus('unavailable');
    }
  }

  useEffect(() => {
    void loadCurrent();
    void loadHistory();
  }, []);

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    // 理由必須・全フィールド有効を送信の前提とする（安全既定。ボタン無効化と二重の防御）。
    if (!current || !form || reason.trim() === '' || invalidFieldLabels(form).length > 0) return;
    setSaveState('saving');
    setSaveError(null);
    setSavedNotice(null);
    try {
      // PUT /risk-controls/settings/limits（{ limits, reason }）。楽観排他は EF 側（競合は 409）。
      await apiFetch<RiskManagementSettings>('/risk-controls/settings/limits', {
        method: 'PUT',
        json: { limits: fromForm(form), reason: reason.trim() },
      });
      // 成功後は現在値・履歴を再取得して最新化する。破壊的操作はしない。
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
    <section>
      <h1>リスク設定</h1>
      <p>
        リスク統制の上限（発注額・保有数・損失/DD 上限など）と取引ガードの閲覧と変更を行います（FR-13）。変更は利用者のみが行えます。
        段階は参照表示です（段階の変更は段階ゲートの承認で行います）。ガードの緩和など危険な変更は確認を求めます。
      </p>

      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'notFound' && <p>リスク設定は利用できません。</p>}
      {status === 'error' && <p role="alert">リスク設定の取得に失敗しました。</p>}

      {status === 'ok' && current && form && (
        <>
          <form onSubmit={handleSubmit} aria-label="リスク上限の変更">
            <fieldset>
              <legend>リスク上限</legend>
              {(Object.keys(FIELD_LABELS) as (keyof FormModel)[]).map((k) => (
                <Field
                  key={k}
                  id={k}
                  label={FIELD_LABELS[k]}
                  value={form[k]}
                  onChange={(v) => setForm({ ...form, [k]: v })}
                />
              ))}
            </fieldset>

            <div>
              <label htmlFor="reason">変更理由</label>
              <textarea id="reason" value={reason} onChange={(e) => setReason(e.target.value)} required />
            </div>

            {invalidFieldLabels(form).length > 0 && (
              <p role="alert">
                未入力または数値でない項目があります: {invalidFieldLabels(form).join('、')}
              </p>
            )}

            <button
              type="submit"
              disabled={reason.trim() === '' || saveState === 'saving' || invalidFieldLabels(form).length > 0}
            >
              保存
            </button>
            {saveState === 'saving' && <span role="status">保存中…</span>}
            {savedNotice && <p role="status">{savedNotice}</p>}
            {saveError && <p role="alert">{saveError}</p>}
          </form>

          <GuardForm
            guard={current.guard}
            onSaved={async () => {
              await loadCurrent();
              await loadHistory();
            }}
          />
          <StageView stage={current.stage} />
          <HistoryView status={historyStatus} history={history} />
        </>
      )}
    </section>
  );
}

// 数値入力（文字列で保持）。ラベルと入力を id で関連づける（getByLabelText で参照可能）。
function Field({
  id,
  label,
  value,
  onChange,
}: {
  id: string;
  label: string;
  value: string;
  onChange: (v: string) => void;
}) {
  return (
    <div>
      <label htmlFor={id}>{label}</label>
      <input id={id} type="number" step="any" value={value} onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}

// FR-13, FR-19, IADR-0086: 取引ガードの変更フォーム。`PUT /risk-controls/settings/guard`（全置換）。
// 現在値を初期値に読み込み、編集後の全体を理由とともに送る。危険な緩和（トグル OFF・禁止銘柄削除・信用の新規有効化）は
// 明示確認を要求する（安全側 fail-safe・#188）。厳格化（禁止追加・トグル ON・信用無効化）は確認不要（非対称）。
type GuardSaveState = 'idle' | 'saving' | 'error';

function toggleValue(list: number[], value: number): number[] {
  return list.includes(value) ? list.filter((v) => v !== value) : [...list, value].sort((a, b) => a - b);
}

// 禁止銘柄の同一性キー（symbol と market の組）。区切りは銘柄コードに現れない縦棒を用いる。
function bannedKey(b: BannedSymbol): string {
  return `${b.symbol}|${b.market}`;
}

// 現在値（original）に対する送信予定の「危険な緩和」を列挙する。空なら危険なし。
function dangerousChanges(original: TradingGuardSettings, form: GuardFormState): string[] {
  const dangers: string[] = [];
  if (original.preventSameDayReentry && !form.preventSameDayReentry) {
    dangers.push('同日再エントリー禁止を無効化');
  }
  if (original.prohibitManipulativeOrderPatterns && !form.prohibitManipulativeOrderPatterns) {
    dangers.push('相場操縦パターン禁止を無効化');
  }
  if (!original.enabledProductTypes.includes(PRODUCT_TYPE_MARGIN) && form.enabledProductTypes.includes(PRODUCT_TYPE_MARGIN)) {
    dangers.push('信用取引を有効化');
  }
  // 禁止銘柄の削除（登録済みが送信予定に無い）を危険とみなす。同一 symbol+market の重複登録も正しく扱うため、
  // 集合ではなく多重集合（件数）で突合する（1 件消しても残り 1 件あれば「削除」とはみなさない）。
  const remaining = new Map<string, number>();
  for (const b of form.bannedSymbols) {
    const k = bannedKey(b);
    remaining.set(k, (remaining.get(k) ?? 0) + 1);
  }
  const removed: BannedSymbol[] = [];
  for (const b of original.bannedSymbols) {
    const k = bannedKey(b);
    const count = remaining.get(k) ?? 0;
    if (count > 0) remaining.set(k, count - 1);
    else removed.push(b);
  }
  if (removed.length > 0) {
    dangers.push(`禁止銘柄の削除（${removed.map((b) => b.symbol).join('、')}）`);
  }
  return dangers;
}

interface GuardFormState {
  enabledProductTypes: number[];
  enabledMarkets: number[];
  bannedSymbols: BannedSymbol[];
  preventSameDayReentry: boolean;
  prohibitManipulativeOrderPatterns: boolean;
}

function toGuardForm(g: TradingGuardSettings): GuardFormState {
  return {
    enabledProductTypes: [...g.enabledProductTypes],
    enabledMarkets: [...g.enabledMarkets],
    bannedSymbols: [...g.bannedSymbols],
    preventSameDayReentry: g.preventSameDayReentry,
    prohibitManipulativeOrderPatterns: g.prohibitManipulativeOrderPatterns,
  };
}

// 今日の日付（DateOnly＝YYYY-MM-DD）。新規禁止銘柄の登録日に用いる。
function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

function GuardForm({ guard, onSaved }: { guard: TradingGuardSettings; onSaved: () => Promise<void> | void }) {
  const [form, setForm] = useState<GuardFormState>(() => toGuardForm(guard));
  const [reason, setReason] = useState('');
  const [confirmDanger, setConfirmDanger] = useState(false);
  const [saveState, setSaveState] = useState<GuardSaveState>('idle');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);
  // 新規禁止銘柄の下書き。
  const [newSymbol, setNewSymbol] = useState('');
  const [newMarket, setNewMarket] = useState<number>(MARKET_OPTIONS[0]?.value ?? 0);
  const [newReason, setNewReason] = useState('');

  // 現在値に追随してフォームを初期化する。ただし依存を「値のシグネチャ」にして、ガードの内容が実際に変わったとき
  // （＝自分の保存成功後の再取得や外部変更）だけ初期化する。隣接するリスク上限フォームの保存でも親の current は再生成
  // され guard の参照は変わるが、ガードの内容が同一なら初期化しない（編集中のガード内容・理由・危険確認・下書きを
  // 黙って破棄しない・fail-safe / #188 AI レビュー指摘）。guard は guardSignature と同一値のため依存に含めない。
  const guardSignature = JSON.stringify(guard);
  useEffect(() => {
    setForm(toGuardForm(guard));
    setReason('');
    setConfirmDanger(false);
    setNewSymbol('');
    setNewReason('');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [guardSignature]);

  const dangers = dangerousChanges(guard, form);
  // 危険確認は「その時点の危険リスト」に紐付ける。確認後にさらに危険な変更が増えたら確認を外し、再確認を要求する
  // （fail-safe。IADR-0086 決定3 の趣旨＝差分を都度確認させる / #188 AI 再レビュー指摘）。
  const dangersSignature = dangers.join('｜');
  useEffect(() => {
    setConfirmDanger(false);
  }, [dangersSignature]);

  const blocked = reason.trim() === '' || saveState === 'saving' || (dangers.length > 0 && !confirmDanger);

  function addBannedSymbol(): void {
    const symbol = newSymbol.trim();
    const banReason = newReason.trim();
    // FR-19: 禁止根拠を記録する趣旨に沿い、銘柄コードと理由の双方が入るまで追加しない。
    if (symbol === '' || banReason === '') return;
    setForm({
      ...form,
      bannedSymbols: [
        ...form.bannedSymbols,
        { symbol, market: newMarket, reason: banReason, registeredOn: todayIso() },
      ],
    });
    setNewSymbol('');
    setNewReason('');
  }

  function removeBannedSymbol(index: number): void {
    setForm({ ...form, bannedSymbols: form.bannedSymbols.filter((_, i) => i !== index) });
  }

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    // 理由必須・危険確認を送信の前提にする（ボタン無効化と二重の防御・安全既定）。
    if (blocked) return;
    setSaveState('saving');
    setSaveError(null);
    setSavedNotice(null);
    try {
      // PUT /risk-controls/settings/guard（全置換）。楽観排他は EF 側（競合は 409）。
      await apiFetch<RiskManagementSettings>('/risk-controls/settings/guard', {
        method: 'PUT',
        json: {
          enabledProductTypes: form.enabledProductTypes,
          enabledMarkets: form.enabledMarkets,
          bannedSymbols: form.bannedSymbols,
          preventSameDayReentry: form.preventSameDayReentry,
          prohibitManipulativeOrderPatterns: form.prohibitManipulativeOrderPatterns,
          reason: reason.trim(),
        },
      });
      // 成功後は現在値・履歴を再取得（useEffect が form/理由/確認を初期化する）。破壊的操作はしない。
      setSaveState('idle');
      setSavedNotice('保存しました。');
      await onSaved();
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveState('error');
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <Section title="取引ガード（変更）">
      <form onSubmit={handleSubmit} aria-label="取引ガードの変更">
        <fieldset>
          <legend>有効な商品種別</legend>
          {PRODUCT_TYPE_OPTIONS.map((o) => (
            <Check
              key={`pt-${o.value}`}
              label={o.label}
              checked={form.enabledProductTypes.includes(o.value)}
              onChange={() => setForm({ ...form, enabledProductTypes: toggleValue(form.enabledProductTypes, o.value) })}
            />
          ))}
        </fieldset>

        <fieldset>
          <legend>有効な市場</legend>
          {MARKET_OPTIONS.map((o) => (
            <Check
              key={`mk-${o.value}`}
              label={o.label}
              checked={form.enabledMarkets.includes(o.value)}
              onChange={() => setForm({ ...form, enabledMarkets: toggleValue(form.enabledMarkets, o.value) })}
            />
          ))}
        </fieldset>

        <fieldset>
          <legend>取引ガード</legend>
          <Check
            label="同日再エントリー禁止"
            checked={form.preventSameDayReentry}
            onChange={() => setForm({ ...form, preventSameDayReentry: !form.preventSameDayReentry })}
          />
          <Check
            label="相場操縦パターン禁止"
            checked={form.prohibitManipulativeOrderPatterns}
            onChange={() =>
              setForm({ ...form, prohibitManipulativeOrderPatterns: !form.prohibitManipulativeOrderPatterns })
            }
          />
        </fieldset>

        <fieldset>
          <legend>禁止銘柄</legend>
          {form.bannedSymbols.length === 0 ? (
            <p>禁止銘柄はありません。</p>
          ) : (
            <table aria-label="禁止銘柄（編集）">
              <thead>
                <tr>
                  <th>銘柄</th>
                  <th>市場</th>
                  <th>理由</th>
                  <th>登録日</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {form.bannedSymbols.map((b, i) => (
                  <tr key={`${i}-${b.symbol}-${b.market}`}>
                    <td>{b.symbol}</td>
                    <td>{marketLabel(b.market)}</td>
                    <td>{b.reason}</td>
                    <td>{formatAt(b.registeredOn)}</td>
                    <td>
                      <button type="button" onClick={() => removeBannedSymbol(i)}>
                        削除
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <div>
            <label htmlFor="guard-new-symbol">禁止銘柄コード</label>
            <input id="guard-new-symbol" value={newSymbol} onChange={(e) => setNewSymbol(e.target.value)} />
            <label htmlFor="guard-new-market">禁止銘柄の市場</label>
            <select id="guard-new-market" value={newMarket} onChange={(e) => setNewMarket(Number(e.target.value))}>
              {MARKET_OPTIONS.map((o) => (
                <option key={`nm-${o.value}`} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
            <label htmlFor="guard-new-reason">禁止理由</label>
            {/* 追加サブフォームの下書き入力には HTML5 required を付けない（付けると本体フォームの送信が空欄で妨げられる）。
                理由の必須化は「禁止銘柄を追加」ボタンの無効化で担保する（FR-19）。 */}
            <input id="guard-new-reason" value={newReason} onChange={(e) => setNewReason(e.target.value)} />
            <button
              type="button"
              onClick={addBannedSymbol}
              disabled={newSymbol.trim() === '' || newReason.trim() === ''}
            >
              禁止銘柄を追加
            </button>
          </div>
        </fieldset>

        <div>
          <label htmlFor="guard-reason">変更理由</label>
          <textarea id="guard-reason" value={reason} onChange={(e) => setReason(e.target.value)} required />
        </div>

        {dangers.length > 0 && (
          <div role="alert">
            <p>次の変更は取引ガードを緩めます。内容を確認してください: {dangers.join('、')}</p>
            <Check
              label="上記の危険な変更を確認しました"
              checked={confirmDanger}
              onChange={() => setConfirmDanger(!confirmDanger)}
            />
          </div>
        )}

        <button type="submit" disabled={blocked}>
          保存
        </button>
        {saveState === 'saving' && <span role="status">保存中…</span>}
        {savedNotice && <p role="status">{savedNotice}</p>}
        {saveError && <p role="alert">{saveError}</p>}
      </form>
    </Section>
  );
}

// チェックボックス（ラベルと入力を関連づけ、getByRole('checkbox', { name }) で参照可能にする）。
function Check({ label, checked, onChange }: { label: string; checked: boolean; onChange: () => void }) {
  return (
    <div>
      <label>
        <input type="checkbox" checked={checked} onChange={onChange} />
        {label}
      </label>
    </div>
  );
}

// FR-20: 段階（参照専用）。段階変更は段階ゲート承認フロー（#165 Bot 側）。
function StageView({ stage }: { stage: RiskManagementSettings['stage'] }) {
  return (
    <Section title="運用段階（参照）">
      <dl>
        <dt>現段階</dt>
        <dd>{stageLabel(stage.stage)}</dd>
        <dt>モード</dt>
        <dd>{modeLabel(stage.mode)}</dd>
        <dt>資金上限</dt>
        <dd>{stage.capitalCap}</dd>
      </dl>
    </Section>
  );
}

// FR-11, ADR-0007: 変更履歴（新しい順）。取得不能・0 件はその旨を明示する（縮退表示）。
function HistoryView({ status, history }: { status: HistoryStatus; history: SettingsChangeEntry[] }) {
  return (
    <Section title={`変更履歴（${status === 'ok' ? history.length : '—'}）`}>
      {status === 'loading' && <p role="status">履歴を確認中…</p>}
      {status === 'unavailable' && <p>変更履歴は利用できません。</p>}
      {status === 'ok' && history.length === 0 && <p>変更履歴はありません。</p>}
      {status === 'ok' && history.length > 0 && (
        <table aria-label="変更履歴">
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
                <td>{changeTypeLabel(h.changeType)}</td>
                <td>{h.actor}</td>
                <td>{h.reason}</td>
                <td>{formatAt(h.changedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Section>
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
