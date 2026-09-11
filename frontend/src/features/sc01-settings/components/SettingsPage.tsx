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
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tag,
} from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import { METRIC_NOT_SUPPLIED_TEXT } from '@ai-stock-trading/lib/risk/contracts';
import { PaperModeBanner } from '@ai-stock-trading/components/PaperModeBanner';
import { QueryPhase } from '@ai-stock-trading/components/QueryPhase';
import { ScreenHeader, ScreenLink } from '@ai-stock-trading/components/ScreenHeader';
import { useBrokerProvider } from '@ai-stock-trading/hooks/useBrokerProvider';
import type { ChangeEntry, TradingAssumptions } from '../types';
import { useAssumptions, useAssumptionsHistory, useSaveAssumptions } from '../api/assumptionsQueries';

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
//
// UI/UX 改善 2026-09-12（hi-fi モック `sc-01.html` 400 行目以降）: 区画を `Panel`、項目を `Kv`、
// 注記を `Note`、履歴を `Table` へ載せ替え、**待ち・失敗・空は `QueryPhase` に一本化**した
// （手書きの `role="status"` / `role="alert"` の三項連鎖を撤去。失敗には再試行が付く）。

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

/** 404（BFF 未登録・不在の秘匿）かどうか。**再試行しても直らない**ため導線を出さない（IADR-0009）。 */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'notFound';
}

