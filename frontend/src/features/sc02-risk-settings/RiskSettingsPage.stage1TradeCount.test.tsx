import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';

// SC-02, FR-20, FR-13, FR-11, UC-06, #423, 06_daytrading-review §4.1 条件 3 / §4.3, IADR-0164 決定4〜決定6:
// **Stage 1 の最小取引件数が SC-02 の設定項目になったことの退行防止。**
//
// 2026-08-07 の利用者裁定（質問票 第 13 回 Q6 の追加指示）:
//   - **既定 100 件・値域 1〜1000。** 変更理由必須・監査ログ・楽観排他の対象。
//   - **100 件未満を設定した場合は警告を常時表示する。警告は設定を妨げない。**
//   - この設定は条件 1（統制違反 0 件）・条件 2（60 営業日）には及ばない。
//     §4.3 の打ち切り規則（累計 120 営業日）も変わらない。
//
// **本テスト群で最も重要なのは「警告が出ること」と「警告が保存を妨げないこと」の両方である。**
// 片方だけを固定すると、警告を足した実装が「ついでに拒否する」へすり替わっても緑のまま通る。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { RiskSettingsPage } from './RiskSettingsPage';
import { CONTRACT_RISK_SETTINGS, CONTRACT_RISK_STATUS, cloneContract } from '../risk/contractFixtures';
import { CONTRACT_MONITOR_SETTINGS } from '../monitor/contractFixtures';
import { STAGE1_TRADE_COUNT_DEFAULT } from '../risk/contracts';

const STATUS = cloneContract(CONTRACT_RISK_STATUS);
const MONITOR = cloneContract(CONTRACT_MONITOR_SETTINGS);

function mockBff(options: { stage1MinimumTradeCount?: number; put?: 'ok' | 'validation' } = {}) {
  const { stage1MinimumTradeCount = STAGE1_TRADE_COUNT_DEFAULT, put = 'ok' } = options;
  const settings = { ...cloneContract(CONTRACT_RISK_SETTINGS), stage1MinimumTradeCount };
  mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
    if (path === '/risk-controls/settings/history') return [];
    if (path === '/risk-controls/status') return STATUS;
    if (path === '/monitor/watchlist') return [];
    if (path === '/monitor/watchlist/history') return [];
    if (path === '/monitor/settings') return MONITOR;
    if (path === '/monitor/settings/history') return [];
    if (path === '/risk-controls/settings/stage1-minimum-trade-count' && req?.method === 'PUT') {
      if (put === 'validation') {
        throw new ApiError('validation', '入力内容に誤りがあります。', 400, [
          'Stage 1 の最小取引件数は 1 以上 1000 以下（件）で指定してください。',
        ]);
      }
      return settings;
    }
    return settings;
  });
}

