import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';

// SC-01, FR-17, UC-06, ADR-0007, IADR-0080: 設定画面（全体前提条件の閲覧/変更）。
// データ源は BFF `/bff/assumptions`（ConfigurationService・#19/IADR-0021/0063）。変更は利用者のみ（サーバ側 OwnerOnly）、
// 楽観排他（expectedVersion）＋理由必須。検証(400)・競合(409)はメッセージ表示し、破壊的な自動再試行はしない（安全既定）。
// 第1スライスは FR-17 のみ。FR-13（監視銘柄/閾値/リスク上限）は後続スライスで同画面へ節を追加する。

interface CommissionSchedule {
  rate: number;
  minimum: number;
  cap: number;
}
interface MonthlyCostLimits {
  total: number;
  llm: number;
  infrastructure: number;
  data: number;
}
interface TradingAssumptions {
  capitalGainsTaxRate: number;
  japanCommission: CommissionSchedule;
  unitedStatesCommission: CommissionSchedule;
  fxSpreadRatio: number;
  minimumExpectedProfitMultiple: number;
  costLimits: MonthlyCostLimits;
}
interface VersionedAssumptions {
  assumptions: TradingAssumptions;
  version: number;
  isResolved: boolean;
}
interface ChangeEntry {
  actor: string;
  reason: string;
  changedAt: string;
  version: number;
  before?: string | null;
  after?: string | null;
}

// フォームは文字列で保持し、送信時に数値へ変換する（type=number の制御入力の往復問題を避ける）。
interface FormModel {
  capitalGainsTaxRate: string;
  fxSpreadRatio: string;
  minimumExpectedProfitMultiple: string;
  jpRate: string;
  jpMin: string;
  jpCap: string;
  usRate: string;
  usMin: string;
  usCap: string;
  costTotal: string;
  costLlm: string;
  costInfra: string;
  costData: string;
}

type Status = 'loading' | 'ok' | 'notFound' | 'error';
type HistoryStatus = 'loading' | 'ok' | 'unavailable';
type SaveState = 'idle' | 'saving' | 'error';

function toForm(a: TradingAssumptions): FormModel {
  return {
    capitalGainsTaxRate: String(a.capitalGainsTaxRate),
    fxSpreadRatio: String(a.fxSpreadRatio),
    minimumExpectedProfitMultiple: String(a.minimumExpectedProfitMultiple),
    jpRate: String(a.japanCommission.rate),
    jpMin: String(a.japanCommission.minimum),
    jpCap: String(a.japanCommission.cap),
    usRate: String(a.unitedStatesCommission.rate),
    usMin: String(a.unitedStatesCommission.minimum),
    usCap: String(a.unitedStatesCommission.cap),
    costTotal: String(a.costLimits.total),
    costLlm: String(a.costLimits.llm),
    costInfra: String(a.costLimits.infrastructure),
    costData: String(a.costLimits.data),
  };
}

// 数値化。空文字・非数値は 0 とみなす（送信は常に有効な数値。実効検証はサーバ側 400 が担う）。
function num(s: string): number {
  const n = parseFloat(s);
  return Number.isFinite(n) ? n : 0;
}

function fromForm(f: FormModel): TradingAssumptions {
  return {
    capitalGainsTaxRate: num(f.capitalGainsTaxRate),
    fxSpreadRatio: num(f.fxSpreadRatio),
    minimumExpectedProfitMultiple: num(f.minimumExpectedProfitMultiple),
    japanCommission: { rate: num(f.jpRate), minimum: num(f.jpMin), cap: num(f.jpCap) },
    unitedStatesCommission: { rate: num(f.usRate), minimum: num(f.usMin), cap: num(f.usCap) },
    costLimits: {
      total: num(f.costTotal),
      llm: num(f.costLlm),
      infrastructure: num(f.costInfra),
      data: num(f.costData),
    },
  };
}

// ISO 8601（DateTimeOffset 由来）をロケール表記に整形する。解釈できない値はそのまま表示する。
function formatAt(value: string | null | undefined): string {
  if (!value) return '—';
  const t = Date.parse(value);
  return Number.isNaN(t) ? value : new Date(t).toLocaleString();
}

// ApiError の種別を利用者向けメッセージへ写像する。詳細（400 の details）があれば併記する。
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