export function SettingsPage() {
  // IADR-0288: 取得・更新は TanStack Query（`assumptionsQueries`）が持つ。画面は
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
      setSavedNotice(i18n._(msg`保存しました。`));
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveError(saveMessageOf(err));
    }
  }

  // 404 は不在/秘匿を区別しない（IADR-0009）。BFF 未登録も安全側へ縮退させ、再試行は出さない。
  const notFound = assumptionsQuery.isError && isNotFound(assumptionsQuery.error);
  const invalid = form === null ? [] : invalidFieldLabels(form);

  return (
    <section>
      {/* FR-12, #334: 内蔵 paper 稼働中の警告バナー（画面上部に常時表示）。本画面は発注先の表示・変更を
          持たないが、paper 稼働中であることをどの画面からでも把握できるようにする（05_screens SC-01）。 */}
      <PaperModeBanner provider={brokerProvider} />

      <ScreenHeader title={i18n._(msg`設定`)}>
        {current !== null && (
          <Tag tone="accent">{`${i18n._(msg`現在のバージョン:`)} ${current.version}`}</Tag>
        )}
        <ScreenLink to="/settings/risk">{i18n._(msg`リスク設定 →`)}</ScreenLink>
        <ScreenLink to="/controls">{i18n._(msg`統制状態 →`)}</ScreenLink>
      </ScreenHeader>

      <Note>
        {i18n._(
          msg`全体前提条件（税・手数料・為替・費用上限。FR-17）の閲覧と変更を行います。変更は利用者のみが行えます。リスク上限・監視銘柄・`,
        )}
        <strong>{i18n._(msg`市場監視パラメータ（変動閾値・クールダウン）`)}</strong>
        {i18n._(msg`は「リスク設定」画面（SC-02）で扱います。`)}
      </Note>

      {/* SC-01, FR-13, #423, IADR-0164 決定1: 収集間隔は**起動時構成**である。
          入力欄を作らないだけでは「未実装の項目」に見え、次に画面を触る者が善意で実装してしまう。
          **変更しないことが裁定である**ことを画面に明記する。**`role` は付けない**——常時表示される
          静的な注記であり、他の警告と同じ緊急度で読ませると警告全体が軽くなる（`Note` は role を持たない）。 */}
      <Note>
        {i18n._(msg`収集間隔（情報収集・市場監視のポーリング間隔）は`)}
        <strong>{i18n._(msg`本画面からも API からも変更しません`)}</strong>
        {i18n._(
          msg`。起動時の構成値として運用します（費用・負荷のパラメータであり、月報レビュー時に構成で見直します）。`,
        )}
      </Note>

      <QueryPhase
        query={assumptionsQuery}
        errorTitle={
          notFound
            ? i18n._(msg`設定情報は利用できません。`)
            : i18n._(msg`設定情報の取得に失敗しました。`)
        }
        canRetry={!notFound}
      >
        {(loaded) =>
          form === null ? null : (
            <>
              <Panel
                heading={i18n._(
                  msg`§1 全体前提条件（FR-17）— 税・手数料・為替・計算方針・費用上限`,
                )}
              >
                {/* SC-01 §1, #424, IADR-0162 決定4: **供給可否はサーバが宣言する。**
                    `isResolved`（＝`Version > 0`）は ConfigurationService 由来の値を一度でも解決できたかを
                    サーバが宣言したものであり、画面はそれに従う（値の中身から推測しない）。
                    未解決のとき表示しているのは**組み込みの既定値であって権威値ではない**——「取得できている値」と
                    同じ見た目で出すと、利用者は画面の数字が実際の運用値だと信じてしまう（05_screens 共通規約）。
                    **静的な宣言であり結果通知ではないため `role` は付けない**（`Note tone="err"`）。 */}
                {!loaded.isResolved && (
                  <Note tone="err">
                    {i18n._(msg`全体前提条件を`)}
                    <strong>{METRIC_NOT_SUPPLIED_TEXT}</strong>
                    {i18n._(msg`。以下に表示しているのは`)}
                    <strong>
                      {i18n._(msg`組み込みの既定値であり、実際に適用されている値ではありません`)}
                    </strong>
                    {i18n._(msg`（設定サービスの値を一度も解決できていません）。`)}
                  </Note>
                )}

                <form onSubmit={handleSubmit} aria-label={i18n._(msg`全体前提条件の変更`)}>
                  <Kv columns={3}>
                    <Field
                      id="capitalGainsTaxRate"
                      label={FIELD_LABELS.capitalGainsTaxRate}
                      value={form.capitalGainsTaxRate}
                      onChange={(v) => setForm({ ...form, capitalGainsTaxRate: v })}
                    />
                    <Field
                      id="fxSpreadRatio"
                      label={FIELD_LABELS.fxSpreadRatio}
                      value={form.fxSpreadRatio}
                      onChange={(v) => setForm({ ...form, fxSpreadRatio: v })}
                    />
                    <Field
                      id="minimumExpectedProfitMultiple"
                      label={FIELD_LABELS.minimumExpectedProfitMultiple}
                      value={form.minimumExpectedProfitMultiple}
                      onChange={(v) => setForm({ ...form, minimumExpectedProfitMultiple: v })}
                    />
                  </Kv>

                  <fieldset className="mt-3 border-0 p-0">
                    <legend className="mb-1 text-[10.5px] tracking-[0.05em] text-fg-muted">
                      {i18n._(msg`日本株 手数料体系`)}
                    </legend>
                    <Kv columns={3}>
                      <Field
                        id="jpRate"
                        label={FIELD_LABELS.jpRate}
                        value={form.jpRate}
                        onChange={(v) => setForm({ ...form, jpRate: v })}
                      />
                      <Field
                        id="jpMin"
                        label={FIELD_LABELS.jpMin}
                        value={form.jpMin}
                        onChange={(v) => setForm({ ...form, jpMin: v })}
                      />
                      <Field
                        id="jpCap"
                        label={FIELD_LABELS.jpCap}
                        value={form.jpCap}
                        onChange={(v) => setForm({ ...form, jpCap: v })}
                      />
                    </Kv>
                  </fieldset>

                  <fieldset className="mt-3 border-0 p-0">
                    <legend className="mb-1 text-[10.5px] tracking-[0.05em] text-fg-muted">
                      {i18n._(msg`米国株 手数料体系`)}
                    </legend>
                    <Kv columns={3}>
                      <Field
                        id="usRate"
                        label={FIELD_LABELS.usRate}
                        value={form.usRate}
                        onChange={(v) => setForm({ ...form, usRate: v })}
                      />
                      <Field
                        id="usMin"
                        label={FIELD_LABELS.usMin}
                        value={form.usMin}
                        onChange={(v) => setForm({ ...form, usMin: v })}
                      />
                      <Field
                        id="usCap"
                        label={FIELD_LABELS.usCap}
                        value={form.usCap}
                        onChange={(v) => setForm({ ...form, usCap: v })}
                      />
                    </Kv>
                  </fieldset>

                  <fieldset className="mt-3 border-0 p-0">
                    <legend className="mb-1 text-[10.5px] tracking-[0.05em] text-fg-muted">
                      {i18n._(msg`月次費用上限`)}
                    </legend>
                    <Kv columns={4}>
                      <Field
                        id="costTotal"
                        label={FIELD_LABELS.costTotal}
                        value={form.costTotal}
                        onChange={(v) => setForm({ ...form, costTotal: v })}
                      />
                      <Field
                        id="costLlm"
                        label={FIELD_LABELS.costLlm}
                        value={form.costLlm}
                        onChange={(v) => setForm({ ...form, costLlm: v })}
                      />
                      <Field
                        id="costInfra"
                        label={FIELD_LABELS.costInfra}
                        value={form.costInfra}
                        onChange={(v) => setForm({ ...form, costInfra: v })}
                      />
                      <Field
                        id="costData"
                        label={FIELD_LABELS.costData}
                        value={form.costData}
                        onChange={(v) => setForm({ ...form, costData: v })}
                      />
                    </Kv>
                  </fieldset>

                  <div className="mt-3">
                    {/* 🔴 `requiredHint` を使わない。**アクセシブル名に「*」「必須」が混ざり**、
                        `getByLabelText('変更理由')`（役割とテキストで引く既存テスト・E2E）が外れる。
                        必須である旨は下の補足テキストで述べる。 */}
                    <Label htmlFor="reason">{i18n._(msg`変更理由`)}</Label>
                    <Input
                      id="reason"
                      value={reason}
                      onChange={(e) => setReason(e.target.value)}
                      required
                      className="mt-1 w-full"
                    />
                    <span className="mt-1 block text-[10.5px] text-fg-muted">
                      {i18n._(msg`監査のため必須・1 文字以上`)}
                    </span>
                  </div>

                  {invalid.length > 0 && (
                    <p role="alert" className="mt-2 text-[11px] text-danger">
                      {`${i18n._(msg`未入力または数値でない項目があります:`)} ${invalid.join('、')}`}
                    </p>
                  )}

                  <div className="mt-2 flex flex-wrap items-center gap-3">
                    <Button
                      type="submit"
                      variant="primary"
                      disabled={reason.trim() === '' || saving || invalid.length > 0}
                    >
                      {i18n._(msg`保存`)}
                    </Button>
                    <span className="text-[11px] text-fg-muted">
                      {i18n._(
                        msg`→ 履歴追記・監査ログ・Discord 通知（FR-09）。各報告書に適用版を記録`,
                      )}
                    </span>
                    {saving && <span role="status">{i18n._(msg`保存中…`)}</span>}
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

                <Note>
                  {i18n._(
                    msg`楽観排他: 表示中の版と現行版が一致する場合のみ保存します。競合したときは再読込を促します（自動では上書きしません）。`,
                  )}
                </Note>
              </Panel>

              <HistoryPanel query={historyQuery} />
            </>
          )
        }
      </QueryPhase>
    </section>
  );
}

