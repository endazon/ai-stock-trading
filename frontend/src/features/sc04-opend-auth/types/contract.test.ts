import { describe, expect, it } from 'vitest';
import stateJson from '@ai-stock-trading/testing/contract-fixtures/opend-auth.state.json';
import type { OpendAuthStateView } from './index';
import { canSubmitCode, CONNECTION_WAITING, PROMPT_PHONE } from './index';

// SC-04, FR-09, FR-11, #1413, IADR-0146, IADR-0321: **BFF の実 HTTP 応答（契約フィクスチャ）と型の突合。**
//
// `opend-auth.state.json` は、バックエンドの xUnit（`OpendAuthContractFixtureTests`）が BFF の
// `GET /bff/opend-auth/state` を**実際に叩いて得た応答そのもの**である（手書きしない）。
// 下の代入は、JSON のキー名・型が `OpendAuthStateView` を満たさなければ**コンパイルエラーになる**
// （`npm run typecheck` が赤）。仕組みは `testing/riskContractFixtures.ts` と同じである。
//
// 置き場を `testing/` ではなく本 feature にしたのは、`testing` 層から `features` を参照できないため
// である（ADR-0067 決定 5。型 `OpendAuthStateView` は本 feature の `types/` にある）。
const CONTRACT_OPEND_AUTH_STATE: OpendAuthStateView = stateJson;

describe('opend-auth/state の契約フィクスチャ（#1413）', () => {
  it('SMS 検証コード待ちの実応答であり、画面はそのまま入力を受け付ける', () => {
    expect(CONTRACT_OPEND_AUTH_STATE.connection).toBe(CONNECTION_WAITING);
    expect(CONTRACT_OPEND_AUTH_STATE.prompt).toBe(PROMPT_PHONE);
    expect(canSubmitCode(CONTRACT_OPEND_AUTH_STATE)).toBe(true);
  });
});
