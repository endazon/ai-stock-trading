import { describe, expect, it } from 'vitest';
import {
  CONTRACT_RISK_SETTINGS,
  CONTRACT_RISK_STATUS,
  CONTRACT_SETTINGS_HISTORY,
  CONTRACT_STAGE_GATE,
} from './contractFixtures';
import type { RiskLimitSettings, StageSettings } from './contracts';
import { criterionLabel, withdrawalReasonLabel } from './contracts';

// FR-10, FR-20, SC-02, SC-03, #389, IADR-0146: **バックエンドの実応答に対する契約テスト。**
//
// ここで検証しているのは「フロントが自分で書いたモック」ではなく、バックエンドの xUnit が
// 実エンドポイントから採取した応答そのもの（`contract-fixtures/*.json`）である。
// 型の突合はコンパイル時（`contractFixtures.ts` の代入）が担うため、本ファイルは
//   - 画面が実際に読む値が **`undefined` にならない**こと（#389 の症状そのもの）
//   - 数値 enum → ラベル写像が実応答の値域を**取りこぼしていない**こと
//   - 旧い形（金額キー）が契約型を**満たさない**こと（`@ts-expect-error` によるコンパイル時の否定形）
// を固定する。
describe('リスク契約フィクスチャ（実応答）', () => {
  // ---- 正の確認: #389 の 3 キーが読める ----

  it('リスク上限は equity 比のキーで読め、計画（05_trading-assumptions §5）の既定値と一致する', () => {
    // FR-10, IADR-0130: 1 注文 25% / 1 日 150%。旧キーなら undefined になる（それが #389 の症状）。
    expect(CONTRACT_RISK_SETTINGS.limits.maxOrderAmountRatio).toBe(0.25);
    expect(CONTRACT_RISK_SETTINGS.limits.maxDailyOrderAmountRatio).toBe(1.5);
    // 比率であることの確認: 金額（数万〜数十万）ではあり得ない値域に収まる。
    expect(CONTRACT_RISK_SETTINGS.limits.maxOrderAmountRatio).toBeLessThan(1);
  });

  it('段階の発注可能額は総資金比のキーで読める', () => {
    // FR-20, IADR-0136: capitalCap（金額）ではなく capitalCapRatio（比率）。
    expect(CONTRACT_RISK_SETTINGS.stage.capitalCapRatio).toBeTypeOf('number');
    expect(CONTRACT_STAGE_GATE.currentSettings.capitalCapRatio).toBeTypeOf('number');
    expect(CONTRACT_RISK_SETTINGS.stage.capitalCapRatio).toBeLessThanOrEqual(1);
  });

  it('統制状態の上限は equity から解決済みの実額であり、設定の比率とは別物である', () => {
    // #389 で**改名してはいけない**側。SC-03 の上限表示・使用率表示・実弾切替モーダル③が使う。
    expect(CONTRACT_RISK_STATUS.maxOrderAmount).toBeGreaterThan(1);
    expect(CONTRACT_RISK_STATUS.maxOrderAmount).toBeCloseTo(
      CONTRACT_RISK_STATUS.capital * CONTRACT_RISK_SETTINGS.limits.maxOrderAmountRatio,
      5,
    );
    expect(CONTRACT_RISK_STATUS.maxDailyOrderAmount).toBeCloseTo(
      CONTRACT_RISK_STATUS.capital * CONTRACT_RISK_SETTINGS.limits.maxDailyOrderAmountRatio,
      5,
    );
  });

  it('画面が描画するキーがすべて実応答に存在する（undefined を描かない）', () => {
    // SC-03 の統制状態・段階ゲート・履歴のうち、画面が値として出す項目を実応答で確認する。
    for (const value of [
      CONTRACT_RISK_STATUS.activeControl,
      CONTRACT_RISK_STATUS.stage,
      CONTRACT_RISK_STATUS.brokerProvider,
      CONTRACT_RISK_STATUS.dailyOrderedAmount,
      CONTRACT_RISK_STATUS.maxDailyOrderAmount,
      CONTRACT_RISK_STATUS.drawdownRatio,
      CONTRACT_RISK_STATUS.maxDrawdownRatio,
      CONTRACT_RISK_STATUS.openPositionCount,
      CONTRACT_RISK_STATUS.maxOpenPositions,
      CONTRACT_STAGE_GATE.currentStage,
      CONTRACT_STAGE_GATE.currentSettings.mode,
      CONTRACT_STAGE_GATE.stage1Progress.qualifiedTradingDays,
      CONTRACT_STAGE_GATE.stage1Criteria.targetTradingDays,
    ]) {
      expect(value).toBeTypeOf('number');
    }
    expect(CONTRACT_STAGE_GATE.history.length).toBeGreaterThan(0);
    expect(CONTRACT_SETTINGS_HISTORY.length).toBeGreaterThan(0);
  });

  it('実応答に現れる数値 enum がラベル写像に存在する（不明(N) を出さない）', () => {
    // #389 実測: 既定応答の unmetCriteria は [9, 10] だが写像は 0〜8 しか無く、SC-03 は「不明(9)」を出していた。
    // ラベル写像の取りこぼしは型では捕まらないため、実応答の値域で押さえる。
    for (const criterion of CONTRACT_STAGE_GATE.promotion.unmetCriteria) {
      expect(criterionLabel(criterion)).not.toContain('不明');
    }
  });

  // ---- 否定形（写像・型が「効かない」方向に壊れていないこと） ----

  it('未知の enum 値は安全側フォールバックへ倒れる（写像を拡げすぎていない）', () => {
    // ラベル写像を「何でも返す」形に壊すと、上の正の確認が常に緑になり検査が死ぬ。
    expect(criterionLabel(9999)).toBe('不明(9999)');
    // WithdrawalReason の 1 は**欠番**（#333 で撤退事由から外され再利用しない）。誤ったラベルを出さない。
    expect(withdrawalReasonLabel(1)).toBe('不明(1)');
  });

  it('旧い金額キーの形は契約型を満たさない（コンパイル時の否定形）', () => {
    // #389 の退行そのもの。**この代入が通ってしまうなら @ts-expect-error が「エラー無し」として
    // `npm run typecheck` を落とす**——検査が効かなくなったことをコンパイラが教える。
    // 形だけを先に作る（`@ts-expect-error` は**直後の 1 行**にしか効かないため、代入の行で受ける）。
    const legacyLimitsShape = {
      maxOrderAmount: 100000,
      maxDailyOrderAmount: 300000,
      maxOpenPositions: 3,
      dailyLossLimitRatio: 0.02,
      perTradeRiskRatio: 0.01,
      maxDrawdownRatio: 0.1,
      losingStreakThreshold: 5,
      losingStreakSizeFactor: 0.5,
    };
    // @ts-expect-error 旧 API の形（金額キー）は equity 比の契約型を満たさない
    const legacyLimits: RiskLimitSettings = legacyLimitsShape;

    const legacyStageShape = { stage: 2, mode: 1, capitalCap: 1000000 };
    // @ts-expect-error 旧 API の形（capitalCap＝金額）は総資金比の契約型を満たさない
    const legacyStage: StageSettings = legacyStageShape;

    const retypedShape = { ...CONTRACT_RISK_SETTINGS.limits, maxOrderAmountRatio: '0.25' };
    // @ts-expect-error 比率キーに文字列は入らない（型変更も検出する）
    const retypedLimits: RiskLimitSettings = retypedShape;

    // 実行時には「旧キーでは新しいキーが読めない」ことだけを確かめる（#389 の症状）。
    expect((legacyLimits as unknown as Record<string, unknown>).maxOrderAmountRatio).toBeUndefined();
    expect((legacyStage as unknown as Record<string, unknown>).capitalCapRatio).toBeUndefined();
    expect(retypedLimits.maxOrderAmountRatio).toBe('0.25');
  });
});