// 数値入力（文字列で保持）。ラベルと入力を id で関連づける（getByLabelText で参照可能）。
// hi-fi モックの `.kv`（ラベル ＋ 値の箱）へ載せる。
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
    <KvItem label={<Label htmlFor={id}>{label}</Label>}>
      <Input
        id={id}
        type="number"
        step="any"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="w-full border-0 bg-transparent p-0"
      />
    </KvItem>
  );
}

// FR-17: 変更履歴（新しい順）。取得不能・0 件はその旨を明示する（縮退表示）。
// **失敗と 0 件を混同しない**——`QueryPhase` の `isEmpty` は成功したデータにだけ効く。
function HistoryPanel({
  query,
}: {
  query: ReturnType<typeof useAssumptionsHistory>;
}) {
  return (
    <Panel heading={i18n._(msg`変更履歴`)}>
      <QueryPhase
        query={query}
        loadingLabel={i18n._(msg`履歴を確認中…`)}
        errorTitle={i18n._(msg`変更履歴は利用できません。`)}
        isEmpty={(rows: ChangeEntry[]) => rows.length === 0}
        empty={<Note>{i18n._(msg`変更履歴はありません。`)}</Note>}
      >
        {(history: ChangeEntry[]) => (
          <Table aria-label={i18n._(msg`変更履歴`)}>
            <TableHead>
              <TableRow>
                <TableHeaderCell>{i18n._(msg`バージョン`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`変更者`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`理由`)}</TableHeaderCell>
                <TableHeaderCell>{i18n._(msg`日時`)}</TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {history.map((h, i) => (
                <TableRow key={`${i}-${h.version}-${h.changedAt}`}>
                  <TableCell>{h.version}</TableCell>
                  <TableCell>{h.actor}</TableCell>
                  <TableCell>{h.reason}</TableCell>
                  <TableCell>{formatAt(h.changedAt)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </QueryPhase>
    </Panel>
  );
}
