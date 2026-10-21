import { describe, expect, it } from 'vitest';
import {
  isPreCloseControlsView,
  isThemeConcentrationControlView,
  sameThemes,
  themeDraftErrors,
  themeLoosenings,
} from './contracts';
import {
  CONTRACT_PRE_CLOSE_CONTROLS,
  CONTRACT_RISK_SETTINGS,
  CONTRACT_THEME_CONCENTRATION,
  cloneContract,
} from '@ai-stock-trading/testing/riskContractFixtures';

// T-10-2963, SC-02, FR-10, ADR-0071 決定 2, #1407, IADR-0564: 画面側の判定（応答の形の検査・テーマの緩める変更・下書きの値域）。
describe('閉場前の統制とテーマの定義の画面側の判定（#1407）', () => {
  it('T-10-2963 契約フィクスチャの形は受理し、別の応答の形は拒む（既定値で埋めて描かない）', () => {
    expect(isPreCloseControlsView(cloneContract(CONTRACT_PRE_CLOSE_CONTROLS))).toBe(true);
    expect(isThemeConcentrationControlView(cloneContract(CONTRACT_THEME_CONCENTRATION))).toBe(true);
    expect(isPreCloseControlsView(cloneContract(CONTRACT_RISK_SETTINGS))).toBe(false);
    expect(isThemeConcentrationControlView(cloneContract(CONTRACT_RISK_SETTINGS))).toBe(false);
    expect(isPreCloseControlsView({ ...cloneContract(CONTRACT_PRE_CLOSE_CONTROLS), flattenMaxMinutes: '60' })).toBe(false);
  });

  it('T-10-2963 テーマの緩める変更を列挙する（外す・銘柄を外す・上限を上げる・市場を変える。名前の変更は外して足す）', () => {
    const current = [
      { name: '暗号資産', market: 1, symbols: ['COIN', 'MARA'], maxPositions: 1 },
      { name: 'AI・半導体', market: 1, symbols: ['NVDA', 'SMCI'], maxPositions: 1 },
    ];
    expect(themeLoosenings(current, [...current])).toEqual([]);
    expect(
      themeLoosenings(current, [
        { name: '暗号資産', market: 0, symbols: ['coin'], maxPositions: 2 },
        { name: 'AI半導体', market: 1, symbols: ['NVDA', 'SMCI'], maxPositions: 1 },
      ]),
    ).toEqual([
      'テーマ「暗号資産」の市場を米国から日本へ変える',
      'テーマ「暗号資産」から銘柄を外す（MARA）',
      'テーマ「暗号資産」の上限を 1 から 2 へ上げる',
      'テーマ「AI・半導体」を外す',
    ]);
    // 締める変更（銘柄を足す・上限を下げる・テーマを足す）は緩める変更に数えない。
    expect(
      themeLoosenings(current, [...current, { name: '大型テック', market: 1, symbols: ['AAPL'], maxPositions: 1 }]),
    ).toEqual([]);
  });

  it('T-10-2963 下書きの値域（名前の空・重複・銘柄なし・上限 1 未満）と、並び順・大文字小文字を区別しない比較', () => {
    expect(
      themeDraftErrors([
        { name: ' ', market: 1, symbols: ['COIN'], maxPositions: 1 },
        { name: 'A', market: 1, symbols: [], maxPositions: 0 },
        { name: 'A ', market: 1, symbols: ['X'], maxPositions: Number.NaN },
      ]),
    ).toEqual([
      '1 番目のテーマの名前を入力してください。',
      'テーマ「A」に銘柄を 1 つ以上追加してください。',
      'テーマ「A」の上限は 1 以上の整数で入力してください。',
      'テーマ「A」の名前が重複しています。',
      'テーマ「A」の上限は 1 以上の整数で入力してください。',
    ]);
    expect(
      sameThemes(
        [{ name: 'A', market: 1, symbols: ['COIN', 'MARA'], maxPositions: 1 }],
        [{ name: ' A', market: 1, symbols: ['mara', 'COIN'], maxPositions: 1 }],
      ),
    ).toBe(true);
  });
});
