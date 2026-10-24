import { describe, expect, it } from 'vitest';
import { ApiError } from '@foundation/api/ApiError';
import {
  CONTRACT_STAGE1_MINIMUM_TRADE_COUNT_400,
  CONTRACT_STOP_LOSS_METHOD_400,
} from '@ai-stock-trading/testing/riskContractFixtures';
import { serverBadRequest } from '@ai-stock-trading/testing/serverErrors';
import { validationReasonsOf } from './validationReasons';

// SC-01, SC-02, NFR, #1465, IADR-0585: 400 の本文からサーバの理由を取り出す。期待値は**本番サーバの実応答**（契約フィクスチャ）で組む。
describe('validationReasonsOf（#1465）', () => {
  it('`{ error, details }` の本文からは details（拒否の理由の一覧）を返す', () => {
    expect(validationReasonsOf(serverBadRequest(CONTRACT_STOP_LOSS_METHOD_400))).toEqual(
      CONTRACT_STOP_LOSS_METHOD_400.details,
    );
    expect(CONTRACT_STOP_LOSS_METHOD_400.details?.[0]).toContain('S0（ブローカー側逆指値）以外にできません');
  });

  it('`{ error }` だけの本文からは error を返す', () => {
    expect(validationReasonsOf(serverBadRequest(CONTRACT_STAGE1_MINIMUM_TRADE_COUNT_400))).toEqual([
      CONTRACT_STAGE1_MINIMUM_TRADE_COUNT_400.error,
    ]);
  });

  it('RFC 7807 の本文（基盤の apiClient が details へ詰めたもの）はそのまま返す', () => {
    const e = new ApiError('validation', '入力内容に誤りがあります。', 400, ['理由は必須です'], { errors: { reason: ['理由は必須です'] } });
    expect(validationReasonsOf(e)).toEqual(['理由は必須です']);
  });

  it('本文が無い・文字列でない要素は描かない（形を推測しない）', () => {
    expect(validationReasonsOf(new ApiError('validation', 'x', 400, [], undefined))).toEqual([]);
    expect(validationReasonsOf(new ApiError('validation', 'x', 400, [], { details: [1, null, ' '], error: 2 }))).toEqual([]);
    expect(validationReasonsOf(new ApiError('validation', 'x', 400, [], { details: [], error: '要約' }))).toEqual(['要約']);
  });
});
