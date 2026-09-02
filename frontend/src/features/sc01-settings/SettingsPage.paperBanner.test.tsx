import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProviders } from '../../testing/renderWithProviders';

// SC-01, FR-12, INDEX 決定 46, #334:
// 内蔵 `paper` 稼働中の警告バナーを **SC-01 でも**表示する。
//
// 計画（05_screens SC-01・共通規約）の明示的な注意:
//   「バナーの見た目は **SC-02 のモックアップの「状態例」区画にのみ描かれている**。SC-01・SC-03 の
//     モックアップ本体には描かれていない（共通要素を代表 1 箇所で示す慣行による）。**実装時は SC-01・SC-03 にも
//     同じバナーを実装すること。** モックアップの見た目だけを頼りにすると実装を落とす」
//
// 本画面は発注先の表示も変更も持たない。それでもバナーを出すのは、`paper` 稼働中であることを
// 利用者がどの画面からでも把握できるようにするためである。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { SettingsPage } from './SettingsPage';
import {
  BROKER_PROVIDER_INTERNAL_PAPER,
  BROKER_PROVIDER_MOOMOO_SIMULATE,
} from '@ai-stock-trading/lib/risk/contracts';
import {
  PAPER_BANNER_DEBUG_MESSAGE,
  PAPER_BANNER_EXCLUSION_MESSAGE,
} from '@ai-stock-trading/lib/paperMode';

const ASSUMPTIONS = {
  assumptions: {
    capitalGainsTaxRate: 0.20315,
    japanCommission: { rate: 0, minimum: 0, cap: 0 },
    unitedStatesCommission: { rate: 0, minimum: 0, cap: 0 },
    fxSpreadRatio: 0,
    minimumExpectedProfitMultiple: 2,
    costLimits: { total: 20000, llm: 15000, infrastructure: 5000, data: 0 },
  },
  version: 1,
  isResolved: true,
};

function mockApi(provider: number | 'unavailable') {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/assumptions/history') return [];
    if (path === '/risk-controls/status') {
      if (provider === 'unavailable') throw new Error('risk service unavailable');
      return { brokerProvider: provider };
    }
    return ASSUMPTIONS;
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-01 内蔵 paper 稼働中の警告バナー（FR-12・#334）', () => {
  it('内蔵 paper 稼働中は必須 2 文言のバナーを表示する', async () => {
    mockApi(BROKER_PROVIDER_INTERNAL_PAPER);
    renderWithProviders(<SettingsPage />);

    const banner = await screen.findByRole('alert', { name: '内蔵 paper 稼働中の警告' });
    expect(within(banner).getByText(PAPER_BANNER_DEBUG_MESSAGE)).toBeInTheDocument();
    expect(within(banner).getByText(PAPER_BANNER_EXCLUSION_MESSAGE)).toBeInTheDocument();
  });

  // 否定形: paper でないときに出してはならない（SIMULATE / 実弾稼働をデバッグ稼働と誤認させる）。
  it('SIMULATE 稼働中はバナーを表示しない', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    renderWithProviders(<SettingsPage />);

    await screen.findByRole('heading', { name: '設定' });
    expect(screen.queryByRole('alert', { name: '内蔵 paper 稼働中の警告' })).not.toBeInTheDocument();
  });

  // 縮退: 発注先が判らない場合はバナーを出さない（「外部へ発注していません」という事実でない断定をしない）。
  // 本画面本来の機能（全体前提条件の閲覧）は Risk サービスの障害に巻き込まれない。
  it('発注先を取得できない場合はバナーを出さず、本画面の機能は動く', async () => {
    mockApi('unavailable');
    renderWithProviders(<SettingsPage />);

    await screen.findByRole('heading', { name: '設定' });
    expect(await screen.findByText('現在のバージョン: 1')).toBeInTheDocument();
    expect(screen.queryByRole('alert', { name: '内蔵 paper 稼働中の警告' })).not.toBeInTheDocument();
  });
});
