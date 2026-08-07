import { describe, it, expect } from 'vitest';
import {
  activeControlLabel,
  changeTypeLabel,
  CHANGE_TYPE_STAGE1_MINIMUM_TRADE_COUNT,
  criterionLabel,
  inputBelowStatisticalBasis,
  STAGE1_TRADE_COUNT_DEFAULT,
  STAGE1_TRADE_COUNT_MAX,
  STAGE1_TRADE_COUNT_MIN,
  validateStage1TradeCountInput,
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
  describeLimitRange,
  formatAmount,
  isEquityRatioField,
  LIMIT_FIELDS,
  LIMIT_FIELD_KEYS,
  limitInputToWire,
  percentTextToRatio,
  ratioToPercentText,
  resolveEquityAmount,
  validateLimitInput,
  wireToLimitInput,
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

// FR-10, SC-02, #362, IADR-0151: 比率 ⇄ 百分率の変換、値域、実額の解決。
//
// **画面（百分率）とワイヤ（比率）で単位が違う。** 取り違えは統制で最も危険な誤りであるため
// （IADR-0130 決定1）、変換は本ファイルが検査する 2 関数だけを通す。値域の実効はサーバ側
// （`RiskLimitBounds`）であり、ここは利用者への即時提示が正しく効くことを固定する。
describe('リスク上限の単位変換（#362 / IADR-0151 決定1）', () => {
  it('比率を百分率の表示文字列にする', () => {
    expect(ratioToPercentText(0.25)).toBe('25');
    expect(ratioToPercentText(1.5)).toBe('150');
    expect(ratioToPercentText(0.02)).toBe('2');
    expect(ratioToPercentText(0.001)).toBe('0.1');
    expect(ratioToPercentText(0)).toBe('0');
  });

  it('百分率の入力文字列を比率にする', () => {
    expect(percentTextToRatio('25')).toBe(0.25);
    expect(percentTextToRatio('150')).toBe(1.5);
    expect(percentTextToRatio('2')).toBe(0.02);
    expect(percentTextToRatio('0.1')).toBe(0.001);
    expect(percentTextToRatio(' 33.3 ')).toBe(0.333);
    expect(percentTextToRatio('-5')).toBe(-0.05);
  });

  // 否定形: 読めない入力は null（**黙って 0 にしない**）。0 は「発注できない」であって未入力ではない。
  it('読めない入力は null（0 へ倒さない）', () => {
    expect(percentTextToRatio('')).toBeNull();
    expect(percentTextToRatio('   ')).toBeNull();
    expect(percentTextToRatio('abc')).toBeNull();
    expect(ratioToPercentText(null)).toBe('');
    expect(ratioToPercentText(undefined)).toBe('');
    expect(ratioToPercentText(Number.NaN)).toBe('');
  });

  // プロパティベース: 比率 → 百分率 → 比率 の往復で値が変わらない。
  // 変換に丸め誤差が入れば（`/100` 実装なら入り得る）ここが落ちる。
  it('往復で値が変わらない（丸め誤差を統制値へ紛れ込ませない）', () => {
    for (const ratio of [0.25, 1.5, 0.02, 0.01, 0.1, 0.5, 0.333, 0.0001, 3, 10]) {
      expect(percentTextToRatio(ratioToPercentText(ratio))).toBe(ratio);
    }
  });

  it('小数点移動は浮動小数点の除算と違い誤差を出さない', () => {
    // `2.675 / 100` は 0.026749999999999996 になる。文字列の小数点移動なら 0.02675 のままである。
    expect(percentTextToRatio('2.675')).toBe(0.02675);
    expect(percentTextToRatio('8.7')).toBe(0.087);
  });
});

describe('リスク上限の値域（#362 / IADR-0151 決定2）', () => {
  // 境界値テーブル: 下限直下・下限・既定・上限一致・上限直上。
  // **サーバ側 `RiskLimitBoundsTests` の同じ表と一致していなければならない**（片方だけ変えると、
  // 画面は通すのにサーバが 400 を返す／その逆になる）。
  it.each([
    ['maxOrderAmountRatio', '0', false],
    ['maxOrderAmountRatio', '0.01', true],
    ['maxOrderAmountRatio', '25', true],
    ['maxOrderAmountRatio', '100', true],
    ['maxOrderAmountRatio', '100.1', false],
    ['maxOrderAmountRatio', '3500000', false],
    ['maxDailyOrderAmountRatio', '0', false],
    ['maxDailyOrderAmountRatio', '150', true],
    ['maxDailyOrderAmountRatio', '1000', true],
    ['maxDailyOrderAmountRatio', '1000.1', false],
    ['maxOpenPositions', '0', false],
    ['maxOpenPositions', '1', true],
    ['maxOpenPositions', '3', true],
    ['maxOpenPositions', '20', true],
    ['maxOpenPositions', '21', false],
    ['maxOpenPositions', '3.5', false],
    ['dailyLossLimitRatio', '0', false],
    ['dailyLossLimitRatio', '2', true],
    ['dailyLossLimitRatio', '20', true],
    ['dailyLossLimitRatio', '20.1', false],
    ['perTradeRiskRatio', '1', true],
    ['perTradeRiskRatio', '10', true],
    ['perTradeRiskRatio', '10.1', false],
    ['maxDrawdownRatio', '10', true],
    ['maxDrawdownRatio', '50', true],
    ['maxDrawdownRatio', '50.1', false],
    ['losingStreakThreshold', '0', false],
    ['losingStreakThreshold', '5', true],
    ['losingStreakThreshold', '20', true],
    ['losingStreakThreshold', '21', false],
    // 1.0 は「縮小しない」＝連敗時縮小の無効化であるため上限に含めない。
    ['losingStreakSizeFactor', '0', false],
    ['losingStreakSizeFactor', '0.5', true],
    ['losingStreakSizeFactor', '0.99', true],
    ['losingStreakSizeFactor', '1', false],
    ['losingStreakSizeFactor', '1.5', false],
  ] as const)('%s に %s は %s', (key, text, accepted) => {
    expect(validateLimitInput(key, text) === null).toBe(accepted);
  });

  // 否定形: 空欄・非数値も無効（黙って 0 送信しない）。
  it('空欄・非数値は無効', () => {
    for (const key of LIMIT_FIELD_KEYS) {
      expect(validateLimitInput(key, '')).not.toBeNull();
      expect(validateLimitInput(key, 'abc')).not.toBeNull();
    }
  });

  // プロパティベース: 8 項目すべてに範囲の説明があり、既定値（計画 §5）は全項目で受理される。
  it('計画の既定値は全項目で受理される', () => {
    const defaults: Record<string, string> = {
      maxOrderAmountRatio: '25',
      maxDailyOrderAmountRatio: '150',
      maxOpenPositions: '3',
      dailyLossLimitRatio: '2',
      perTradeRiskRatio: '1',
      maxDrawdownRatio: '10',
      losingStreakThreshold: '5',
      losingStreakSizeFactor: '0.5',
    };
    for (const key of LIMIT_FIELD_KEYS) {
      expect(validateLimitInput(key, defaults[key])).toBeNull();
      expect(describeLimitRange(key)).toContain(LIMIT_FIELDS[key].unit);
    }
  });

  it('equity 比の項目だけが実額の併記対象である', () => {
    expect(LIMIT_FIELD_KEYS.filter(isEquityRatioField)).toEqual([
      'maxOrderAmountRatio',
      'maxDailyOrderAmountRatio',
      'dailyLossLimitRatio',
      'perTradeRiskRatio',
      'maxDrawdownRatio',
    ]);
  });

  it('ラベルに計画が禁じた語（保有銘柄数上限）を用いない', () => {
    // ADR-0016 決定9・計画 §5: 同一銘柄で複数の建玉を持ち得るため銘柄数では数えない。
    for (const key of LIMIT_FIELD_KEYS) {
      expect(LIMIT_FIELDS[key].label).not.toContain('保有銘柄数');
    }
  });

  it('画面単位とワイヤ単位の変換は項目の種別で切り替わる', () => {
    // equity 比の項目だけが 100 倍の関係にある。件数・倍率はそのまま。
    expect(limitInputToWire('maxOrderAmountRatio', '25')).toBe(0.25);
    expect(limitInputToWire('maxOpenPositions', '3')).toBe(3);
    expect(limitInputToWire('losingStreakSizeFactor', '0.5')).toBe(0.5);
    expect(wireToLimitInput('maxOrderAmountRatio', 0.25)).toBe('25');
    expect(wireToLimitInput('maxOpenPositions', 3)).toBe('3');
    expect(wireToLimitInput('losingStreakSizeFactor', 0.5)).toBe('0.5');
  });
});

describe('実額の解決（#362 / IADR-0151 決定4 / #364・IADR-0152 決定6）', () => {
  it('equity × 比率 を返す', () => {
    // #364: equity は基準通貨（USD）建てである（計画 §5 の確定値 $3,000）。
    expect(resolveEquityAmount(3000, 0.25)).toBe(750);
    expect(resolveEquityAmount(3000, 1.5)).toBe(4500);
  });

  // 否定形: equity または比率が不明なら null（画面は「—」を出す）。**0 を返さない**——
  // $0 という実額を自信満々に表示すると、equity が取れていない事実が隠れる。
  it('equity・比率が不明なら null（0 を返さない）', () => {
    expect(resolveEquityAmount(null, 0.25)).toBeNull();
    expect(resolveEquityAmount(undefined, 0.25)).toBeNull();
    expect(resolveEquityAmount(Number.NaN, 0.25)).toBeNull();
    expect(resolveEquityAmount(3000, null)).toBeNull();
    expect(resolveEquityAmount(3000, Number.NaN)).toBeNull();
  });

  // ---- FR-20, SC-02, #423, IADR-0164 決定5/決定6: Stage 1 の最小取引件数の値域と警告 ----
  //
  // **実効はサーバ側（`Stage1TradeCountBounds`）である。** ここは利用者への即時提示だが、
  // **サーバと値が一致していなければならない**（片方だけ変えると、画面は通すのにサーバが 400 を返す／その逆）。

  it('Stage 1 最小取引件数の値域はサーバ側 Stage1TradeCountBounds と一致する', () => {
    expect(STAGE1_TRADE_COUNT_DEFAULT).toBe(100);
    expect(STAGE1_TRADE_COUNT_MIN).toBe(1);
    expect(STAGE1_TRADE_COUNT_MAX).toBe(1000);
  });

  it.each(['1', '2', '99', '100', '999', '1000'])(
    'Stage 1 最小取引件数の値域内（%s）は受理する', (text) => {
      expect(validateStage1TradeCountInput(text)).toBeNull();
    });

  // 0 以下では条件 3 が無条件に成立し、**期間だけで昇格できてしまう**。
  it.each(['0', '-1'])('Stage 1 最小取引件数の 0 以下（%s）は受理しない', (text) => {
    expect(validateStage1TradeCountInput(text)).not.toBeNull();
  });

  it.each(['1001', '10000'])('Stage 1 最小取引件数の 1000 超（%s）は受理しない', (text) => {
    expect(validateStage1TradeCountInput(text)).not.toBeNull();
  });

  // 空欄・非数値・小数を黙って通さない（件数は整数である）。
  it.each(['', '   ', 'abc', '10.5'])(
    'Stage 1 最小取引件数の空欄・非数値・小数（%s）は受理しない', (text) => {
      expect(validateStage1TradeCountInput(text)).not.toBeNull();
    });

  // **警告の対象（100 未満）であっても受理する。** 裁定は「警告は設定を妨げない」と定めている。
  it.each(['1', '30', '99'])('統計的根拠を下回る件数（%s）でも入力としては受理する', (text) => {
    expect(inputBelowStatisticalBasis(text)).toBe(true);
    expect(validateStage1TradeCountInput(text)).toBeNull();
  });

  // 逆方向の否定形。満たしているのに警告を出すと、警告が常時出て誰も読まなくなる。
  it.each(['100', '101', '1000'])('統計的根拠を満たす件数（%s）では警告しない', (text) => {
    expect(inputBelowStatisticalBasis(text)).toBe(false);
  });

  // 読めない入力を「警告あり」に倒さない（値域エラーの方が先に出る）。
  it.each(['', 'abc', '0', '-5'])('読めない入力（%s）は統計的根拠の警告に倒さない', (text) => {
    expect(inputBelowStatisticalBasis(text)).toBe(false);
  });

  it('Stage 1 最小取引件数の変更履歴ラベルが写像されている（序数 8・末尾追加）', () => {
    expect(CHANGE_TYPE_STAGE1_MINIMUM_TRADE_COUNT).toBe(8);
    expect(changeTypeLabel(CHANGE_TYPE_STAGE1_MINIMUM_TRADE_COUNT)).toBe('Stage 1 最小取引件数');
  });

  it('金額の整形は基準通貨（USD）の記号を付ける', () => {
    // #364, IADR-0152 決定6: `capital` は基準通貨（USD）建てになったため、`$` を付けることが正しい表示である
    // （計画 SC-02 の表記例「25%（$750）」と一致する。#409 は本移行で解消）。
    expect(formatAmount(750)).toBe('$750');
    expect(formatAmount(4500)).toBe('$4,500');
    // セントまで表示する（USD は補助単位を持つ）。
    expect(formatAmount(1234.56)).toBe('$1,234.56');
    expect(formatAmount(null)).toBe('—');
    expect(formatAmount(Number.NaN)).toBe('—');
  });
});
