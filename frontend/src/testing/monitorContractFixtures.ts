// FR-03, FR-13, SC-01 §2, #340, IADR-0146 / IADR-0155: **バックエンドの実 HTTP 応答（契約フィクスチャ）。**
//
// `contract-fixtures/*.json` は、バックエンドの xUnit（`MonitorContractFixtureTests`）が
// `MonitorWorkerWebApplicationFactory` で**実エンドポイントを叩いて得た応答そのもの**である（手書きしない）。
//
// 本ファイルはその JSON を契約型へ代入する。TypeScript は JSON モジュールから構造を推論するため、
// **キー名・型が契約型を満たさなければここでコンパイルエラーになる**（`npm run typecheck` が赤）。
//
// これが機構の要点である（IADR-0146 決定2）:
//   - バックエンドがプロパティ名を変える → バックエンドの契約テストが赤
//   - フィクスチャを再生成してそれを緑に戻す → **本ファイルが赤**（フロントを直すまで通らない）
//
// テスト（コンポーネント / E2E）のモックは**必ず本ファイル経由で作る**。インラインの literal で
// モックを書くと、フロントが「こう返ってくるはずだ」と思っている形をフロント自身が検証する構造に戻る
// （それが #389 の根因である）。
import settingsJson from './contract-fixtures/monitor.settings.json';
import settingsHistoryJson from './contract-fixtures/monitor.settings-history.json';
import type { MarketMonitorSettings, MonitorSettingsChangeEntry } from '@ai-stock-trading/lib/monitor/contracts';

/** `GET /monitor/settings` の実応答（SC-01 §2 の収集パラメータ・SC-02 の監視銘柄）。 */
export const CONTRACT_MONITOR_SETTINGS: MarketMonitorSettings = settingsJson;

/** `GET /monitor/settings/history` の実応答（変動閾値の変更と監視銘柄の追加の 2 件）。 */
export const CONTRACT_MONITOR_SETTINGS_HISTORY: MonitorSettingsChangeEntry[] = settingsHistoryJson;
