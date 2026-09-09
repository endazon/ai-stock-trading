import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-04, FR-09, FR-11, UC-06, NFR-06, IADR-0009/0035/0321, planning#594:
// OpenD 認証操作画面のアクセス制御（利用者 trading-owner 限定＋存在秘匿）。
// ルート factory を実際に描画する（理由は SC-01 の同名テスト冒頭）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { createSc04OpendAuthRoute, sc04OpendAuthNav } from '../index';

// 本 feature のルートだけを載せる（理由は SC-01 の同名テスト）。
const createRoutes = (shell: Parameters<typeof createSc04OpendAuthRoute>[0]) =>
  [createSc04OpendAuthRoute(shell)] as const;

const WAITING_STATE = {
  promptAvailability: 0,
  prompt: 'phone',
  connection: 'waiting',
  captchaAvailable: false,
  lastLoginAtAvailability: 0,
  lastLoginAt: '2026-09-09T21:02:00Z',
  deviceTrustAvailability: 0,
  deviceTrustPersisted: true,
  egressStabilityAvailability: 0,
  egressStable: true,
  detail: null,
};

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/opend-auth/state') return { ...WAITING_STATE };
    // 発注先（Risk）はバナー用。本テストの主題ではない。
    return { brokerProvider: 1 };
  });
});

describe('SC-04 access control (planning#594)', () => {
  it('grants access to trading-owner', async () => {
    await renderUnitRoute(createRoutes, {
      initialEntry: sc04OpendAuthNav.to,
      roles: ['trading-owner'],
    });
    expect(await screen.findByRole('heading', { name: 'OpenD 認証操作' })).toBeInTheDocument();
  });

  // 🔴 **403 ではなく NotFound である。** 権限外の利用者に「在るが権限が無い」と告げない
  // （NFR-06 の存在秘匿）。本画面は実口座のゲートウェイへの窓口であり、
  // **存在そのものが情報である。**
  it('hides existence (NotFound) for a non-owner user', async () => {
    await renderUnitRoute(createRoutes, {
      initialEntry: sc04OpendAuthNav.to,
      roles: ['user'],
    });
    expect(screen.queryByRole('heading', { name: 'OpenD 認証操作' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '見つかりませんでした' })).toBeInTheDocument();
    // 🔴 権限外では認証 API を呼ばない（存在を推測させない）。呼ぶと、応答の有無や
    // 所要時間の差だけで端点の存在が漏れる。
    expect(mocks.apiFetch).not.toHaveBeenCalled();
  });

  // 🔴 **否定形**: 権限外の画面に、入力欄・送信ボタンが 1 つも現れない。
  // 見出しだけを見て「描かれていない」と判定すると、フォームだけが描かれる退行を見逃す。
  it('renders no code input or submit control for a non-owner user', async () => {
    await renderUnitRoute(createRoutes, {
      initialEntry: sc04OpendAuthNav.to,
      roles: ['user'],
    });
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '送信' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'SMS を再送' })).not.toBeInTheDocument();
  });

  it('exposes a nav entry limited to trading-owner', () => {
    expect(sc04OpendAuthNav.requiresAnyRole).toEqual(['trading-owner']);
    expect(sc04OpendAuthNav.to).toBe('/opend-auth');
  });
});
