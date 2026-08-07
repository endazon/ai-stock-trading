import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';

// SC-03, FR-20, FR-12, UC-06, INDEX 決定 46, #334, IADR-0140 / IADR-0142:
// 統制状態参照画面における発注先の**参照表示**・`paper` 警告バナー／ラベル・変更履歴・
// Stage 1 進捗（除外営業日数の併記）。
//
// 計画（05_screens SC-03）は本画面を**参照専用**と定める——発注先の変更は SC-02 だけが持つ。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { ControlStatusPage } from './ControlStatusPage';
import {
  BROKER_PROVIDER_INTERNAL_PAPER,
  BROKER_PROVIDER_MOOMOO_SIMULATE,
  CHANGE_TYPE_BROKER_PROVIDER,
} from '../risk/contracts';
import type { RiskStatusView, SettingsChangeEntry, StageGateStatus } from '../risk/contracts';
import {
  CONTRACT_RISK_STATUS,
  CONTRACT_SETTINGS_HISTORY,
  CONTRACT_SHORT_SELLING,
  CONTRACT_STAGE_GATE,
  cloneContract,
} from '../risk/contractFixtures';
import {
  PAPER_BANNER_DEBUG_MESSAGE,
  PAPER_BANNER_EXCLUSION_MESSAGE,
  PAPER_REFERENCE_LABEL,
} from '../shared/paperMode';

// #389, IADR-0146: モックはバックエンドの実応答（契約フィクスチャ）を土台に作る。
function status(brokerProvider: number): RiskStatusView {
  return { ...cloneContract(CONTRACT_RISK_STATUS), stage: 1, brokerProvider };
}

const STAGE_GATE: StageGateStatus = {
  ...cloneContract(CONTRACT_STAGE_GATE),
  currentStage: 1,
  currentSettings: {
    ...cloneContract(CONTRACT_STAGE_GATE.currentSettings),
    stage: 1,
    mode: BROKER_PROVIDER_MOOMOO_SIMULATE,
  },
  history: [],
  promotion: { targetStage: 2, eligible: false, unmetCriteria: [] },
  stage1Progress: { qualifiedTradingDays: 42, tradeCount: 70, excludedInternalPaperDays: 3 },
};

// 発注先の変更履歴。実応答の 2 件（発注先変更＝changeType 7 と上限変更＝1）を土台に、
// 本テストが見たい値（変更前後・理由）だけを上書きする。
const PROVIDER_HISTORY: SettingsChangeEntry[] = [
  {
    ...cloneContract(CONTRACT_SETTINGS_HISTORY[0]),
    actor: 'owner',
    changeType: CHANGE_TYPE_BROKER_PROVIDER,
    reason: 'デバッグのため内蔵擬似約定へ落とす',
    before: 'MoomooSimulate',
    after: 'InternalPaper',
  },
  // 発注先以外の履歴（上限変更）は本表に出さない。
  { ...cloneContract(CONTRACT_SETTINGS_HISTORY[1]), actor: 'owner', changeType: 1, reason: '上限見直し' },
];

function mockApi(brokerProvider: number, gate: unknown = STAGE_GATE) {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/risk-controls/stage-gate') return gate;
    if (path === '/risk-controls/settings/history') return PROVIDER_HISTORY;
    if (path === '/risk-controls/short-selling') return cloneContract(CONTRACT_SHORT_SELLING);
    return status(brokerProvider);
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-03 発注先の参照表示（#334）', () => {
  it('運用段階と発注先を別々の行として表示する（1 行に混ぜない）', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    await screen.findByText('発注先');
    expect(screen.getByText('運用段階')).toBeInTheDocument();
    expect(screen.getAllByText('Stage 1（SIMULATE）').length).toBeGreaterThan(0);
    expect(screen.getAllByText(/moomoo SIMULATE/).length).toBeGreaterThan(0);
  });

  // 05_screens:「本画面は参照専用のため表示のみとし、変更は SC-02 で行う」。
  it('発注先の変更 UI を持たない（参照専用）', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    await screen.findByText('発注先');
    expect(screen.queryByRole('form', { name: '発注先の変更' })).not.toBeInTheDocument();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
  });
});

