import type { ReactNode } from 'react';
import { useState } from 'react';
import { ApiError } from '@foundation/api/ApiError';
import { METRIC_NOT_SUPPLIED_TEXT } from '../risk/contracts';
import { PaperModeBanner } from '../shared/PaperModeBanner';
import { useBrokerProvider } from '../shared/paperMode';
import type { ChangeEntry, TradingAssumptions } from './assumptionsQueries';
import { useAssumptions, useAssumptionsHistory, useSaveAssumptions } from './assumptionsQueries';

// SC-01, FR-17, UC-06, IADR-0080, IADR-0164: 設定画面。
//
// **本画面は §1 全体前提条件（FR-17）のみの画面である。**
// データ源は BFF `/bff/assumptions`（ConfigurationService・#19/IADR-0021/0063）。
//
// **§2「収集パラメータ」は 2026-08-07 の利用者裁定（質問票 第 13 回 Q11・Q12）で廃止された**（#423）。
//   収集間隔 … **画面から変更しない。起動時構成とする**（Q11・案 A）。
//               費用・負荷のパラメータであり、月報レビュー時に構成で変える頻度で足りる。
//               画面から変えるには稼働中の `BackgroundService` が値を読み直す機構が要り、
//               その重さに見合う運用上の必要が示されていない。
//   変動閾値 … **SC-02 へ移した**（Q12・案 B）。権威は MarketMonitorService であり、
//               監視銘柄と同じ由来サービスである（旧記述の「ConfigurationService 由来」は誤りであった）。
//
// **本画面に収集パラメータの入力欄を戻してはならない。** 戻すこと自体が裁定に反する。
//
// 変更は利用者のみ（サーバ側 OwnerOnly）、楽観排他（expectedVersion）＋理由必須。
// 検証(400)・競合(409)はメッセージ表示し、破壊的な自動再試行はしない（安全既定）。
// リスク上限・監視銘柄・市場監視パラメータは本画面の範囲外である
// （RiskManagementService / MarketMonitorService 由来のため SC-02。planning#33 の責務分界）。

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

// 各フィールドの表示ラベル（入力検証の警告文と <label> の対応に用いる。順序は表示順）。
const FIELD_LABELS: Record<keyof FormModel, string> = {
  capitalGainsTaxRate: '譲渡益税率',
  fxSpreadRatio: '為替スプレッド率',
  minimumExpectedProfitMultiple: '最小期待利益倍率',
  jpRate: '日本株 手数料率',
  jpMin: '日本株 最低手数料',
  jpCap: '日本株 上限手数料',
  usRate: '米国株 手数料率',
  usMin: '米国株 最低手数料',
  usCap: '米国株 上限手数料',
  costTotal: '月次費用上限 総額',
  costLlm: '月次費用上限 LLM',
  costInfra: '月次費用上限 インフラ',
  costData: '月次費用上限 データ',
};

// 財務パラメータの入力検証。空欄・非数値は「無効」とし、黙って 0 送信しない（安全既定）。実効な範囲検証はサーバ側 400 が担う。
function isValidNumber(s: string): boolean {
  if (s.trim() === '') return false;
  return Number.isFinite(Number(s));
}

// 無効な（空欄/非数値の）フィールドのラベル一覧を返す。
function invalidFieldLabels(f: FormModel): string[] {
  return (Object.keys(FIELD_LABELS) as (keyof FormModel)[])
    .filter((k) => !isValidNumber(f[k]))
    .map((k) => FIELD_LABELS[k]);
}

