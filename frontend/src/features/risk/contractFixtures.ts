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
import type {
  RiskManagementSettings,
  RiskStatusView,
  SettingsChangeEntry,
  StageGateStatus,
} from './contracts';

// ---- 契約型への代入（＝コンパイル時の突合。ここが赤くなることが本機構の第一の関門） ----

/** `GET /risk-controls/settings` の実応答（SC-02 のリスク上限・ガード・段階・発注先）。 */
export const CONTRACT_RISK_SETTINGS: RiskManagementSettings = settingsJson;

/** `GET /risk-controls/status` の実応答（SC-03 の統制状態。上限は equity から解決済みの**実額**）。 */
export const CONTRACT_RISK_STATUS: RiskStatusView = statusJson;

/** `GET /risk-controls/stage-gate` の実応答（SC-03 の段階ゲート現況。遷移履歴を 1 件含む）。 */
export const CONTRACT_STAGE_GATE: StageGateStatus = stageGateJson;

/** `GET /risk-controls/settings/history` の実応答（上限変更と発注先変更の 2 件）。 */
export const CONTRACT_SETTINGS_HISTORY: SettingsChangeEntry[] = settingsHistoryJson;

// ---- テスト用のヘルパ ----

// フィクスチャは全テストで共有されるため、複製してから渡す（あるテストの上書きが他へ漏れない）。
// JSON 由来の純データのみを扱うため往復複製で十分である。
export function cloneContract<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}
