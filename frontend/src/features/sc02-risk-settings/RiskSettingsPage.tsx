import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import type {
  BannedSymbol,
  LimitFieldKey,
  RiskLimitSettings,
  RiskManagementSettings,
  RiskStatusView,
  SettingsChangeEntry,
  TradingGuardSettings,
} from '../risk/contracts';
import {
  brokerProviderLabel,
  BROKER_PROVIDER_OPTIONS,
  changeTypeLabel,
  describeLimitRange,
  equityAmountText,
  formatAmount,
  formatAt,
  isEquityRatioField,
  isLiveProvider,
  LIMIT_FIELDS,
  LIMIT_FIELD_KEYS,
  limitInputToWire,
  LIVE_ACKNOWLEDGEMENT_PHRASE,
  marketLabel,
  MARKET_OPTIONS,
  METRIC_NOT_SUPPLIED_TEXT,
  RISKY_PRODUCT_TYPES,
  PRODUCT_TYPE_OPTIONS,
  stageLabel,
  validateLimitInput,
  wireToLimitInput,
} from '../risk/contracts';
import { PaperModeBanner } from '../shared/PaperModeBanner';
import { MonitorParametersForm } from './MonitorParametersForm';
import { Stage1TradeCountForm } from './Stage1TradeCountForm';
import { WatchlistForm } from './WatchlistForm';

// SC-02, FR-13, FR-19, FR-20, UC-06, ADR-0007, ADR-0008, IADR-0084, IADR-0086: リスク設定画面（リスク上限・ガードの閲覧/変更）。
// データ源は BFF `/bff/risk-controls/settings`（RiskManagementService・OwnerOnly）。変更は利用者のみ・理由必須。
// リスク上限（limits 8 項目・#186）に加え、取引ガード（guard・#188/IADR-0086）を変更できる。段階（stage）は参照表示に留める
// （段階変更は段階ゲート承認フロー＝#20/#165 Bot 側と重複するため直接は開かない）。検証(400)・競合(409)はメッセージ表示し、
// 破壊的な自動再試行はしない（安全既定）。ガードの危険な緩和は明示確認を要求する（fail-safe）。

// FR-10, SC-02, #362, IADR-0151: フォームは**画面の単位**（百分率／件数／倍率）を文字列で保持し、
// 送信時にワイヤの単位（比率／整数／倍率）へ変換する（type=number 制御入力の往復問題を避ける・SC-01 と同方針）。
// 単位の定義・値域・変換は `contracts.ts`（`LIMIT_FIELDS` ほか）が単一情報源であり、ここでは持たない。
type FormModel = Record<LimitFieldKey, string>;

type Status = 'loading' | 'ok' | 'notFound' | 'error';
type HistoryStatus = 'loading' | 'ok' | 'unavailable';
type SaveState = 'idle' | 'saving' | 'error';

function toForm(l: RiskLimitSettings): FormModel {
  return Object.fromEntries(
    LIMIT_FIELD_KEYS.map((k) => [k, wireToLimitInput(k, l[k])]),
  ) as FormModel;
}

// FR-10, #362, IADR-0151 決定2: 空欄・非数値**および値域外**を「無効」とし、保存を無効化する。
// 黙って 0 を送らない（安全既定）。**実効はサーバ側（`RiskLimitBounds`）である**——ここは利用者への
// 即時提示であり、画面だけの関門は API 直叩きで消える（IADR-0141 決定1 と同じ判断）。
function invalidFieldMessages(f: FormModel): string[] {
  return LIMIT_FIELD_KEYS.map((k) => validateLimitInput(k, f[k])).filter((m): m is string => m !== null);
}

// FR-10, SC-02, #362, #389, IADR-0130, IADR-0151: **PUT の本文は `*Ratio`（equity 比）である。**
//
// #389 まで本文は旧名（金額キー）のままで、保存は 400 で拒否されていた。これは不具合ではなく、
// 「入力欄が金額のまま比率キーを送ると、`35000` が **equity の 35,000 倍**として保存され統制が
// 事実上無効になる」ことを避けるための安全側の状態だった。
//
// #362 でその前提を外した。**外してよい条件は次の 2 つが同時に満たされることである**（IADR-0151 決定3）:
//   1. 入力が**百分率**になり、単位が画面上に常に見えている（`LIMIT_FIELDS`）
//   2. **値域の関門がサーバ側に実在する**（`RiskLimitBounds`。本 issue で新設。それまでサーバは
//      `MaxOrderAmountRatio = 35000` をそのまま受理していた）
// 画面側の検証（`invalidFieldMessages`）は 1 の即時提示であり、実効は 2 である。
type RiskLimitsPayload = Record<LimitFieldKey, number>;