describe('SC-03 内蔵 paper 稼働中の表示（FR-12・#334）', () => {
  it('必須 2 文言のバナーと統制状態カードの paper ラベルを表示する', async () => {
    mockApi(BROKER_PROVIDER_INTERNAL_PAPER);
    render(<ControlStatusPage />);

    const banner = await screen.findByRole('alert', { name: '内蔵 paper 稼働中の警告' });
    expect(within(banner).getByText(PAPER_BANNER_DEBUG_MESSAGE)).toBeInTheDocument();
    expect(within(banner).getByText(PAPER_BANNER_EXCLUSION_MESSAGE)).toBeInTheDocument();
    // 05_screens: 統制状態のカード類にも `paper` である旨のラベルを付す。
    expect(screen.getAllByText(new RegExp(PAPER_REFERENCE_LABEL)).length).toBeGreaterThan(0);
  });

  // 否定形: paper でないときにバナー・ラベルを出してはならない。
  it('SIMULATE 稼働中はバナーも paper ラベルも出さない', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    await screen.findByText('発注先');
    expect(screen.queryByRole('alert', { name: '内蔵 paper 稼働中の警告' })).not.toBeInTheDocument();
    expect(screen.queryByText(new RegExp(PAPER_REFERENCE_LABEL))).not.toBeInTheDocument();
  });
});

describe('SC-03 発注先の変更履歴（FR-20 (2)・#334）', () => {
  it('日時・変更前後・理由を表示し、発注先以外の履歴は混ぜない', async () => {
    mockApi(BROKER_PROVIDER_INTERNAL_PAPER);
    render(<ControlStatusPage />);

    const table = await screen.findByRole('table', { name: '発注先の変更履歴' });
    const rows = within(table).getAllByRole('row');
    // ヘッダ + 発注先の 1 件（上限変更は含まれない）。
    expect(rows).toHaveLength(2);
    expect(within(rows[1]).getByText('MoomooSimulate')).toBeInTheDocument();
    expect(within(rows[1]).getByText('InternalPaper')).toBeInTheDocument();
    expect(within(rows[1]).getByText('デバッグのため内蔵擬似約定へ落とす')).toBeInTheDocument();
    expect(within(table).queryByText('上限見直し')).not.toBeInTheDocument();
  });
});