// 数値化。呼び出し前に isValidNumber で有効性を担保する（無効時は保存ボタンが無効なため到達しない）。
function num(s: string): number {
  const n = Number(s);
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
  // IADR-0286: 取得・更新は TanStack Query（`assumptionsQueries`）が持つ。画面は
  // 「取得済みの値をどう見せるか」と「入力の検証」だけを持つ（MSP/ADR-0031）。
  const assumptionsQuery = useAssumptions();
  const historyQuery = useAssumptionsHistory();
  const saveAssumptions = useSaveAssumptions();

  const [form, setForm] = useState<FormModel | null>(null);
  const [reason, setReason] = useState('');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);
  // FR-12, #334: 内蔵 paper 警告バナーの判定に用いる現在の発注先（取得不能は null＝バナーを出さない）。
  const brokerProvider = useBrokerProvider();

  const current = assumptionsQuery.data ?? null;
  // 404 は不在/秘匿を区別しない（IADR-0009）。
  const status: Status = assumptionsQuery.isPending
    ? 'loading'
    : assumptionsQuery.isError
      ? assumptionsQuery.error instanceof ApiError && assumptionsQuery.error.kind === 'notFound'
        ? 'notFound'
        : 'error'
      : 'ok';
  // 履歴の取得不能はその領域のみ縮退（設定表示・変更と疎結合）。
  const historyStatus: HistoryStatus = historyQuery.isPending
    ? 'loading'
    : historyQuery.isError
      ? 'unavailable'
      : 'ok';
  const history: ChangeEntry[] = historyQuery.data ?? [];

  // 取得した現在値に追随してフォームを初期化する。
  //
  // 🔴 #498, #539, NFR: **これを `useEffect` で行わない。** commit（DOM が見える）と passive effect の
  // 実行の間には窓があり、その窓で利用者の入力が入ると、遅れて流れてきた初期化が入力を黙って巻き戻す。
  // 比較対象を「値のシグネチャ」にしているのは、**再取得のたびに編集を捨てない**ためである
  // （TanStack Query は無効化のたびに新しいオブジェクトを配るので、参照で比べると毎回初期化になる）。
  const assumptionsSignature = current === null ? null : JSON.stringify(current.assumptions);
  const [syncedSignature, setSyncedSignature] = useState<string | null>(null);
  if (current !== null && assumptionsSignature !== syncedSignature) {
    setSyncedSignature(assumptionsSignature);
    setForm(toForm(current.assumptions));
  }

  const saving = saveAssumptions.isPending;

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    // 理由必須・全フィールド有効を送信の前提とする（安全既定。ボタン無効化と二重の防御）。
    if (!current || !form || reason.trim() === '' || invalidFieldLabels(form).length > 0) return;
    setSaveError(null);
    setSavedNotice(null);
    try {
      // 成功時は mutation が現在値・履歴を無効化して最新化する。破壊的操作はしない。
      await saveAssumptions.mutateAsync({
        assumptions: fromForm(form),
        expectedVersion: current.version,
        reason: reason.trim(),
      });
      setReason('');
      setSavedNotice('保存しました。');
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <section>
      {/* FR-12, #334: 内蔵 paper 稼働中の警告バナー（画面上部に常時表示）。本画面は発注先の表示・変更を
          持たないが、paper 稼働中であることをどの画面からでも把握できるようにする（05_screens SC-01）。 */}
      <PaperModeBanner provider={brokerProvider} />
      <h1>設定</h1>
      <p>
        全体前提条件（税・手数料・為替・費用上限。FR-17）の閲覧と変更を行います。変更は利用者のみが行えます。
        リスク上限・監視銘柄・<strong>市場監視パラメータ（変動閾値・クールダウン）</strong>は
        「リスク設定」画面（SC-02）で扱います。
      </p>
      {/* SC-01, FR-13, #423, IADR-0164 決定1: 収集間隔は**起動時構成**である。
          入力欄を作らないだけでは「未実装の項目」に見え、次に画面を触る者が善意で実装してしまう。
          **変更しないことが裁定である**ことを画面に明記する（`role="note"` とし `alert` にはしない。
          常時表示される静的な注記であり、他の警告と同じ緊急度で読ませると警告全体が軽くなる）。 */}
      <p role="note">
        収集間隔（情報収集・市場監視のポーリング間隔）は<strong>本画面からも API からも変更しません</strong>。
        起動時の構成値として運用します（費用・負荷のパラメータであり、月報レビュー時に構成で見直します）。
      </p>

      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'notFound' && <p>設定情報は利用できません。</p>}
      {status === 'error' && <p role="alert">設定情報の取得に失敗しました。</p>}

      {status === 'ok' && current && form && (
        <>
          <p>{`現在のバージョン: ${current.version}`}</p>
          {/* SC-01 §1, #424, IADR-0162 決定4: **供給可否はサーバが宣言する。**
              `isResolved`（＝`Version > 0`）は ConfigurationService 由来の値を一度でも解決できたかを
              サーバが宣言したものであり、画面はそれに従う（値の中身から推測しない）。
              未解決のとき表示しているのは**組み込みの既定値であって権威値ではない**——「取得できている値」と
              同じ見た目で出すと、利用者は画面の数字が実際の運用値だと信じてしまう（05_screens 共通規約）。 */}
          {!current.isResolved && (
            <p role="alert">
              全体前提条件を<strong>{METRIC_NOT_SUPPLIED_TEXT}</strong>。
              以下に表示しているのは<strong>組み込みの既定値であり、実際に適用されている値ではありません</strong>
              （設定サービスの値を一度も解決できていません）。
            </p>
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

            {invalidFieldLabels(form).length > 0 && (
              <p role="alert">
                未入力または数値でない項目があります: {invalidFieldLabels(form).join('、')}
              </p>
            )}

            <button
              type="submit"
              disabled={reason.trim() === '' || saving || invalidFieldLabels(form).length > 0}
            >
              保存
            </button>
            {saving && <span role="status">保存中…</span>}
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

// FR-17: 変更履歴（新しい順）。取得不能・0 件はその旨を明示する（縮退表示）。
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
