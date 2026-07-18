import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import type {
  RiskLimitSettings,
  RiskManagementSettings,
  SettingsChangeEntry,
} from '../risk/contracts';
import {
  changeTypeLabel,
  formatAt,
  marketLabel,
  modeLabel,
  productTypeLabel,
  stageLabel,
} from '../risk/contracts';

// SC-02, FR-13, FR-19, FR-20, UC-06, ADR-0007, IADR-0084: リスク設定画面（リスク上限の閲覧/変更）。
// データ源は BFF `/bff/risk-controls/settings`（RiskManagementService・OwnerOnly）。変更は利用者のみ・理由必須。
// 本スライスは FR-13 の中核である「リスク上限（limits 8 項目）」の変更のみを許し、ガード・段階は参照表示に留める
// （段階変更は段階ゲート承認フロー＝#165 Bot 側と重複するため直接は開かない）。検証(400)・競合(409)はメッセージ表示し、
// 破壊的な自動再試行はしない（安全既定）。

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
        リスク統制の上限（発注額・保有数・損失/DD 上限など）の閲覧と変更を行います（FR-13）。変更は利用者のみが行えます。
        ガード・段階は参照表示です（段階の変更は段階ゲートの承認で行います）。
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

          <GuardView guard={current.guard} />
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

// FR-19: ガード（参照専用）。変更 UI は後続スライス。
function GuardView({ guard }: { guard: RiskManagementSettings['guard'] }) {
  const products = guard.enabledProductTypes.map(productTypeLabel).join('、') || '—';
  const markets = guard.enabledMarkets.map(marketLabel).join('、') || '—';
  return (
    <Section title="取引ガード（参照）">
      <dl>
        <dt>有効な商品種別</dt>
        <dd>{products}</dd>
        <dt>有効な市場</dt>
        <dd>{markets}</dd>
        <dt>同日再エントリー禁止</dt>
        <dd>{guard.preventSameDayReentry ? '有効' : '無効'}</dd>
        <dt>相場操縦パターン禁止</dt>
        <dd>{guard.prohibitManipulativeOrderPatterns ? '有効' : '無効'}</dd>
      </dl>
      {guard.bannedSymbols.length === 0 ? (
        <p>禁止銘柄はありません。</p>
      ) : (
        <table aria-label="禁止銘柄">
          <thead>
            <tr>
              <th>銘柄</th>
              <th>市場</th>
              <th>理由</th>
              <th>登録日</th>
            </tr>
          </thead>
          <tbody>
            {guard.bannedSymbols.map((b, i) => (
              <tr key={`${i}-${b.symbol}-${b.market}`}>
                <td>{b.symbol}</td>
                <td>{marketLabel(b.market)}</td>
                <td>{b.reason}</td>
                <td>{formatAt(b.registeredOn)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Section>
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