describe('SC-03 Stage 1 進捗と除外営業日数（FR-20・IADR-0142）', () => {
  it('経過営業日数に paper 稼働による除外日数を併記する', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    expect(await screen.findByText(/経過 42 \/ 60 営業日/)).toHaveTextContent(
      'paper 稼働により 3 日を除外',
    );
    expect(screen.getByText(/取引 70 \/ 100 件/)).toBeInTheDocument();
  });

  it('SIMULATE の約定のみを集計している旨の注記を置く', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    expect(await screen.findByText(/moomoo SIMULATE.*の約定のみを集計/)).toBeInTheDocument();
  });

  // 除外が 0 日なら括弧書きを出さない（常に「除外あり」と読ませない）。
  it('除外日数が 0 なら併記しない', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE, {
      ...STAGE_GATE,
      stage1Progress: { qualifiedTradingDays: 42, tradeCount: 70, excludedInternalPaperDays: 0 },
    });
    render(<ControlStatusPage />);

    const line = await screen.findByText(/経過 42 \/ 60 営業日/);
    expect(line).not.toHaveTextContent('除外');
  });

  // ------------------------------------------------------------------
  // #423, IADR-0164 決定6: Stage 1 の最小取引件数が**設定値**になったことの表示
  // ------------------------------------------------------------------

  // 計画は「100 件未満を設定した場合、**画面と昇格承認の双方**に『統計的な根拠（§4.3）を満たさない
  // 設定である』旨の警告を常時表示する」と定める。SC-03 は段階ゲートの現況を読む画面である。
  //
  // **判定はサーバが宣言した `belowStatisticalBasis` に従う**——画面が `< 100` を自分で判定すると、
  // 警告を出す場所（SC-02・SC-03・Discord）が増えるたびに条件が写経される。
  it('最小取引件数が統計的根拠を下回るときサーバの宣言に従って警告を出す', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE, {
      ...STAGE_GATE,
      stage1Criteria: {
        ...cloneContract(CONTRACT_STAGE_GATE.stage1Criteria),
        minimumTradeCount: 30,
        belowStatisticalBasis: true,
      },
    });
    render(<ControlStatusPage />);

    // 警告の段落そのものを取る（`統計的な根拠` は `<strong>` の中にあり、直接の子テキストではない）。
    const warning = await screen.findByText(/Stage 1 の最小取引件数が/);
    expect(warning).toHaveTextContent('30 件');
    expect(warning).toHaveTextContent('統計的な根拠');
    expect(warning).toHaveTextContent('§4.3');
    // 変更先（SC-02）を案内する（SC-03 は参照専用である）。
    expect(warning).toHaveTextContent('リスク設定');
  });

  // 逆方向の否定形。**満たしているのに警告を出すと、警告が常時出て誰も読まなくなる。**
  it('最小取引件数が既定なら警告を出さない', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);
    await screen.findByText(/経過 42 \/ 60 営業日/);

    expect(screen.queryByText(/Stage 1 の最小取引件数が/)).not.toBeInTheDocument();
    expect(screen.queryByText(/統計的な根拠/)).not.toBeInTheDocument();
  });

  // **サーバの宣言に従う**ことの否定形。件数が 100 未満でも、サーバが警告を宣言していなければ出さない
  // （画面が独自に `< 100` を判定していれば、ここが赤くなる）。
  it('件数が100未満でもサーバが宣言しなければ警告を出さない', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE, {
      ...STAGE_GATE,
      stage1Criteria: {
        ...cloneContract(CONTRACT_STAGE_GATE.stage1Criteria),
        minimumTradeCount: 30,
        belowStatisticalBasis: false,
      },
    });
    render(<ControlStatusPage />);
    await screen.findByText(/経過 42 \/ 60 営業日/);

    expect(screen.queryByText(/Stage 1 の最小取引件数が/)).not.toBeInTheDocument();
    expect(screen.queryByText(/統計的な根拠/)).not.toBeInTheDocument();
  });

  // 件数が設定値であることを画面に出す（100 という定数だと思わせない）。
  it('取引件数の閾値が設定値である旨と計上単位を明記する', async () => {
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE);
    render(<ControlStatusPage />);

    expect(await screen.findByText(/取引 70 \/ 100 件（設定値）/)).toBeInTheDocument();
    const note = screen.getByText(/計上単位は/);
    expect(note).toHaveTextContent('約定が成立した新規建て注文 1 件');
    expect(note).toHaveTextContent('手仕舞いは計上しません');
  });

  // 縮退: 進捗を含まない応答（BFF 未追随・旧版サーバ）でも画面を壊さない。
  it('進捗を含まない応答では進捗領域のみ縮退する', async () => {
    const withoutProgress = { ...STAGE_GATE } as Record<string, unknown>;
    delete withoutProgress.stage1Progress;
    delete withoutProgress.stage1Criteria;
    mockApi(BROKER_PROVIDER_MOOMOO_SIMULATE, withoutProgress);
    render(<ControlStatusPage />);

    expect(await screen.findByText('Stage 1 の進捗は利用できません。')).toBeInTheDocument();
    expect(screen.getByText('発注先')).toBeInTheDocument();
  });
});
