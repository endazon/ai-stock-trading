import { describe, it, expect } from 'vitest';
import {
  COOLDOWN_MAX_HOURS,
  COOLDOWN_MIN_HOURS,
  hoursTextToTimeSpan,
  monitorChangeTypeLabel,
  MOVEMENT_THRESHOLD_MAX_PERCENT,
  MOVEMENT_THRESHOLD_MIN_PERCENT_EXCLUSIVE,
  timeSpanToHoursText,
  validateCooldownHours,
  validateMovementThresholdPercent,
} from './contracts';

// FR-11, FR-13, IADR-0088, IADR-0090: 監視銘柄の数値 enum → ラベル写像。既知値の写像と未知値の安全側フォールバックを固定する。
describe('monitor contracts label mapping', () => {
  it('maps MonitorSettingsChangeType to labels (0=追加, 1=削除)', () => {
    expect(monitorChangeTypeLabel(0)).toBe('追加');
    expect(monitorChangeTypeLabel(1)).toBe('削除');
  });

  it('maps the collection parameter change types (2=変動閾値, 3=クールダウン)', () => {
    expect(monitorChangeTypeLabel(2)).toBe('変動閾値');
    expect(monitorChangeTypeLabel(3)).toBe('クールダウン');
  });

  it('falls back to 不明(N) for unknown change types (画面を壊さない)', () => {
    expect(monitorChangeTypeLabel(99)).toBe('不明(99)');
    expect(monitorChangeTypeLabel(-1)).toBe('不明(-1)');
  });
});

// FR-03, FR-13, SC-02, #423, IADR-0164 決定2: 市場監視パラメータの値域（画面側の即時提示）。
//
// **実効はサーバ側（`MonitorSettingsBounds`）である。** ここは利用者への即時提示だが、
// **サーバと値が一致していなければならない**（片方だけ変えると、画面は通すのにサーバが 400 を返す／その逆）。
//
// 2026-08-07 の裁定（質問票 第 13 回 Q13）が値域とその理由を明記した。
//   変動閾値   … **0 超 0.50 以下**。0 以下では**あらゆる変動が閾値超過**となり LLM 費用が暴走する。
//   クールダウン … **0 以上 24 時間以下**。24 時間超では同一銘柄が 1 営業日に 1 度も再判定されない。
describe('SC-02 市場監視パラメータの値域（#423）', () => {
  it('サーバ側 MonitorSettingsBounds と同じ境界値を持つ', () => {
    expect(MOVEMENT_THRESHOLD_MIN_PERCENT_EXCLUSIVE).toBe(0);
    expect(MOVEMENT_THRESHOLD_MAX_PERCENT).toBe(50);
    expect(COOLDOWN_MIN_HOURS).toBe(0);
    expect(COOLDOWN_MAX_HOURS).toBe(24);
  });

  // ---- 変動閾値: 0 は不可 / 0 超は可 / 50 は可 / 50 超は不可 ----

  it.each(['0', '-0.01', '-1'])('変動閾値の 0 以下（%s）は受理しない', (text) => {
    expect(validateMovementThresholdPercent(text)).not.toBeNull();
  });

  it.each(['0.01', '1', '3', '49.99'])('変動閾値の 0 超（%s）は受理する', (text) => {
    expect(validateMovementThresholdPercent(text)).toBeNull();
  });

  it('変動閾値の 50 ちょうどは受理する（上限は含む）', () => {
    expect(validateMovementThresholdPercent('50')).toBeNull();
  });

  it.each(['50.01', '51', '100'])('変動閾値の 50 超（%s）は受理しない', (text) => {
    expect(validateMovementThresholdPercent(text)).not.toBeNull();
  });

  // **空欄・非数値を黙って 0 として扱わない**（0 は「すべての変動で発火する」危険側の設定である）。
  it.each(['', '   ', 'abc'])('変動閾値の空欄・非数値（%s）は受理しない', (text) => {
    expect(validateMovementThresholdPercent(text)).not.toBeNull();
  });

  // ---- クールダウン: 0 は可 / 24 は可 / 24 超は不可 / 負値は不可 ----

  it('クールダウンの 0 は受理する（抑制なしは正当な設定である）', () => {
    expect(validateCooldownHours('0')).toBeNull();
  });

  it.each(['0.25', '1', '12', '23.99'])('クールダウンの値域内（%s）は受理する', (text) => {
    expect(validateCooldownHours(text)).toBeNull();
  });

  it('クールダウンの 24 ちょうどは受理する（上限は含む）', () => {
    expect(validateCooldownHours('24')).toBeNull();
  });

  it.each(['24.01', '25', '48'])('クールダウンの 24 超（%s）は受理しない', (text) => {
    expect(validateCooldownHours(text)).not.toBeNull();
  });

  it.each(['-0.01', '-1'])('クールダウンの負値（%s）は受理しない', (text) => {
    expect(validateCooldownHours(text)).not.toBeNull();
  });

  it.each(['', '   ', 'abc'])('クールダウンの空欄・非数値（%s）は受理しない', (text) => {
    expect(validateCooldownHours(text)).not.toBeNull();
  });
});

// クールダウンの単位変換（画面＝時間／ワイヤ＝TimeSpan 文字列）。
// **単位の取り違えは統制設定で最も危険な誤りである**（15 分の設定が 15 時間になれば、
// 同一銘柄が 1 営業日に 1 度も再判定されない）。
describe('SC-02 クールダウンの単位変換（#423）', () => {
  it.each([
    ['0', '00:00:00'],
    ['0.25', '00:15:00'],
    ['1', '01:00:00'],
    ['1.5', '01:30:00'],
    ['24', '24:00:00'],
  ])('入力 %s 時間はワイヤの %s へ変換される', (text, expected) => {
    expect(hoursTextToTimeSpan(text)).toBe(expected);
  });

  // **読めない値は送らない。** 黙って "00:00:00"（抑制なし）へ倒すと、
  // 「入力し損ねた」ことが利用者にも履歴にも現れない。
  it.each(['', '   ', 'abc', '-1'])('読めない入力（%s）は null を返す（黙って 0 を送らない）', (text) => {
    expect(hoursTextToTimeSpan(text)).toBeNull();
  });

  it.each([
    ['00:00:00', '0'],
    ['00:15:00', '0.25'],
    ['01:00:00', '1'],
    ['01:30:00', '1.5'],
    ['1.00:00:00', '24'],
    ['00:15:00.0000000', '0.25'],
  ])('ワイヤの %s は入力 %s 時間へ変換される', (value, expected) => {
    expect(timeSpanToHoursText(value)).toBe(expected);
  });

  // 解釈できない値は空文字（**0 と誤解させない**）。
  it.each([null, undefined, '', 'not-a-timespan'])('解釈できない値（%s）は空文字を返す', (value) => {
    expect(timeSpanToHoursText(value)).toBe('');
  });

  // 往復（画面 → ワイヤ → 画面）で値が変わらない。
  it.each(['0', '0.25', '1', '1.5', '12', '24'])('往復で値が変わらない（%s 時間）', (text) => {
    expect(timeSpanToHoursText(hoursTextToTimeSpan(text)!)).toBe(text);
  });
});