async function tradeCountForm() {
  return screen.findByRole('form', { name: 'Stage 1 の最小取引件数の変更' });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-02 Stage 1 の最小取引件数（#423）', () => {
  it('現在の設定値を表示する', async () => {
    mockBff({ stage1MinimumTradeCount: 100 });
    render(<RiskSettingsPage />);
    const form = await tradeCountForm();

    expect(within(form).getByLabelText('Stage 1 の最小取引件数 件')).toHaveValue(100);
    expect(within(form).getByText(/現在の設定:/)).toHaveTextContent('100 件');
  });

  // 計画は「**運用段階（FR-20）の参照表示の近くに置く**」と定める（段階ゲートの合格条件に効く値であるため）。
  it('運用段階の参照表示の近くに置かれている', async () => {
    mockBff();
    render(<RiskSettingsPage />);
    await tradeCountForm();

    const html = document.body.innerHTML;
    const stageIndex = html.indexOf('運用段階と発注先（参照）');
    const tradeCountIndex = html.indexOf('Stage 1 の最小取引件数（変更）');
    expect(stageIndex).toBeGreaterThanOrEqual(0);
    expect(tradeCountIndex).toBeGreaterThan(stageIndex);
  });

  it('理由が無いと保存できない（監査のため理由必須）', async () => {
    mockBff();
    render(<RiskSettingsPage />);
    const form = await tradeCountForm();

    expect(within(form).getByRole('button', { name: '最小取引件数を保存' })).toBeDisabled();
  });

  it('件数と理由を入れると PUT する', async () => {
    mockBff();
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    const form = await tradeCountForm();

    await user.clear(within(form).getByLabelText('Stage 1 の最小取引件数 件'));
    await user.type(within(form).getByLabelText('Stage 1 の最小取引件数 件'), '150');
    await user.type(within(form).getByLabelText('最小取引件数の変更理由'), '標本を増やす');

    // NFR (#498): click の前に有効化を待ち、PUT の発火そのものを先に確かめる。
    //
    // 本テストは CI で 1 度だけ落ちており、そのときの出力は
    // 「`toHaveBeenCalledWith` 不一致・Number of calls: 7」だけであった。
    // **これでは「PUT が発火しなかった」のか「違う引数で発火した」のかを区別できない**
    // （7 件は初期表示の GET 群で、PUT が 0 件でも 1 件でも同じ見え方になる）。
    //
    // `userEvent.click()` は**無効化されたボタンに対して例外も投げず何もしない**ため、
    // 理由入力による再レンダリングが click に間に合わなければ PUT は発火しない。
    // **ただしこの機序は未実証である** —— 素で 15 回・CPU 飽和下で 6 回、計 21 回連続で再現しなかった。
    // よって「原因を直した」とは書かない。**次に落ちたとき原因を切り分けられるようにする**のが本変更の目的である。
    // 🔴 【2 度目の発生・2026-08-15 / PR #509 の CI】前回入れた切り分けが機序を 1 段絞った。
    // 落ちたのは PUT の assert ではなく **`toBeEnabled()` の waitFor** であった——
    // **click が握り潰されたのではなく、ボタンがそもそも有効化されなかった。**
    //
    // それを踏まえて 2 つ直す。
    //
    // 1. **入力が入ったことを先に確かめる。** ボタンの `disabled` は
    //    「値域エラー」「理由の空」「保存中」の 3 つで決まる。入力が入っていなければ
    //    有効化されないのは**正しい挙動**であり、テストの前提が崩れているだけである。
    //    ここで落ちれば「入力が届いていない」と分かり、ボタン側の問題と区別できる。
    // 2. **待ち時間を明示的に伸ばす。** 落ち方は `waitFor` の**時間切れ**であり、既定は 1000ms である。
    //    CI の負荷下で `userEvent` の入力と React の再描画が 1 秒に収まらなければ、
    //    **実装が正しくても落ちる**。伸ばしても**有効化されないなら結局落ちる**ため、
    //    不具合を隠すことにはならない（遅くなるだけである）。
    //
    // 🔴 **機序はまだ確定していない。** 「掴んだノードが作り直されるのでは」という仮説は
    // **実測で否定した**（同一性を調べたところ React は同じノードの属性を書き換えており、
    // ノードは作り直されていなかった）。**要素を毎回引き直すのは堅牢化であって原因の是正ではない。**
    // 本変更もまた「原因を直した」とは書かない。
    expect(within(form).getByLabelText('Stage 1 の最小取引件数 件')).toHaveValue(150);
    expect(within(form).getByLabelText('最小取引件数の変更理由')).toHaveValue('標本を増やす');

    await waitFor(
      () => expect(within(form).getByRole('button', { name: '最小取引件数を保存' })).toBeEnabled(),
      { timeout: 5000 },
    );
    await user.click(within(form).getByRole('button', { name: '最小取引件数を保存' }));

    const putCalls = mocks.apiFetch.mock.calls.filter((call) => call[1]?.method === 'PUT');
    expect(putCalls).toHaveLength(1); // 先にここで落ちれば「発火しなかった」と分かる
    expect(mocks.apiFetch).toHaveBeenCalledWith('/risk-controls/settings/stage1-minimum-trade-count', {
      method: 'PUT',
      json: { minimumTradeCount: 150, reason: '標本を増やす' },
    });
  });

  // ------------------------------------------------------------------
  // 値域の境界（0 不可 / 1 可 / 1000 可 / 1001 不可）
  // ------------------------------------------------------------------

  it.each(['1', '1000'])('境界値（%s 件）は保存できる', async (value) => {
    mockBff();
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    const form = await tradeCountForm();

    await user.type(within(form).getByLabelText('最小取引件数の変更理由'), '境界の検証');
    await user.clear(within(form).getByLabelText('Stage 1 の最小取引件数 件'));
    await user.type(within(form).getByLabelText('Stage 1 の最小取引件数 件'), value);

    expect(within(form).getByRole('button', { name: '最小取引件数を保存' })).toBeEnabled();
  });

  it.each(['0', '-1', '1001'])('値域外（%s 件）は保存できずサーバへ送らない', async (value) => {
    mockBff();
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    const form = await tradeCountForm();

    await user.type(within(form).getByLabelText('最小取引件数の変更理由'), '値域外');
    await user.clear(within(form).getByLabelText('Stage 1 の最小取引件数 件'));
    await user.type(within(form).getByLabelText('Stage 1 の最小取引件数 件'), value);

    expect(within(form).getByRole('button', { name: '最小取引件数を保存' })).toBeDisabled();
    expect(mocks.apiFetch).not.toHaveBeenCalledWith(
      '/risk-controls/settings/stage1-minimum-trade-count',
      expect.anything(),
    );
  });

  // ------------------------------------------------------------------
  // 100 件未満の警告（**常時表示するが、設定を妨げない**）
  // ------------------------------------------------------------------

  it.each(['1', '30', '99'])('%s 件（100 件未満）を入力すると警告を常時表示する', async (value) => {
    mockBff();
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    const form = await tradeCountForm();

    await user.clear(within(form).getByLabelText('Stage 1 の最小取引件数 件'));
    await user.type(within(form).getByLabelText('Stage 1 の最小取引件数 件'), value);

    const warning = within(form).getByText(/統計的な根拠/);
    expect(warning).toBeInTheDocument();
    expect(warning).toHaveTextContent('§4.3');
  });

  // 逆方向の否定形。**満たしているのに警告を出すと、警告が常時出て誰も読まなくなる。**
  it.each(['100', '101', '1000'])('%s 件（100 件以上）では警告を出さない', async (value) => {
    mockBff();
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    const form = await tradeCountForm();

    await user.clear(within(form).getByLabelText('Stage 1 の最小取引件数 件'));
    await user.type(within(form).getByLabelText('Stage 1 の最小取引件数 件'), value);

    expect(within(form).queryByText(/統計的な根拠/)).not.toBeInTheDocument();
  });

  // **裁定の核心。** 警告が出た状態でも保存できなければならない。
  // ここが赤くなる実装は「警告」を「拒否」へすり替えたことを意味する。
  it('100 件未満でも保存できる（警告は設定を妨げない）', async () => {
    mockBff();
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    const form = await tradeCountForm();

    await user.clear(within(form).getByLabelText('Stage 1 の最小取引件数 件'));
    await user.type(within(form).getByLabelText('Stage 1 の最小取引件数 件'), '30');
    await user.type(within(form).getByLabelText('最小取引件数の変更理由'), '検証期間を短縮したい');

    // 警告は出ているが、保存ボタンは有効である。
    expect(within(form).getByText(/統計的な根拠/)).toBeInTheDocument();
    const save = within(form).getByRole('button', { name: '最小取引件数を保存' });
    expect(save).toBeEnabled();

    await user.click(save);

    expect(mocks.apiFetch).toHaveBeenCalledWith('/risk-controls/settings/stage1-minimum-trade-count', {
      method: 'PUT',
      json: { minimumTradeCount: 30, reason: '検証期間を短縮したい' },
    });
  });

  // 保存済みの値が 100 件未満なら、画面を開いた時点で警告が出ている（**常時表示**）。
  it('保存済みの値が 100 件未満なら画面を開いた時点で警告が出ている', async () => {
    mockBff({ stage1MinimumTradeCount: 40 });
    render(<RiskSettingsPage />);
    const form = await tradeCountForm();

    expect(within(form).getByText(/統計的な根拠/)).toBeInTheDocument();
  });

  // ------------------------------------------------------------------
  // 裁定の適用範囲（設定で変えられるのは件数だけである）
  // ------------------------------------------------------------------

  it('条件 1・条件 2・打ち切り規則は変えられない旨を明記する', async () => {
    mockBff();
    render(<RiskSettingsPage />);
    await tradeCountForm();

    expect(screen.getByText(/変更できるのは/)).toHaveTextContent('取引件数だけ');
    expect(screen.getByText(/統制違反 0 件・60 営業日の条件と、/)).toBeInTheDocument();
    expect(screen.getByText(/累計 120 営業日で Stage 0 へ差し戻す規則は変わりません/)).toBeInTheDocument();
  });

  // 計上単位を画面にも書く（「件」の意味が読み手によって変わらないようにする）。
  it('計上単位（約定が成立した新規建て注文 1 件）を画面に明記する', async () => {
    mockBff();
    render(<RiskSettingsPage />);
    await tradeCountForm();

    const note = screen.getByText(/計上単位は/);
    expect(note).toHaveTextContent('約定が成立した新規建て注文 1 件');
    expect(note).toHaveTextContent('分割約定しても 1 件');
    expect(note).toHaveTextContent('手仕舞いは計上しません');
  });

  it('検証エラー（400）はメッセージ表示に留め破壊的な再試行をしない', async () => {
    mockBff({ put: 'validation' });
    const user = userEvent.setup();
    render(<RiskSettingsPage />);
    const form = await tradeCountForm();

    await user.type(within(form).getByLabelText('最小取引件数の変更理由'), '検証');
    await user.click(within(form).getByRole('button', { name: '最小取引件数を保存' }));

    expect(await within(form).findByText(/入力内容に誤りがあります/)).toBeInTheDocument();
    const putCalls = mocks.apiFetch.mock.calls.filter(
      ([path]) => path === '/risk-controls/settings/stage1-minimum-trade-count',
    );
    expect(putCalls).toHaveLength(1);
  });
});
