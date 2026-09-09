import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { renderWithProviders } from '@ai-stock-trading/testing/renderWithProviders';

// SC-04, FR-09, FR-11, UC-06, NFR-05, ADR-0002 前提条件1, ADR-0024 決定1・2, IADR-0321, planning#594:
// OpenD 認証操作画面の表示・操作。
//
// **本ファイルの中核は 3 状態の描き分け**（値がある / 対象なし / 供給が無い）である。
// 取り違えると、壊れたゲートウェイが「正常にログインできている」ように見える。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { OpendAuthPage } from './OpendAuthPage';

const NOT_SUPPLIED_TEXT = '取得できていません（供給元がありません）';
const CODE = '483921';

/** 既定は「SMS 検証コード待ち」。各テストで必要な部分だけ上書きする。 */
function stateOf(overrides: Record<string, unknown> = {}) {
  return {
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
    ...overrides,
  };
}

/** `brokerProvider` は Risk 由来（バナー・「現在の発注先」用）。1 = moomoo SIMULATE 相当。 */
function mockApi(state: unknown, opts: { onSubmit?: () => unknown; onResend?: () => unknown } = {}) {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === '/opend-auth/state') {
      if (state instanceof Error) throw state;
      return state;
    }
    if (path === '/opend-auth/code') return opts.onSubmit?.();
    if (path === '/opend-auth/resend') return opts.onResend?.();
    return { brokerProvider: 1 };
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SC-04 OpenD 認証操作画面（planning#594）', () => {
  // ---- 3 状態: 値がある ----

  it('待機中のプロンプト種別を表示し、入力欄と送信を有効にする', async () => {
    mockApi(stateOf());
    renderWithProviders(<OpendAuthPage />);

    expect(await screen.findByText('SMS 検証コード')).toBeInTheDocument();
    expect(await screen.findByText('検証コード待ち')).toBeInTheDocument();
    expect(screen.getByRole('textbox')).toBeEnabled();
  });

  // ---- 3 状態: 対象なし（正常）----

  // 🔴 「いま入力を待っていない」は**正常**である。未供給の文言を出してはならない
  // （出すと、正常なゲートウェイが壊れているように見える＝逆向きの事故）。
  it('入力待ちでないときは「対象なし」として描き、未供給の文言を出さない', async () => {
    mockApi(stateOf({ promptAvailability: 2, prompt: null, connection: 'idle' }));
    renderWithProviders(<OpendAuthPage />);

    expect(await screen.findByText('いま OpenD は入力を待っていません')).toBeInTheDocument();
    expect(await screen.findByText('ログイン済み — API 稼働中')).toBeInTheDocument();
    // 待機中プロンプト欄に未供給の文言が出ていないこと（送信履歴の未供給表示とは別物）。
    expect(
      screen.getByText(/いま OpenD は入力を待っていないため、入力・送信はできません/),
    ).toBeInTheDocument();
    expect(screen.getByRole('textbox')).toBeDisabled();
  });

  // ---- 3 状態: 供給が無い ----

  // 🔴 **本画面で最も高くつく取り違え。** 到達できていないだけなのに「ログイン済み」と描くと、
  // 利用者は「認証は済んでいる」と信じ、発注が届かない理由を別の場所に探し続ける。
  it('状態を取得できないときは「供給が無い」として描き、「入力待ちではない」と描かない', async () => {
    mockApi(stateOf({ promptAvailability: 1, prompt: null, connection: 'unavailable' }));
    renderWithProviders(<OpendAuthPage />);

    expect(await screen.findAllByText(NOT_SUPPLIED_TEXT)).not.toHaveLength(0);
    expect(screen.queryByText('いま OpenD は入力を待っていません')).not.toBeInTheDocument();
    expect(screen.queryByText('ログイン済み — API 稼働中')).not.toBeInTheDocument();
    expect(
      screen.getByText(/「いま入力を待っていない（ログイン済み）」とは別の状態です/),
    ).toBeInTheDocument();
    expect(screen.getByRole('textbox')).toBeDisabled();
  });

  // 取得そのものが失敗（BFF 未登録＝404 など）した場合も同じ側へ倒れる。
  it('状態取得自体が失敗しても「入力待ちではない」に見せない', async () => {
    mockApi(ApiError.fromStatus(404));
    renderWithProviders(<OpendAuthPage />);

    expect(
      await screen.findByText(/「いま入力を待っていない」のではなく、確認できていません。/),
    ).toBeInTheDocument();
    expect(screen.queryByText('いま OpenD は入力を待っていません')).not.toBeInTheDocument();
    expect(screen.getByRole('textbox')).toBeDisabled();
  });

  // 🔴 **未知の状態は「供給が無い」へ倒す**（サーバが `waiting` と言っても種別が読めなければ入力させない）。
  it('種別が読めない未供給は入力を許さない', async () => {
    mockApi(stateOf({ promptAvailability: 1, prompt: null, connection: 'unavailable' }));
    renderWithProviders(<OpendAuthPage />);

    await screen.findByRole('heading', { name: 'OpenD 認証操作' });
    expect(screen.getByRole('button', { name: '送信' })).toBeDisabled();
  });

  // ---- 供給可否は「値の有無」から推測しない（逆向きの否定形）----

  // 🔴 供給されている値を未供給として描かない。SC-03 で実測された逆向きの事故と同型である。
  it('供給されている最終ログイン日時を未供給として描かない', async () => {
    mockApi(stateOf());
    renderWithProviders(<OpendAuthPage />);

    await screen.findByText('SMS 検証コード');
    const definitions = screen.getAllByRole('definition').map((d) => d.textContent ?? '');
    // 最終ログイン成功の欄が未供給文言になっていない。
    expect(definitions.some((t) => t.includes('2026'))).toBe(true);
  });

  it('構成から供給される 2 条件（デバイス信頼・egress 安定）を表示する', async () => {
    mockApi(stateOf());
    renderWithProviders(<OpendAuthPage />);

    expect(await screen.findByText('永続化済')).toBeInTheDocument();
    expect(screen.getByText('安定（固定 NAT）')).toBeInTheDocument();
  });

  it('構成が無い 2 条件は未供給として描く', async () => {
    mockApi(
      stateOf({
        deviceTrustAvailability: 1,
        deviceTrustPersisted: null,
        egressStabilityAvailability: 1,
        egressStable: null,
      }),
    );
    renderWithProviders(<OpendAuthPage />);

    await screen.findByText('SMS 検証コード');
    expect(screen.queryByText('永続化済')).not.toBeInTheDocument();
    expect(screen.queryByText('安定（固定 NAT）')).not.toBeInTheDocument();
  });

  // ---- 利用者はコマンドを選べない ----

  // 🔴 本画面の中核の統制。**自由入力のコンソールを置かない。**
  it('自由入力のコンソールを持たず、入力欄は検証コードの 1 つだけである', async () => {
    mockApi(stateOf());
    renderWithProviders(<OpendAuthPage />);

    await screen.findByText('SMS 検証コード');
    expect(screen.getAllByRole('textbox')).toHaveLength(1);
    // コマンドを選ばせる UI（選択・ラジオ）が 1 つも無い。
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
  });

  it('送信本文はコードだけで、コマンド種別を含まない', async () => {
    mockApi(stateOf(), { onSubmit: () => undefined });
    renderWithProviders(<OpendAuthPage />);

    await screen.findByText('SMS 検証コード');
    await userEvent.type(screen.getByRole('textbox'), CODE);
    await userEvent.click(screen.getByRole('button', { name: '送信' }));

    await waitFor(() => {
      expect(mocks.apiFetch).toHaveBeenCalledWith('/opend-auth/code', {
        method: 'POST',
        json: { code: CODE },
      });
    });
    // 本文に command / kind / prompt が無い（型で塞いでいることの実測）。
    const call = mocks.apiFetch.mock.calls.find((c) => c[0] === '/opend-auth/code');
    expect(Object.keys(call![1].json)).toEqual(['code']);
  });

  it('再送は本文を持たない', async () => {
    mockApi(stateOf(), { onResend: () => undefined });
    renderWithProviders(<OpendAuthPage />);

    await screen.findByText('SMS 検証コード');
    await userEvent.click(screen.getByRole('button', { name: 'SMS を再送' }));

    await waitFor(() => {
      expect(mocks.apiFetch).toHaveBeenCalledWith('/opend-auth/resend', { method: 'POST' });
    });
  });

  // ---- 検証コードの値を残さない（NFR-05 の適用範囲の拡張） ----

  // 🔴 送信後に入力欄へ値が残っていると、画面を離れるまで肩越しに読める状態が続く。
  it('送信後に入力欄からコードを消す', async () => {
    mockApi(stateOf(), { onSubmit: () => undefined });
    renderWithProviders(<OpendAuthPage />);

    await screen.findByText('SMS 検証コード');
    const input = screen.getByRole('textbox');
    await userEvent.type(input, CODE);
    await userEvent.click(screen.getByRole('button', { name: '送信' }));

    await waitFor(() => expect(input).toHaveValue(''));
  });

  // 🔴 失敗表示に入力値を反射しない（「483921 は誤りです」と描かない）。
  it('送信が拒否されても画面にコードを反射しない', async () => {
    mockApi(stateOf(), {
      onSubmit: () => {
        throw ApiError.fromStatus(400);
      },
    });
    renderWithProviders(<OpendAuthPage />);

    await screen.findByText('SMS 検証コード');
    await userEvent.type(screen.getByRole('textbox'), CODE);
    await userEvent.click(screen.getByRole('button', { name: '送信' }));

    expect(
      await screen.findByText('検証コードが受け付けられませんでした。もう一度入力してください。'),
    ).toBeInTheDocument();
    expect(document.body.textContent).not.toContain(CODE);
  });

  it('待機中でないというサーバの拒否（409）をそのまま利用者へ述べる', async () => {
    mockApi(stateOf(), {
      onSubmit: () => {
        throw ApiError.fromStatus(409);
      },
    });
    renderWithProviders(<OpendAuthPage />);

    await screen.findByText('SMS 検証コード');
    await userEvent.type(screen.getByRole('textbox'), CODE);
    await userEvent.click(screen.getByRole('button', { name: '送信' }));

    expect(
      await screen.findByText('いま OpenD は入力を待っていないため、送信できませんでした。'),
    ).toBeInTheDocument();
    expect(document.body.textContent).not.toContain(CODE);
  });

  // ---- 入力の検証（SMS は 6 桁の数字） ----

  it('SMS は 6 桁の数字でなければ送信できない', async () => {
    mockApi(stateOf());
    renderWithProviders(<OpendAuthPage />);

    await screen.findByText('SMS 検証コード');
    await userEvent.type(screen.getByRole('textbox'), '12345');
    expect(screen.getByRole('button', { name: '送信' })).toBeDisabled();

    await userEvent.type(screen.getByRole('textbox'), '6');
    expect(screen.getByRole('button', { name: '送信' })).toBeEnabled();
  });

  // 画像 CAPTCHA は桁数・文字種が定まらないため、空でないことだけを見る。
  it('画像 CAPTCHA では桁数を強制せず、画像を提示する', async () => {
    mockApi(stateOf({ prompt: 'pic', captchaAvailable: true }));
    renderWithProviders(<OpendAuthPage />);

    expect(await screen.findByAltText('OpenD が返した画像 CAPTCHA')).toBeInTheDocument();
    await userEvent.type(screen.getByRole('textbox'), 'a1b2');
    expect(screen.getByRole('button', { name: '送信' })).toBeEnabled();
  });

  // ---- 許可リスト（利用者への情報） ----

  it('送れる 3 コマンドと送れないコマンドを表で示す', async () => {
    mockApi(stateOf());
    renderWithProviders(<OpendAuthPage />);

    const table = await screen.findByRole('table', { name: 'この画面から送れる操作' });
    expect(table).toHaveTextContent('input_phone_verify_code');
    expect(table).toHaveTextContent('input_pic_verify_code');
    expect(table).toHaveTextContent('req_phone_verify_code');
    // 禁止側も明示する（**送れないことが読み取れる**）。色ではなく文言で述べる。
    expect(table).toHaveTextContent('show_delay_report');
    expect(table).toHaveTextContent('送れない');
  });

  // ---- 送信履歴（供給が無い） ----

  // 🔴 「送信履歴はありません」は**一度も送信していない**という別の事実である。
  // 照会 API が無いことをそれで描くと、経路の欠落に気付けなくなる。
  it('送信履歴は「供給が無い」として描き、「履歴はありません」と描かない', async () => {
    mockApi(stateOf());
    renderWithProviders(<OpendAuthPage />);

    expect(
      await screen.findByText(/送信が無いのではなく、履歴を読む照会 API がまだありません。/),
    ).toBeInTheDocument();
    expect(screen.queryByText('送信履歴はありません。')).not.toBeInTheDocument();
    expect(screen.queryByRole('table', { name: '送信履歴' })).not.toBeInTheDocument();
  });

  // ---- 取引の統制操作を持たない ----

  // 破壊的統制操作（昇格承認・pause/resume・kill switch）は Discord Bot が一次窓口である。
  it('取引の統制操作を 1 つも持たない', async () => {
    mockApi(stateOf());
    renderWithProviders(<OpendAuthPage />);

    await screen.findByText('SMS 検証コード');
    const buttonNames = screen.getAllByRole('button').map((b) => b.textContent ?? '');
    expect(buttonNames).toEqual(expect.arrayContaining(['送信', 'SMS を再送']));
    for (const forbidden of ['緊急停止', '一時停止', '再開', '承認', '昇格']) {
      expect(buttonNames.some((n) => n.includes(forbidden))).toBe(false);
    }
  });
});
