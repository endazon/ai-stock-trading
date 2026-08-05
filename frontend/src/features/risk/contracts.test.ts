import { describe, it, expect } from 'vitest';
import {
  activeControlLabel,
  changeTypeLabel,
  criterionLabel,
  formatAt,
  brokerProviderLabel,
  BROKER_PROVIDER_INTERNAL_PAPER,
  BROKER_PROVIDER_MOOMOO_REAL,
  BROKER_PROVIDER_MOOMOO_SIMULATE,
  BROKER_PROVIDER_OPTIONS,
  isInternalPaper,
  isLiveProvider,
  LIVE_ACKNOWLEDGEMENT_PHRASE,
  marketLabel,
  MARKET_OPTIONS,
  productTypeLabel,
  PRODUCT_TYPE_MARGIN_LONG,
  PRODUCT_TYPE_SHORT_SELL,
  PRODUCT_TYPE_OPTIONS,
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
    // #333 / #334: 段階の呼称は計画（06_daytrading-review §4 表）に従う。「ペーパー」の語は単独で使わない。
    expect(stageLabel(1)).toBe('Stage 1（SIMULATE）');
    expect(stageLabel(2)).toBe('Stage 2（最小実弾）');
    expect(stageLabel(3)).toBe('Stage 3（段階増額）');
    expect(brokerProviderLabel(BROKER_PROVIDER_MOOMOO_REAL)).toContain('moomoo REAL');
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

describe('risk contracts — enum options for guard editing (#188/IADR-0086)', () => {
  it('derives product-type / market options from the known label tables', () => {
    // FR-19 / ADR-0016 決定1・#332: 商品種別は 3 値（現物 / 信用買い / 空売り）。
    expect(PRODUCT_TYPE_OPTIONS).toEqual([
      { value: 0, label: '現物' },
      { value: 1, label: '信用買い' },
      { value: 2, label: '空売り' },
    ]);
    expect(MARKET_OPTIONS).toEqual([
      { value: 0, label: '日本' },
      { value: 1, label: '米国' },
    ]);
    // 危険判定に使う信用買い・空売りの値がラベルと整合する。
    expect(productTypeLabel(PRODUCT_TYPE_MARGIN_LONG)).toBe('信用買い');
    expect(productTypeLabel(PRODUCT_TYPE_SHORT_SELL)).toBe('空売り');
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

// FR-20, FR-12, INDEX 決定 46, #334: 発注先（Broker Provider）の 3 値と序数、実弾判定・paper 判定。
describe('risk contracts — broker provider (2 軸分離)', () => {
  it('序数はバックエンド BrokerProvider と一致する（旧 TradeMode の Paper=0 / Live=1 を保存）', () => {
    expect(BROKER_PROVIDER_INTERNAL_PAPER).toBe(0);
    expect(BROKER_PROVIDER_MOOMOO_REAL).toBe(1);
    expect(BROKER_PROVIDER_MOOMOO_SIMULATE).toBe(2);
  });

  it('選択肢は計画の 3 値だけを出す', () => {
    expect(BROKER_PROVIDER_OPTIONS.map((o) => o.value)).toEqual([0, 1, 2]);
  });

  it('ラベルは用語の分離を守る（SIMULATE を「ペーパー」と呼ばない・paper を「デモ取引」と呼ばない）', () => {
    expect(brokerProviderLabel(BROKER_PROVIDER_MOOMOO_SIMULATE)).not.toContain('ペーパー');
    expect(brokerProviderLabel(BROKER_PROVIDER_INTERNAL_PAPER)).not.toContain('デモ');
    expect(brokerProviderLabel(BROKER_PROVIDER_INTERNAL_PAPER)).not.toContain('SIMULATE');
  });

  it('実弾判定は moomoo REAL だけを真とする（否定形）', () => {
    expect(isLiveProvider(BROKER_PROVIDER_MOOMOO_REAL)).toBe(true);
    expect(isLiveProvider(BROKER_PROVIDER_INTERNAL_PAPER)).toBe(false);
    expect(isLiveProvider(BROKER_PROVIDER_MOOMOO_SIMULATE)).toBe(false);
    expect(isLiveProvider(99)).toBe(false);
  });

  it('内蔵 paper 判定は 0 だけを真とし、未知値・欠損は偽（バナーを誤って出さない）', () => {
    expect(isInternalPaper(BROKER_PROVIDER_INTERNAL_PAPER)).toBe(true);
    expect(isInternalPaper(BROKER_PROVIDER_MOOMOO_SIMULATE)).toBe(false);
    expect(isInternalPaper(null)).toBe(false);
    expect(isInternalPaper(undefined)).toBe(false);
    expect(isInternalPaper(99)).toBe(false);
  });

  it('確認文字列はサーバの BrokerProviderChange.LiveAcknowledgementPhrase と同じ値である', () => {
    expect(LIVE_ACKNOWLEDGEMENT_PHRASE).toBe('REAL');
  });

  it('発注先の変更履歴は種別 7 で絞れる（SettingsChangeType.BrokerProviderChanged）', () => {
    expect(changeTypeLabel(7)).toBe('発注先');
  });
});
