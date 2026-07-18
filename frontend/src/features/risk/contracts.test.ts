import { describe, it, expect } from 'vitest';
import {
  activeControlLabel,
  changeTypeLabel,
  criterionLabel,
  formatAt,
  marketLabel,
  modeLabel,
  productTypeLabel,
  ratioPercent,
  stageLabel,
  transitionKindLabel,
  withdrawalReasonLabel,
} from './contracts';

// IADR-0084 決定 4, SC-02/SC-03 受け入れ基準 5: 数値 enum → 表示ラベルの写像と、未知値の安全側フォールバック、
// 使用率の 0 除算回避を機械テストで固定する（バックエンド enum 追加や欠損値で退行しても検出できるようにする）。

describe('risk contracts — enum label mapping (fail-safe fallback)', () => {
  it('maps known enum values to labels', () => {
    expect(stageLabel(0)).toBe('Stage 0（検証）');
    expect(stageLabel(3)).toBe('Stage 3（拡大実弾）');
    expect(modeLabel(1)).toBe('実弾');
    expect(activeControlLabel(1)).toBe('緊急停止（kill switch）');
    expect(transitionKindLabel(1)).toBe('差し戻し');
    expect(criterionLabel(0)).toBe('バックテスト未合格');
    expect(withdrawalReasonLabel(0)).toBe('実DD がバックテスト最大DD × 倍率に到達');
    expect(changeTypeLabel(1)).toBe('上限');
    expect(productTypeLabel(0)).toBe('現物');
    expect(marketLabel(1)).toBe('米国');
  });

  it('falls back to 不明(N) for values not in the table (fail-safe)', () => {
    // バックエンド enum に将来値が増えても画面を壊さず安全側に倒す。
    expect(stageLabel(99)).toBe('不明(99)');
    expect(activeControlLabel(7)).toBe('不明(7)');
    expect(transitionKindLabel(5)).toBe('不明(5)');
    expect(criterionLabel(42)).toBe('不明(42)');
    expect(changeTypeLabel(-1)).toBe('不明(-1)');
    expect(withdrawalReasonLabel(9)).toBe('不明(9)');
  });
});

describe('risk contracts — ratioPercent (divide-by-zero safe)', () => {
  it('computes a percentage for a positive denominator', () => {
    expect(ratioPercent(150000, 300000)).toBe('50.0%');
    expect(ratioPercent(2, 5)).toBe('40.0%');
  });

  it('returns — for a zero or non-finite denominator (no divide-by-zero)', () => {
    expect(ratioPercent(5, 0)).toBe('—');
    expect(ratioPercent(Number.NaN, 10)).toBe('—');
    expect(ratioPercent(10, Number.POSITIVE_INFINITY)).toBe('—');
  });
});

describe('risk contracts — formatAt (degrades safely)', () => {
  it('returns — for null/undefined/empty', () => {
    expect(formatAt(null)).toBe('—');
    expect(formatAt(undefined)).toBe('—');
    expect(formatAt('')).toBe('—');
  });

  it('returns the raw string for an unparseable value (does not throw)', () => {
    expect(formatAt('not-a-date')).toBe('not-a-date');
  });
});
