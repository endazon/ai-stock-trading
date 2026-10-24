// 🔴 SC-01, FR-17, #1470, IADR-0585, IADR-0146: **ConfigurationService の実 HTTP 応答（契約フィクスチャ）。**
//
// `contract-fixtures/assumptions.400.json` は、バックエンドの xUnit（`AssumptionsContractFixtureTests`）が `PUT /assumptions` を
// 実際に叩いて得た 400 の本文そのものである（手書きしない）。型 `BadRequestBody` へ代入するので、本文の形が変わると
// ここでコンパイルエラーになる（`npm run typecheck` が赤）。仕組みは `riskContractFixtures.ts` と同じである。
import assumptions400Json from './contract-fixtures/assumptions.400.json';
import type { BadRequestBody } from '@ai-stock-trading/lib/validationReasons';

/** `PUT /assumptions` の 400 の実応答（理由の空欄。`{ error }` だけ）。 */
export const CONTRACT_ASSUMPTIONS_400: BadRequestBody = assumptions400Json;