export function SettingsPage() {
  const [status, setStatus] = useState<Status>('loading');
  const [current, setCurrent] = useState<VersionedAssumptions | null>(null);
  const [form, setForm] = useState<FormModel | null>(null);
  const [reason, setReason] = useState('');
  const [historyStatus, setHistoryStatus] = useState<HistoryStatus>('loading');
  const [history, setHistory] = useState<ChangeEntry[]>([]);
  const [saveState, setSaveState] = useState<SaveState>('idle');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);

  async function loadCurrent(): Promise<void> {
    try {
      const data = await apiFetch<VersionedAssumptions>('/assumptions');
      setCurrent(data);
      setForm(toForm(data.assumptions));
      setStatus('ok');
    } catch (e: unknown) {
      // 404 は不在/秘匿を区別しない（IADR-0009）。
      setStatus(e instanceof ApiError && e.kind === 'notFound' ? 'notFound' : 'error');
    }
  }

  async function loadHistory(): Promise<void> {
    try {
      const data = await apiFetch<ChangeEntry[]>('/assumptions/history');
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
    if (!current || !form || reason.trim() === '') return;
    setSaveState('saving');
    setSaveError(null);
    setSavedNotice(null);
    try {
      await apiFetch<VersionedAssumptions>('/assumptions', {
        method: 'PUT',
        json: { assumptions: fromForm(form), expectedVersion: current.version, reason: reason.trim() },
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
      <h1>設定</h1>
      <p>全体前提条件（税・手数料・為替・費用上限）の閲覧と変更を行います（FR-17）。変更は利用者のみが行えます。</p>

      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'notFound' && <p>設定情報は利用できません。</p>}
      {status === 'error' && <p role="alert">設定情報の取得に失敗しました。</p>}

      {status === 'ok' && current && form && (
        <>
          <p>{`現在のバージョン: ${current.version}`}</p>
          {!current.isResolved && (
            <p role="alert">設定サービスの値を解決できていません（既定値を表示しています）。</p>
          )}

          <form onSubmit={handleSubmit} aria-label="全体前提条件の変更">
            <Field id="capitalGainsTaxRate" label="譲渡益税率" value={form.capitalGainsTaxRate}
              onChange={(v) => setForm({ ...form, capitalGainsTaxRate: v })} />
            <Field id="fxSpreadRatio" label="為替スプレッド率" value={form.fxSpreadRatio}
              onChange={(v) => setForm({ ...form, fxSpreadRatio: v })} />
            <Field id="minimumExpectedProfitMultiple" label="最小期待利益倍率" value={form.minimumExpectedProfitMultiple}
              onChange={(v) => setForm({ ...form, minimumExpectedProfitMultiple: v })} />

            <fieldset>
              <legend>日本株 手数料体系</legend>
              <Field id="jpRate" label="日本株 手数料率" value={form.jpRate}
                onChange={(v) => setForm({ ...form, jpRate: v })} />
              <Field id="jpMin" label="日本株 最低手数料" value={form.jpMin}
                onChange={(v) => setForm({ ...form, jpMin: v })} />
              <Field id="jpCap" label="日本株 上限手数料" value={form.jpCap}
                onChange={(v) => setForm({ ...form, jpCap: v })} />
            </fieldset>

            <fieldset>
              <legend>米国株 手数料体系</legend>
              <Field id="usRate" label="米国株 手数料率" value={form.usRate}
                onChange={(v) => setForm({ ...form, usRate: v })} />
              <Field id="usMin" label="米国株 最低手数料" value={form.usMin}
                onChange={(v) => setForm({ ...form, usMin: v })} />
              <Field id="usCap" label="米国株 上限手数料" value={form.usCap}
                onChange={(v) => setForm({ ...form, usCap: v })} />
            </fieldset>

            <fieldset>
              <legend>月次費用上限</legend>
              <Field id="costTotal" label="月次費用上限 総額" value={form.costTotal}
                onChange={(v) => setForm({ ...form, costTotal: v })} />
              <Field id="costLlm" label="月次費用上限 LLM" value={form.costLlm}
                onChange={(v) => setForm({ ...form, costLlm: v })} />
              <Field id="costInfra" label="月次費用上限 インフラ" value={form.costInfra}
                onChange={(v) => setForm({ ...form, costInfra: v })} />
              <Field id="costData" label="月次費用上限 データ" value={form.costData}
                onChange={(v) => setForm({ ...form, costData: v })} />
            </fieldset>

            <div>
              <label htmlFor="reason">変更理由</label>
              <textarea id="reason" value={reason} onChange={(e) => setReason(e.target.value)} required />
            </div>

            <button type="submit" disabled={reason.trim() === '' || saveState === 'saving'}>
              保存
            </button>
            {saveState === 'saving' && <span role="status">保存中…</span>}
            {savedNotice && <p role="status">{savedNotice}</p>}
            {saveError && <p role="alert">{saveError}</p>}
          </form>

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

// FR-17, ADR-0007: 変更履歴（新しい順）。取得不能・0 件はその旨を明示する（縮退表示）。
function HistoryView({ status, history }: { status: HistoryStatus; history: ChangeEntry[] }) {
  return (
    <Section title={`変更履歴（${status === 'ok' ? history.length : '—'}）`}>
      {status === 'loading' && <p role="status">履歴を確認中…</p>}
      {status === 'unavailable' && <p>変更履歴は利用できません。</p>}
      {status === 'ok' && history.length === 0 && <p>変更履歴はありません。</p>}
      {status === 'ok' && history.length > 0 && (
        <table aria-label="変更履歴">
          <thead>
            <tr>
              <th>バージョン</th>
              <th>変更者</th>
              <th>理由</th>
              <th>日時</th>
            </tr>
          </thead>
          <tbody>
            {history.map((h, i) => (
              <tr key={`${i}-${h.version}-${h.changedAt}`}>
                <td>{h.version}</td>
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
