import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { ApiError } from '@foundation/api/ApiError';

// #414, SC-01, SC-02, SC-03, IADR-0288: 本ユニットの**合成面**（ルート factory ＋ ナビ項目）の不変条件。
//
// ここで固定するのは「画面が何を描くか」ではなく、**合成点が本ユニットを束ねたときに成り立つべきこと**である。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { aiStockTradingNavItems, createAiStockTradingRoutes } from './index';

const OWNER = ['trading-owner'];

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('AI 株取引ユニットの合成面（#414）', () => {
  // MSP/IADR-0124 決定 5: **ナビはデータであり `<Link to>` の静的検査が効かない。**
  // よって「全ナビ項目の `to` がルート木に解決すること」を単体テストで固定する。
  //
  // 🔴 **捕まえるのは「ナビだけ足してルートを足し忘れた」側だけである。** 逆向き
  // （ルートだけ足してナビに載せ忘れた）は不変条件ではない（ナビに出さない画面が正しい場合がある）。
  // **逆向きは人が見る。**
  it.each(aiStockTradingNavItems.map((nav) => [nav.id, nav.to] as const))(
    'ナビ項目 %s の遷移先 %s がルート木に解決して描画される',
    async (_id, to) => {
      // 端点は全滅させてよい（ここで見たいのは「ルートが解決して画面が出るか」だけである）。
      mocks.apiFetch.mockRejectedValue(ApiError.fromStatus(503));
      await renderUnitRoute(createAiStockTradingRoutes, { initialEntry: to, roles: OWNER });

      // どの画面も h1 を 1 つ持つ。ルートが解決していなければ何も描かれず、ここで落ちる。
      expect(await screen.findByRole('heading', { level: 1 })).toBeInTheDocument();
    },
  );

  it('全ナビ項目が利用者（trading-owner）限定である', () => {
    // ルート側の `RequireRole anyOf` と同じ値を置く。ずれると「ナビには出るが開けない」
    // （あるいは「権限外にも見出しだけ見える」＝存在秘匿の破れ）になる。
    for (const nav of aiStockTradingNavItems) {
      expect(nav.requiresAnyRole).toEqual(OWNER);
    }
  });

  it('ナビ項目はグループを宣言しない（合成点が機能名のグループへ束ねる）', () => {
    // 🔴 MSP/IADR-0125 決定 9: 基盤の 4 グループは**基盤の計画に属するユニット**の区分である。
    // 本ユニットが `group` を宣言すると、基盤の計画のグループへ紛れ込む（「株式自動売買」の
    // 見出しから消える）。**宣言しないことが正しい**という非対称をここで固定する。
    for (const nav of aiStockTradingNavItems) {
      expect(nav.group).toBeUndefined();
    }
  });

  // ---- 否定形: 合成点の登録漏れを「正常値」に見せない（IADR-0154 の供給可否宣言） ----
  //
  // 本ユニットの端点は BFF（platform 側）に**ユニットごとの登録**が要る（IADR-0091）。登録が漏れた
  // 端点は 404 を返す。404 を「0 件」「該当なし」と読める形で描くと、**統制が働いている画面と
  // 見分けがつかなくなる**（#403 の `ControlViolationCount` 既定 0 が「違反なし」に見えた fail-open と同型）。
  describe('BFF に登録されていない端点（404）を正常値らしく描かない', () => {
    beforeEach(() => {
      mocks.apiFetch.mockRejectedValue(ApiError.fromStatus(404));
    });

    it('SC-01: 設定情報を「利用できません」と述べ、既定値のフォームを出さない', async () => {
      await renderUnitRoute(createAiStockTradingRoutes, { initialEntry: '/settings', roles: OWNER });

      expect(await screen.findByText('設定情報は利用できません。')).toBeInTheDocument();
      // 取得できていないのにフォームを描くと、**組み込みの初期値が「現在の設定」に見える**。
      expect(screen.queryByRole('form', { name: '全体前提条件の変更' })).not.toBeInTheDocument();
    });

    it('SC-02: リスク設定・監視銘柄のいずれも「0 件」ではなく「利用できません」と述べる', async () => {
      await renderUnitRoute(createAiStockTradingRoutes, {
        initialEntry: '/settings/risk',
        roles: OWNER,
      });

      expect(await screen.findByText('リスク設定は利用できません。')).toBeInTheDocument();
      expect(await screen.findByText('監視銘柄設定は利用できません。')).toBeInTheDocument();
      // 🔴 「監視銘柄はありません。」は**監視対象が 0 件である**という別の事実である。
      // 登録漏れをこれで描くと、監視されていないことに気付けない。
      expect(screen.queryByText('監視銘柄はありません。')).not.toBeInTheDocument();
      expect(screen.queryByRole('table', { name: '監視銘柄' })).not.toBeInTheDocument();
    });

    it('SC-03: 統制状態・維持率のいずれも「問題なし」に見せない', async () => {
      await renderUnitRoute(createAiStockTradingRoutes, { initialEntry: '/controls', roles: OWNER });

      expect(await screen.findByText('統制状態は利用できません。')).toBeInTheDocument();
      expect(
        await screen.findByText(
          '維持率・空売りの現況を取得できませんでした。値が無いのではなく、確認できていません。',
        ),
      ).toBeInTheDocument();
      // 発注先の変更履歴も「0 件」と述べない（履歴が無いのか、端点が無いのかは別である）。
      expect(screen.queryByText('発注先の変更履歴はありません。')).not.toBeInTheDocument();
      expect(await screen.findByText('発注先の変更履歴は利用できません。')).toBeInTheDocument();
    });
  });
});
