import { describe, it, expect } from 'vitest';
import { monitorChangeTypeLabel } from './contracts';

// FR-11, FR-13, IADR-0088, IADR-0090: 監視銘柄の数値 enum → ラベル写像。既知値の写像と未知値の安全側フォールバックを固定する。
describe('monitor contracts label mapping', () => {
  it('maps MonitorSettingsChangeType to labels (0=追加, 1=削除)', () => {
    expect(monitorChangeTypeLabel(0)).toBe('追加');
    expect(monitorChangeTypeLabel(1)).toBe('削除');
  });

  it('falls back to 不明(N) for unknown change types (画面を壊さない)', () => {
    expect(monitorChangeTypeLabel(99)).toBe('不明(99)');
    expect(monitorChangeTypeLabel(-1)).toBe('不明(-1)');
  });
});
