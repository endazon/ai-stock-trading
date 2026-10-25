// FR-10, FR-20, SC-02, SC-03, #389, IADR-0146: **バックエンドの実 HTTP 応答（契約フィクスチャ）**。
//
// `contract-fixtures/*.json` は、バックエンドの xUnit（`FrontendContractFixtureTests`）が
// `RiskWorkerWebApplicationFactory` で**実エンドポイントを叩いて得た応答そのもの**である（手書きしない）。
//
// 本ファイルはその JSON を契約型へ代入する。TypeScript は JSON モジュールから構造を推論するため、
// **キー名・型が契約型を満たさなければここでコンパイルエラーになる**（`npm run typecheck` が赤）。
//
// これが機構の要点である（IADR-0146 決定2）:
//   - バックエンドがプロパティ名を変える → バックエンドの契約テストが赤
//   - フィクスチャを再生成してそれを緑に戻す → **本ファイルが赤**（フロントを直すまで通らない）
// 片側だけ直しても緑にならないため、#329 / #333 のように「両方緑で本番だけ壊れる」状態が作れない。
//
// テスト（コンポーネント / E2E）のモックは**必ず本ファイル経由で作る**。インラインの literal でモックを
// 書くと、フロントが「こう返ってくるはずだ」と思っている形を、フロント自身が検証する構造に戻る
// （それが #389 の根因である）。
import settingsJson from './contract-fixtures/risk-controls.settings.json';
import statusJson from './contract-fixtures/risk-controls.status.json';
import stageGateJson from './contract-fixtures/risk-controls.stage-gate.json';
import settingsHistoryJson from './contract-fixtures/risk-controls.settings-history.json';
import shortSellingJson from './contract-fixtures/risk-controls.short-selling.json';
import preCloseJson from './contract-fixtures/risk-controls.settings-pre-close.json';
import themeConcentrationJson from './contract-fixtures/risk-controls.settings-theme-concentration.json';
import stopLossMethod400Json from './contract-fixtures/risk-controls.settings-stop-loss-method.400.json';
import stage1MinimumTradeCount400Json from './contract-fixtures/risk-controls.settings-stage1-minimum-trade-count.400.json';
import type { BadRequestBody } from '@ai-stock-trading/lib/validationReasons';
import type {
  PreCloseControlsView,
  RiskManagementSettings,
  RiskStatusView,
  SettingsChangeEntry,
  ShortSellingStatusView,
  StageGateStatus,
  ThemeConcentrationControlView,
} from '@ai-stock-trading/lib/risk/contracts';

// ---- 契約型への代入（＝コンパイル時の突合。ここが赤くなることが本機構の第一の関門） ----

/** `GET /risk-controls/settings` の実応答（SC-02 のリスク上限・ガード・段階・発注先）。 */
export const CONTRACT_RISK_SETTINGS: RiskManagementSettings = settingsJson;

/** `GET /risk-controls/status` の実応答（SC-03 の統制状態。上限は equity から解決済みの**実額**）。 */
export const CONTRACT_RISK_STATUS: RiskStatusView = statusJson;

/** `GET /risk-controls/stage-gate` の実応答（SC-03 の段階ゲート現況。遷移履歴を 1 件含む）。 */
export const CONTRACT_STAGE_GATE: StageGateStatus = stageGateJson;

/** `GET /risk-controls/settings/history` の実応答（上限変更と発注先変更の 2 件）。 */
export const CONTRACT_SETTINGS_HISTORY: SettingsChangeEntry[] = settingsHistoryJson;

/**
 * `GET /risk-controls/short-selling` の実応答（SC-03 の維持率・空売りの現況。#340・IADR-0154）。
 *
 * **既定構成では維持率・借株料の累計・自動縮小の発動履歴・強制買戻しの発生回数がいずれも
 * `NotSupplied`（＝1）である。** これは不具合ではなく、供給元（ブローカー照会）が未実装であるという
 * 事実そのものである（維持率は **Stage 1 の全期間にわたって表示できない**。ADR-0016 決定7 の 2026-08-07 追記）。
 * 画面テストはこの形を土台にし、「未供給を未供給として描く」ことを検証する。
 */
export const CONTRACT_SHORT_SELLING: ShortSellingStatusView = shortSellingJson;

/** `GET /risk-controls/settings/pre-close` の実応答（構成と違う値を 1 回保存した後。保存値・食い違いの形を含む。#1407・IADR-0564）。 */
export const CONTRACT_PRE_CLOSE_CONTROLS: PreCloseControlsView = preCloseJson;

/** `GET /risk-controls/settings/theme-concentration` の実応答（定義を 1 回保存した後。#1407・IADR-0564）。 */
export const CONTRACT_THEME_CONCENTRATION: ThemeConcentrationControlView = themeConcentrationJson;

// ---- テスト用のヘルパ ----

// 🔴 #1465, IADR-0585: **400 の本文も実応答で縛る。** 単体試験のモックが本番と違う形（`ApiError.details` に理由を直接詰める）
// だったため、画面が本番の本文から理由を読まないことを、両側とも緑のまま見逃していた。

/** `PUT /risk-controls/settings/stop-loss-method` の 400 の実応答（実弾の間に S1 を選ぶ。`{ error, details }`）。 */
export const CONTRACT_STOP_LOSS_METHOD_400: BadRequestBody = stopLossMethod400Json;

/** `PUT /risk-controls/settings/stage1-minimum-trade-count` の 400 の実応答（件数の省略。`{ error }` だけ）。 */
export const CONTRACT_STAGE1_MINIMUM_TRADE_COUNT_400: BadRequestBody = stage1MinimumTradeCount400Json;

// フィクスチャは全テストで共有されるため、複製してから渡す（あるテストの上書きが他へ漏れない）。
// JSON 由来の純データのみを扱うため往復複製で十分である。
export function cloneContract<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}