/** 画面の入力をワイヤの値へ変換する。**呼び出し前に `invalidFieldMessages` が空であることが前提**である。 */
function fromForm(f: FormModel): RiskLimitsPayload {
  return Object.fromEntries(
    // 事前検証を通過していれば null にはならない。万一 null なら 0 ではなく NaN を送り、
    // サーバの値域検証（0 以下は拒否）で確実に落とす（黙って 0＝「発注できない」を保存しない）。
    LIMIT_FIELD_KEYS.map((k) => [k, limitInputToWire(k, f[k]) ?? Number.NaN]),
  ) as RiskLimitsPayload;
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
  // FR-10, SC-02, #362, IADR-0151 決定4: 実額の併記と実弾切替モーダル③に使う equity・統制状態。
  // **ページで 1 回だけ取得して配る**（2 か所で別々に取りに行くと、同じ画面が違う equity を見る状態を作れる）。
  // 別サービスの障害を本ページの障害にしないため、失敗しても null で縮退する（実額は「—」になる）。
  const [riskStatus, setRiskStatus] = useState<RiskStatusView | null>(null);

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

  async function loadRiskStatus(): Promise<void> {
    try {
      setRiskStatus(await apiFetch<RiskStatusView>('/risk-controls/status'));
    } catch {
      // 取得不能は実額を「—」に縮退させる（実弾切替は別途、equity 不明を理由に禁じる）。
      setRiskStatus(null);
    }
  }

  useEffect(() => {
    void loadCurrent();
    void loadHistory();
    void loadRiskStatus();
  }, []);

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    // 理由必須・全フィールド有効（値域内）を送信の前提とする（安全既定。ボタン無効化と二重の防御）。
    if (!current || !form || reason.trim() === '' || invalidFieldMessages(form).length > 0) return;
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
      // 上限が変われば解決済みの実額（統制状態）も変わる。実額の併記を古いままにしない。
      await loadRiskStatus();
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveState('error');
      setSaveError(saveMessageOf(err));
    }
  }

  return (
    <section>
      {/* FR-12, #334: 内蔵 paper 稼働中の警告バナー（画面上部に常時表示。05_screens 共通規約）。 */}
      <PaperModeBanner provider={current?.brokerProvider} />
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
              {/* FR-10, SC-02, #362, #364, IADR-0151 決定1 / IADR-0152 決定6: 割合は**百分率**で入力する。
                  equity 比の項目には現在 equity での実額を併記する（割合だけでは実効額を判断できない）。
                  #364 で判定の基準通貨が USD へ移行したため、実額は**米ドル建て**であり計画 SC-02 の
                  表記例「25%（$750）」と一致する。 */}
              {/* SC-02, #424, IADR-0162: equity が供給されていないときは規約の文言で明示する
                  （「—」「取得できません」といった弱い表現に落とさない。05_screens 共通規約）。 */}
              <p>
                equity 比の項目は<strong>百分率（%）で入力</strong>します（25 ＝ equity の 25%）。比率（0.25）ではありません。
                各項目には<strong>現在の equity（
                {riskStatus === null ? METRIC_NOT_SUPPLIED_TEXT : formatAmount(riskStatus.capital)}
                ）での実額</strong>を併記します（基準通貨＝米ドル建て。統制の判定はすべて自己資金の USD 建てで行います）。
              </p>
              {riskStatus === null && (
                <p role="alert">
                  現在の equity を取得できていないため、<strong>実額を併記できません</strong>。
                  実額が「{METRIC_NOT_SUPPLIED_TEXT}」と表示されている項目は、
                  <strong>0 でも「該当なし」でもなく、判断材料が無い状態</strong>です。
                </p>
              )}
              {LIMIT_FIELD_KEYS.map((k) => (
                <LimitField
                  key={k}
                  fieldKey={k}
                  value={form[k]}
                  equity={riskStatus?.capital ?? null}
                  onChange={(v) => setForm({ ...form, [k]: v })}
                />
              ))}
            </fieldset>

            <div>
              <label htmlFor="reason">変更理由</label>
              <textarea id="reason" value={reason} onChange={(e) => setReason(e.target.value)} required />
            </div>

            {invalidFieldMessages(form).length > 0 && (
              <p role="alert">
                入力できない値があります: {invalidFieldMessages(form).join('、')}
              </p>
            )}

            <button
              type="submit"
              disabled={reason.trim() === '' || saveState === 'saving' || invalidFieldMessages(form).length > 0}
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
          <StageView stage={current.stage} provider={current.brokerProvider} />
          {/* SC-02, FR-20, FR-13, #423, IADR-0164 決定4: Stage 1 の最小取引件数。
              計画は「**運用段階（FR-20）の参照表示の近くに置く**」と定める（段階ゲートの合格条件に
              属する値であるため）。したがって StageView の直後に置く。 */}
          <Stage1TradeCountForm
            current={current.stage1MinimumTradeCount}
            onSaved={async () => {
              await loadCurrent();
              await loadHistory();
            }}
          />
          <BrokerProviderForm
            current={current.brokerProvider}
            stageMode={current.stage.mode}
            stage={current.stage.stage}
            status={riskStatus}
            onSaved={async () => {
              await loadCurrent();
              await loadHistory();
              await loadRiskStatus();
            }}
          />
          <HistoryView status={historyStatus} history={history} />
        </>
      )}

      {/* SC-02, FR-03, FR-13, IADR-0090: 監視銘柄（watchlist）セクション。別サービス（MarketMonitorService `/monitor/watchlist`）を
          消費するため、リスク設定の取得可否に連動させず独立してロード/縮退する（片方の障害・BFF 未結線を巻き込まない・fail-safe）。 */}
      <WatchlistForm />

      {/* SC-02, FR-03, FR-13, #423, IADR-0164 決定2: 市場監視パラメータ（変動閾値・クールダウン）。
          **2026-08-07 の裁定で SC-01 §2 から移管された。** 計画は「**監視銘柄の近くに置く**」と定める
          ——「どの銘柄を、どれだけ動いたら」は 1 つの設定だからである。したがって WatchlistForm の直後に置く。
          監視銘柄と同じ MarketMonitorService 由来のため、同じく独立してロード/縮退する。 */}
      <MonitorParametersForm />
    </section>
  );
}

