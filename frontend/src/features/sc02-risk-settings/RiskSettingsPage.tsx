import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import type {
  BannedSymbol,
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
  formatAt,
  isLiveProvider,
  LIVE_ACKNOWLEDGEMENT_PHRASE,
  marketLabel,
  MARKET_OPTIONS,
  RISKY_PRODUCT_TYPES,
  PRODUCT_TYPE_OPTIONS,
  stageLabel,
} from '../risk/contracts';
import { PaperModeBanner } from '../shared/PaperModeBanner';
import { WatchlistForm } from './WatchlistForm';

// SC-02, FR-13, FR-19, FR-20, UC-06, ADR-0007, ADR-0008, IADR-0084, IADR-0086: リスク設定画面（リスク上限・ガードの閲覧/変更）。
// データ源は BFF `/bff/risk-controls/settings`（RiskManagementService・OwnerOnly）。変更は利用者のみ・理由必須。
// リスク上限（limits 8 項目・#186）に加え、取引ガード（guard・#188/IADR-0086）を変更できる。段階（stage）は参照表示に留める
// （段階変更は段階ゲート承認フロー＝#20/#165 Bot 側と重複するため直接は開かない）。検証(400)・競合(409)はメッセージ表示し、
// 破壊的な自動再試行はしない（安全既定）。ガードの危険な緩和は明示確認を要求する（fail-safe）。

// フォームは文字列で保持し、送信時に数値へ変換する（type=number 制御入力の往復問題を避ける・SC-01 と同方針）。
interface FormModel {
  // FR-10, #329, #389, IADR-0130: **equity 比**（0.25 ＝ 25%）であり金額ではない。
  maxOrderAmountRatio: string;
  maxDailyOrderAmountRatio: string;
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
// #389: 金額系 2 項目は equity 比である。**「金額上限」というラベルのまま比率を出すと桁が 6 桁ずれて読める**ため、
// 単位をラベルに明示する（`0.25` と `25%` のどちらの表現を採るかは #362 の裁定事項であり、ここでは決めない）。
const FIELD_LABELS: Record<keyof FormModel, string> = {
  maxOrderAmountRatio: '1注文発注額上限（equity 比・0.25＝25%）',
  maxDailyOrderAmountRatio: '1日発注額上限（equity 比/日・1.5＝150%）',
  maxOpenPositions: '保有銘柄数上限',
  dailyLossLimitRatio: '日次損失上限（資金比）',
  perTradeRiskRatio: '1取引リスク（資金比）',
  maxDrawdownRatio: '最大ドローダウン上限（比）',
  losingStreakThreshold: '連敗しきい値',
  losingStreakSizeFactor: '連敗時サイズ縮小係数',
};

function toForm(l: RiskLimitSettings): FormModel {
  return {
    maxOrderAmountRatio: String(l.maxOrderAmountRatio),
    maxDailyOrderAmountRatio: String(l.maxDailyOrderAmountRatio),
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

// FR-10, SC-02, #362, #389: **PUT の本文は旧名（金額）のまま送る。これは意図的な設計判断である。**
//
// サーバの `RiskLimitSettings` は `MaxOrderAmountRatio` / `MaxDailyOrderAmountRatio` を `required` で要求するため、
// 本ペイロードでの保存は **400 で拒否される**（#329 の API 変更以降そうなっている）。
//
// 一見すると「キー名を `*Ratio` に直せば保存が通る」ように見えるが、**それは統制を無効化する変更である**。
// 入力欄の作りが「金額」のまま `maxOrderAmountRatio` を送ると、利用者が従来どおり `35000` と入れたときに
// **equity の 35,000 倍**が上限として設定され、統制が事実上無効のまま保存が成功してしまう。
// 「保存できないこと」は不具合ではなく、#362 が明示的に選んだ**安全側の状態**である
// （#362 本文「拒否される方が安全側と判断した」／IADR-0130 決定）。
//
// **したがって、割合入力の UI・バリデーション（#362）と同時にしか、この形は変えてはならない。**
// 読み取り側（GET・表示）の単位是正は #389 で完了しているが、書き込み側はここで止めてある。
interface LegacyAmountLimitsPayload {
  maxOrderAmount: number;
  maxDailyOrderAmount: number;
  maxOpenPositions: number;
  dailyLossLimitRatio: number;
  perTradeRiskRatio: number;
  maxDrawdownRatio: number;
  losingStreakThreshold: number;
  losingStreakSizeFactor: number;
}

function fromForm(f: FormModel): LegacyAmountLimitsPayload {
  return {
    maxOrderAmount: num(f.maxOrderAmountRatio),
    maxDailyOrderAmount: num(f.maxDailyOrderAmountRatio),
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
              {/* FR-10, #362, #389: 保存経路は現在 400 で拒否される（金額 → equity 比の API 変更に入力 UI が
                  未追随。安全側として意図的にこの状態にしてある）。沈黙の失敗にせず、理由を画面にも書く。 */}
              <p>
                発注額の 2 項目は <strong>equity（自己資金）に対する割合</strong>です（0.25 ＝ 25%）。金額ではありません。
                なお現在、リスク上限の<strong>保存はサーバに拒否されます</strong>（割合入力への作り直しが未完のため、
                誤って金額を保存できないようにしてある安全側の状態です）。
              </p>
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
          <StageView stage={current.stage} provider={current.brokerProvider} />
          <BrokerProviderForm
            current={current.brokerProvider}
            stageMode={current.stage.mode}
            stage={current.stage.stage}
            onSaved={async () => {
              await loadCurrent();
              await loadHistory();
            }}
          />
          <HistoryView status={historyStatus} history={history} />
        </>
      )}

      {/* SC-02, FR-03, FR-13, IADR-0090: 監視銘柄（watchlist）セクション。別サービス（MarketMonitorService `/monitor/watchlist`）を
          消費するため、リスク設定の取得可否に連動させず独立してロード/縮退する（片方の障害・BFF 未結線を巻き込まない・fail-safe）。 */}
      <WatchlistForm />
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

function BrokerProviderForm({
  current,
  stageMode,
  stage,
  onSaved,
}: {
  current: number;
  stageMode: number;
  stage: number;
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
  // ③ の提示に用いる equity と統制値の実額。別サービス障害を本フォームの障害にしないため独立に取得する。
  const [status, setStatus] = useState<RiskStatusView | null>(null);

  // 現在値に追随して選択を初期化する（自分の保存成功後の再取得・外部変更）。
  useEffect(() => {
    setSelected(current);
    setReason('');
    setModalOpen(false);
    setAcknowledged(false);
    setPhrase('');
  }, [current]);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const data = await apiFetch<RiskStatusView>('/risk-controls/status');
        if (!cancelled) setStatus(data);
      } catch {
        if (!cancelled) setStatus(null);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

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
        <p role="alert">
          この組み合わせは段階ゲート（統制違反 0 件・60 営業日・取引 100 件）を飛ばしています。
        </p>
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
              <td>{status.capital}</td>
            </tr>
            <tr>
              <th>1 注文あたり発注金額上限</th>
              <td>{status.maxOrderAmount}</td>
            </tr>
            <tr>
              <th>1 日あたり発注金額上限</th>
              <td>{status.maxDailyOrderAmount}</td>
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