// FR-10, SC-02, #362, IADR-0151: リスク上限 1 項目の入力欄。
//
// **単位を必ず画面に出す**（`%` / `%/日` / `件` / `連敗` / `倍`）。比率・百分率・金額の取り違えは統制で
// 最も危険な誤りであり（IADR-0130 決定1）、単位が見えていれば目視で検出できる。
//
// equity 比の項目には**入力中の値に対する実額**を併記する。サーバの `RiskStatusView.maxOrderAmount` は
// **現在保存されている設定**から解決した実額であり、保存前の入力値は表せない。よって画面が
// `resolveEquityAmount(equity, 入力比率)` で計算する（equity の出どころは 1 つに保つ）。
//
// ラベルは単位を含む文字列を <label> に置き、`getByLabelText` で参照できるようにする。
function LimitField({
  fieldKey,
  value,
  equity,
  onChange,
}: {
  fieldKey: LimitFieldKey;
  value: string;
  equity: number | null;
  onChange: (v: string) => void;
}) {
  const spec = LIMIT_FIELDS[fieldKey];
  const label = `${spec.label} ${spec.unit}`;
  const error = validateLimitInput(fieldKey, value);
  // SC-02, #424, IADR-0162 決定3: equity が供給されていないことを「—」で描かない（05_screens の共通規約）。
  // 「—」は**対象なし**（入力が読めず実額が定義できない）にだけ用いる。
  const amountText = isEquityRatioField(fieldKey)
    ? equityAmountText(equity, limitInputToWire(fieldKey, value))
    : '';

  return (
    <div>
      <label htmlFor={fieldKey}>{label}</label>
      <input
        id={fieldKey}
        type="number"
        step="any"
        value={value}
        aria-invalid={error !== null}
        aria-describedby={`${fieldKey}-help`}
        onChange={(e) => onChange(e.target.value)}
      />
      <span id={`${fieldKey}-help`}>
        {`許容範囲: ${describeLimitRange(fieldKey)}`}
        {isEquityRatioField(fieldKey) && (
          // equity が未供給なら「取得できていません（供給元がありません）」、入力が読めないだけなら「—」。
          // 併記できないことを黙って隠さない。
          <>
            {` / 現在の equity での実額: ${amountText}`}
            {spec.kind === 'equityPercentPerDay' && amountText !== METRIC_NOT_SUPPLIED_TEXT && '/日'}
          </>
        )}
      </span>
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
  // FR-19, ADR-0016 決定1, #332: 商品種別は 3 値。現物以外（信用買い・空売り）の**新規有効化**を危険な緩和とみなす。
  for (const risky of RISKY_PRODUCT_TYPES) {
    if (!original.enabledProductTypes.includes(risky.value) && form.enabledProductTypes.includes(risky.value)) {
      dangers.push(risky.label);
    }
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

  // 現在値に追随してフォームを初期化する。ただし比較対象を「値のシグネチャ」にして、ガードの内容が実際に変わったとき
  // （＝自分の保存成功後の再取得や外部変更）だけ初期化する。隣接するリスク上限フォームの保存でも親の current は再生成
  // され guard の参照は変わるが、ガードの内容が同一なら初期化しない（編集中のガード内容・理由・危険確認・下書きを
  // 黙って破棄しない・fail-safe / #188 AI レビュー指摘）。
  //
  // 🔴 #539, NFR: **これを `useEffect` で行わない。** #498（`.ai-context/specs/20260821_498_...md`）で
  // 実証された機序と同型——commit（DOM が見える）と passive effect の実行の間には窓があり、その窓で
  // 利用者の入力が入ると、遅れて流れてきた初期化が入力を黙って巻き戻す。React 公式の「prop が変わった
  // ときに state を調整する」書き方（前回のシグネチャを state に持ち、描画中に同期的に比較・調整する）へ
  // 寄せる。**mount 時は走らない**ため窓そのものが消え、`guardSignature` が実際に変わったときの初期化は
  // 従来どおり効く。
  const guardSignature = JSON.stringify(guard);
  const [syncedGuardSignature, setSyncedGuardSignature] = useState(guardSignature);
  if (syncedGuardSignature !== guardSignature) {
    setSyncedGuardSignature(guardSignature);
    setForm(toGuardForm(guard));
    setReason('');
    setConfirmDanger(false);
    setNewSymbol('');
    setNewReason('');
  }

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
      // 成功後は現在値・履歴を再取得（guardSignature の変化を描画中に検知して form/理由/確認を初期化する）。破壊的操作はしない。
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

// FR-20, #334, INDEX 決定 46: 段階（参照専用）と発注先（現在値）の並記。
// **運用段階と発注先は独立した 2 軸であり、1 行に混ぜて表示しない**（05_screens 共通規約）。
// 段階変更は段階ゲート承認フロー（#165 Bot 側）、発注先の変更は下の「発注先（変更）」で行う。
function StageView({ stage, provider }: { stage: RiskManagementSettings['stage']; provider: number }) {
  return (
    <Section title="運用段階と発注先（参照）">
      <dl>
        <dt>運用段階</dt>
        <dd>{stageLabel(stage.stage)}</dd>
        <dt>発注先</dt>
        <dd>{brokerProviderLabel(provider)}</dd>
        <dt>段階の既定発注先</dt>
        <dd>{brokerProviderLabel(stage.mode)}</dd>
        {/* FR-20, #333, #389, IADR-0136: 段階の発注可能額は**総資金比**である（Stage 2 は 0.30 ＝ 30%）。
            #389 まで画面はこの値を一切表示しておらず（型とモックにしか存在しなかった）、キー名のずれが
            描画結果に現れなかった。表示することで以後のずれは画面に出る。 */}
        <dt>段階の発注可能額（総資金比）</dt>
        <dd>{stage.capitalCapRatio}</dd>
      </dl>
      <p>
        運用段階と発注先は独立した 2 軸です。段階が定める発注先は既定の組み合わせを示すにとどまります（FR-20）。
      </p>
    </Section>
  );
}

// FR-20, FR-13, SC-02, INDEX 決定 46, #334, IADR-0141: 発注先（Broker Provider）の変更フォーム。
//
// **変更操作を持つ画面は SC-02 だけである**（SC-03 は参照専用）。他のリスク設定と同様に変更理由必須・
// 監査ログ記録・版（楽観排他）の対象とする（05_screens 共通規約）。
//
// **実弾（moomoo REAL）への切替は警告モーダルを伴い、「OK」1 押しでは通過できない。** モーダルは計画が
// 定める 4 点を必ず提示する（05_screens SC-02 / FR-20 (1)）:
//   ① これ以降の注文は実際の資金で執行される旨
//   ② 切替先と現在の Stage の組み合わせの妥当性（Stage 1 のままなら段階ゲートを飛ばしている旨）
//   ③ 現在の equity と、それに対する統制値の実額
//   ④ 確認のための明示的な操作（チェックボックスの同意と「REAL」の文字入力）
//
// 同じ関門はサーバ側にもある（IADR-0141 決定1）。画面だけの統制は API 直叩きで消えるためであり、
// ここでの二重化は冗長ではない。
type ProviderSaveState = 'idle' | 'saving' | 'error';

// FR-20 (1), SC-02, #422, IADR-0161 決定4: 計画（FR-20 の 2026-08-07 追記 (1)）が画面へ出すことを
// 明示的に義務づけた文言。
//
//   「**設定は保存できるが、発注は段階が実弾を既定とするまで拒否する**——『発注先の変更が保存できる』ことは
//     『発注できる』ことを意味しない。**Stage 1 のまま実弾を選んでも発注は行われない**。
//     **この旨を SC-02 の警告モーダルにも含める**（利用者が警告に同意したうえで 1 件も発注されない理由が
//     画面に出ないと「壊れている」と判断されるため）」
//
// **出すのは段階が実弾を既定としないときだけである。** 段階が既に実弾（Stage 2 / 3）なら注文は実際に
// 発注されるため、同じ文言を出すと嘘になる（狼少年にもなる）。条件は `skipsStageGate` と同一。
const STAGE_GATE_BLOCKS_LIVE_ORDERS =
  'ただし、段階が実弾を既定とするまで発注は行われません。' +
  '発注先の設定は保存できますが、実弾の注文は段階ゲートが拒否します（1 件も発注されません）。' +
  '実弾で発注するには運用段階の昇格が必要です。';

function BrokerProviderForm({
  current,
  stageMode,
  stage,
  status,
  onSaved,
}: {
  current: number;
  stageMode: number;
  stage: number;
  /**
   * ③ の提示に用いる equity と統制値の実額。**ページが 1 回取得したものを受け取る**（#362）。
   * 以前は本フォームが独自に `/risk-controls/status` を叩いていたが、リスク上限の実額併記でも同じ値が
   * 要るため、**同じ画面が 2 つの equity を見る**状態を避けてページへ引き上げた。`null` は取得不能。
   */
  status: RiskStatusView | null;
  onSaved: () => Promise<void> | void;
}) {
  const [selected, setSelected] = useState<number>(current);
  const [reason, setReason] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [acknowledged, setAcknowledged] = useState(false);
  const [phrase, setPhrase] = useState('');
  const [saveState, setSaveState] = useState<ProviderSaveState>('idle');
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);

  // 現在値に追随して選択を初期化する（自分の保存成功後の再取得・外部変更）。
  //
  // 🔴 #539, NFR: **これを `useEffect` で行わない。** #498（`.ai-context/specs/20260821_498_...md`）で
  // 実証された機序と同型——commit（DOM が見える）と passive effect の実行の間には窓があり、その窓で
  // 利用者の入力が入ると、遅れて流れてきた初期化が入力を黙って巻き戻す。React 公式の「prop が変わった
  // ときに state を調整する」書き方（前回の prop を state に持ち、描画中に同期的に比較・調整する）へ
  // 寄せる。**mount 時は走らない**ため窓そのものが消え、`current` が実際に変わったときの初期化は
  // 従来どおり効く。
  const [syncedCurrent, setSyncedCurrent] = useState(current);
  if (syncedCurrent !== current) {
    setSyncedCurrent(current);
    setSelected(current);
    setReason('');
    setModalOpen(false);
    setAcknowledged(false);
    setPhrase('');
  }

  const live = isLiveProvider(selected);
  const unchanged = selected === current;
  const reasonMissing = reason.trim() === '';
  // ④ チェックボックスの同意と「REAL」の文字入力の**両方**が揃うまで切替を実行できない。
  const confirmationComplete = acknowledged && phrase.trim() === LIVE_ACKNOWLEDGEMENT_PHRASE;
  // ③ を提示できない状態（equity が取れない）では実弾へ切り替えない。提示できない項目がある確認は
  // 「読ませたうえでの同意」にならないため、安全側に倒す（IADR-0141 残余リスク）。
  const equityUnavailable = status === null;
  // ② Stage 1 のまま実弾＝段階ゲートを飛ばしている。**保存は妨げない**（計画）。警告として提示する。
  const skipsStageGate = live && !isLiveProvider(stageMode);

  async function submit(): Promise<void> {
    setSaveState('saving');
    setSaveError(null);
    setSavedNotice(null);
    try {
      await apiFetch('/risk-controls/settings/broker-provider', {
        method: 'PUT',
        json: {
          provider: selected,
          reason: reason.trim(),
          acknowledgedLiveTrading: live ? acknowledged : false,
          acknowledgement: live ? phrase.trim() : null,
        },
      });
      setSaveState('idle');
      setModalOpen(false);
      setAcknowledged(false);
      setPhrase('');
      setSavedNotice('保存しました。');
      await onSaved();
    } catch (err: unknown) {
      // 409/400 等は自動再試行せずメッセージ表示に留める（安全既定）。
      setSaveState('error');
      setSaveError(saveMessageOf(err));
      setModalOpen(false);
    }
  }

  async function handleSubmit(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    if (unchanged || reasonMissing || saveState === 'saving') return;
    if (live) {
      // 実弾は直接保存しない。**必ず警告モーダルを経由する。**
      setModalOpen(true);
      return;
    }
    await submit();
  }

  return (
    <Section title="発注先（変更）">
      <form onSubmit={handleSubmit} aria-label="発注先の変更">
        <p>
          現在の発注先: <strong>{brokerProviderLabel(current)}</strong>
        </p>
        <fieldset>
          <legend>発注先</legend>
          {BROKER_PROVIDER_OPTIONS.map((o) => (
            <div key={`bp-${o.value}`}>
              <label>
                <input
                  type="radio"
                  name="broker-provider"
                  value={o.value}
                  checked={selected === o.value}
                  onChange={() => setSelected(o.value)}
                />
                {o.label}
              </label>
            </div>
          ))}
        </fieldset>

        <div>
          <label htmlFor="broker-provider-reason">変更理由</label>
          <textarea
            id="broker-provider-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            required
          />
        </div>

        {skipsStageGate && (
          <p role="alert">
            現在の運用段階（{stageLabel(stage)}）が想定する発注先は
            {brokerProviderLabel(stageMode)}です。実弾へ切り替えると段階ゲートを飛ばすことになります。
            {/* FR-20 (1), #422: 保存できることは発注できることを意味しない。 */}
            <strong>{STAGE_GATE_BLOCKS_LIVE_ORDERS}</strong>
          </p>
        )}

        <button type="submit" disabled={unchanged || reasonMissing || saveState === 'saving'}>
          {live ? '実弾への切替を確認する' : '保存'}
        </button>
        {unchanged && <p>発注先は変更されていません。</p>}
        {saveState === 'saving' && <span role="status">保存中…</span>}
        {savedNotice && <p role="status">{savedNotice}</p>}
        {saveError && <p role="alert">{saveError}</p>}
      </form>

      {modalOpen && (
        <LiveSwitchWarningModal
          stage={stage}
          stageMode={stageMode}
          skipsStageGate={skipsStageGate}
          status={status}
          equityUnavailable={equityUnavailable}
          acknowledged={acknowledged}
          phrase={phrase}
          confirmationComplete={confirmationComplete}
          saving={saveState === 'saving'}
          onAcknowledgedChange={setAcknowledged}
          onPhraseChange={setPhrase}
          onCancel={() => {
            setModalOpen(false);
            setAcknowledged(false);
            setPhrase('');
          }}
          onConfirm={() => void submit()}
        />
      )}
    </Section>
  );
}

// FR-20 (1), 05_screens SC-02, #334, IADR-0141: 実弾切替の警告モーダル。計画が定める 4 点を必ず描く。
// **切替ボタンは「同意」と「REAL の入力」が両方揃うまで無効である**（「OK」1 押しで通過させない）。
function LiveSwitchWarningModal({
  stage,
  stageMode,
  skipsStageGate,
  status,
  equityUnavailable,
  acknowledged,
  phrase,
  confirmationComplete,
  saving,
  onAcknowledgedChange,
  onPhraseChange,
  onCancel,
  onConfirm,
}: {
  stage: number;
  stageMode: number;
  skipsStageGate: boolean;
  status: RiskStatusView | null;
  equityUnavailable: boolean;
  acknowledged: boolean;
  phrase: string;
  confirmationComplete: boolean;
  saving: boolean;
  onAcknowledgedChange: (v: boolean) => void;
  onPhraseChange: (v: string) => void;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <div role="dialog" aria-modal="true" aria-label="実弾（moomoo REAL）への切替の確認">
      {/* ① 実資金で執行される旨 */}
      <p role="alert">
        <strong>これ以降の注文は実際の資金で執行されます。</strong>
      </p>

      {/* ② 切替先と現在の Stage の組み合わせの妥当性 */}
      <p>
        現在の運用段階: <strong>{stageLabel(stage)}</strong>／段階が想定する発注先:{' '}
        <strong>{brokerProviderLabel(stageMode)}</strong>
      </p>
      {skipsStageGate && (
        <>
          <p role="alert">
            この組み合わせは段階ゲート（統制違反 0 件・60 営業日・取引 100 件）を飛ばしています。
          </p>
          {/*
            FR-20 (1), #422: **同意しても 1 件も発注されない**旨を必ず出す。これが無いと
            「警告に同意したのに発注されない＝壊れている」と読まれる（計画が名指しした誤読）。
          */}
          <p role="alert">
            <strong>{STAGE_GATE_BLOCKS_LIVE_ORDERS}</strong>
          </p>
        </>
      )}

      {/* ③ 現在の equity と、それに対する統制値の実額 */}
      {equityUnavailable || status === null ? (
        <p role="alert">
          現在の equity と統制値を取得できないため、実弾へ切り替えられません。時間をおいて再度お試しください。
        </p>
      ) : (
        <table aria-label="現在の equity と統制値">
          <tbody>
            <tr>
              <th>現在の equity（自己資金）</th>
              <td>{formatAmount(status.capital)}</td>
            </tr>
            <tr>
              <th>1 注文あたり発注金額上限</th>
              <td>{formatAmount(status.maxOrderAmount)}</td>
            </tr>
            <tr>
              <th>1 日あたり発注金額上限</th>
              <td>{formatAmount(status.maxDailyOrderAmount)}</td>
            </tr>
            <tr>
              <th>保有建玉数上限</th>
              <td>{status.maxOpenPositions}</td>
            </tr>
          </tbody>
        </table>
      )}

      {/* ④ 確認のための明示的な操作（チェックボックスの同意と「REAL」の文字入力） */}
      <div>
        <label>
          <input
            type="checkbox"
            checked={acknowledged}
            onChange={(e) => onAcknowledgedChange(e.target.checked)}
          />
          実資金で執行されることを理解しました
        </label>
      </div>
      <div>
        <label htmlFor="live-switch-phrase">
          確認のため「{LIVE_ACKNOWLEDGEMENT_PHRASE}」と入力してください
        </label>
        <input
          id="live-switch-phrase"
          value={phrase}
          onChange={(e) => onPhraseChange(e.target.value)}
        />
      </div>

      <button type="button" onClick={onCancel} disabled={saving}>
        キャンセル
      </button>
      <button
        type="button"
        onClick={onConfirm}
        disabled={!confirmationComplete || equityUnavailable || saving}
      >
        実弾へ切り替える
      </button>
    </div>
  );
}

// FR-11: 変更履歴（新しい順）。取得不能・0 件はその旨を明示する（縮退表示）。
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
