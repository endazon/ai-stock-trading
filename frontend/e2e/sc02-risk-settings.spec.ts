import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';
import {
  defaultBff,
  installBff,
  pathWithRoles,
  MONITOR_SETTINGS,
  RISK_SETTINGS,
  WATCHLIST,
} from './fixtures';

// SC-02, FR-13, FR-19, FR-20, UC-06, IADR-0087: リスク設定画面（リスク上限の閲覧/変更）の実ブラウザ E2E。
// BFF 応答はモック（page.route）。実 API・実クラスタ疎通に依存しない（live 検証は #82 系／MSP #284）。
// 本画面は「リスク上限の変更」と「取引ガードの変更」（#188/IADR-0086）の 2 フォームを持つ。両フォームに
// 「変更理由」ラベルと「保存」ボタンがあるため、上限フォームの操作はフォーム（アクセシブル名）で絞って参照する
// （#188 の vitest と同方針）。本 spec は上限の保存フロー（200/400/409）と縮退を対象にする。

// 「リスク上限の変更」フォームに絞ったロケータ群（両フォーム共通ラベルの曖昧さを避ける）。
function limitsForm(page: Page) {
  return page.getByRole('form', { name: 'リスク上限の変更' });
}

test.describe('SC-02 リスク設定（#187）', () => {
  test('trading-owner にリスク上限とガード/段階を表示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    await expect(page.getByRole('heading', { name: 'リスク設定' })).toBeVisible();
    // FR-10, #329, #389, #362: 発注額の上限は **equity 比**であり金額ではない。画面には
    // **百分率**（25）で出る（IADR-0151 決定1。比率入力で `25` と打つと equity の 25 倍になるため）。
    await expect(limitsForm(page).getByLabel('1注文発注額上限（equity 比） %')).toHaveValue(
      String(RISK_SETTINGS.limits.maxOrderAmountRatio * 100),
    );
    // #362, IADR-0151 決定4: 割合だけでは実効額が読めないため、現在 equity での実額を併記する。
    await expect(limitsForm(page).getByText(/現在の equity での実額/).first()).toBeVisible();
    // 取引ガードは変更フォーム（#188）、運用段階は参照表示。
    await expect(page.getByText('取引ガード（変更）')).toBeVisible();
    // FR-20, SC-02, INDEX 決定 46, #334: 運用段階と発注先は**独立した 2 軸**であり、節見出しは
    // 「運用段階と発注先（参照）」になった（旧「運用段階（参照）」）。**参照節と変更節は別である** ——
    // 段階の変更は段階ゲートの承認で行うため画面には無く、発注先の変更だけが SC-02 に置かれる
    // （05_screens 共通規約。SC-03 は参照専用）。両方の節が出ることを固定し、
    // 「発注先の変更 UI が SC-02 から消える」退行も検知する。
    await expect(page.getByText('運用段階と発注先（参照）')).toBeVisible();
    await expect(page.getByText('発注先（変更）')).toBeVisible();
  });

  test('権限外は NotFound（存在秘匿）で BFF を呼ばない', async ({ page }) => {
    let bffCalled = false;
    page.on('request', (req) => {
      if (req.url().includes('/bff/')) bffCalled = true;
    });
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['user']));

    await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'リスク設定' })).toHaveCount(0);
    expect(bffCalled).toBe(false);
  });

  test('上限の保存（PUT /risk-controls/settings/limits 200）で成功通知を表示する', async ({ page }) => {
    let putPath: string | null = null;
    let putBody: { limits?: Record<string, number> } | null = null;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/limits': (route) => {
        putPath = new URL(route.request().url()).pathname;
        putBody = route.request().postDataJSON();
        return { status: 200, body: RISK_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = limitsForm(page);
    await expect(form).toBeVisible();

    await form.getByLabel('変更理由').fill('上限調整');
    await form.getByRole('button', { name: '保存' }).click();

    await expect(form.getByText('保存しました。')).toBeVisible();
    // 画面は /bff/risk-controls/settings/limits を叩く（BFF が Risk へプロキシすべき契約の追認）。
    expect(putPath).toBe('/bff/risk-controls/settings/limits');
    // #362, IADR-0151 決定3: **本文は `*Ratio`（比率）である**。画面の 25（%）が 0.25 として送られる。
    // #389 まで旧名（金額キー）で送って 400 に落ちていた形からの是正であり、値域の関門（画面＋サーバ）が
    // 揃った本 issue で初めてこの形にしてよい。
    expect(putBody!.limits!.maxOrderAmountRatio).toBe(RISK_SETTINGS.limits.maxOrderAmountRatio);
    expect(putBody!.limits).not.toHaveProperty('maxOrderAmount');
  });

  // #362 の否定形: 統制を無効化する値では保存へ進めない（PUT が 1 度も飛ばない）。
  // 画面は百分率入力のため、「equity の 35,000 倍」は 3500000（%）として現れる。
  test('1注文上限に equity の 35,000 倍を入れても PUT を飛ばさない', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/limits': () => {
        putCount += 1;
        return { status: 200, body: RISK_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = limitsForm(page);
    await expect(form).toBeVisible();

    await form.getByLabel('1注文発注額上限（equity 比） %').fill('3500000');
    await form.getByLabel('変更理由').fill('統制を無効化する値');

    const save = form.getByRole('button', { name: '保存' });
    await expect(save).toBeDisabled();
    await expect(form.getByRole('alert').filter({ hasText: '1注文発注額上限' })).toBeVisible();
    expect(putCount).toBe(0);
  });

  test('上限の競合（PUT 409）でメッセージを表示し破壊的な再試行をしない', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/limits': () => {
        putCount += 1;
        return { status: 409, body: { title: '競合', detail: '競合が発生しました。' } };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = limitsForm(page);
    await expect(form).toBeVisible();

    await form.getByLabel('変更理由').fill('上限調整');
    await form.getByRole('button', { name: '保存' }).click();

    await expect(form.getByRole('alert').filter({ hasText: '競合' })).toBeVisible();
    expect(putCount).toBe(1);
  });

  test('上限の検証エラー（PUT 400）でメッセージを表示する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/limits': {
        status: 400,
        body: { errors: { limits: ['1注文発注額上限は必須です'] } },
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = limitsForm(page);
    await expect(form).toBeVisible();

    await form.getByLabel('変更理由').fill('不正値');
    await form.getByRole('button', { name: '保存' }).click();

    await expect(form.getByRole('alert').filter({ hasText: '入力' })).toBeVisible();
  });

  test('履歴取得の失敗（500）は履歴領域のみ縮退する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'GET /risk-controls/settings/history': { status: 500 },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    // 本体（リスク設定）は表示され、履歴領域のみ縮退表示になる。
    await expect(page.getByRole('heading', { name: 'リスク設定' })).toBeVisible();
    await expect(page.getByText('変更履歴は利用できません。')).toBeVisible();
  });

  test('取得失敗（500）は取得失敗メッセージへ縮退する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'GET /risk-controls/settings': { status: 500 },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    await expect(page.getByRole('alert').filter({ hasText: 'リスク設定の取得に失敗' })).toBeVisible();
  });
});

// SC-02, FR-13, FR-03, FR-11, UC-06, IADR-0088, IADR-0090: 監視銘柄（watchlist）変更 UI（#196）の E2E。
// 別サービス（MarketMonitor `/monitor/watchlist`）を独立ロードする。BFF は page.route でモックし、画面が叩く
// パス/メソッド（GET/POST/DELETE /monitor/watchlist）を契約として追認する（実 BFF プロキシ結線は MSP 後続）。
function watchlistTable(page: Page) {
  return page.getByRole('table', { name: '監視銘柄' });
}
function watchlistAddForm(page: Page) {
  return page.getByRole('form', { name: '監視銘柄の追加' });
}

test.describe('SC-02 監視銘柄（#196）', () => {
  test('trading-owner に監視銘柄一覧を市場ラベル付きで表示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    const table = watchlistTable(page);
    await expect(table.getByRole('row', { name: /7203/ })).toContainText('日本');
    await expect(table.getByRole('row', { name: /AAPL/ })).toContainText('米国');
  });

  test('追加は POST /monitor/watchlist（{symbol,market,reason}）を叩き成功通知を出す', async ({ page }) => {
    let postPath: string | null = null;
    let postBody: unknown = null;
    await installBff(page, {
      ...defaultBff(),
      'POST /monitor/watchlist': (route) => {
        postPath = new URL(route.request().url()).pathname;
        postBody = route.request().postDataJSON();
        return { status: 200, body: WATCHLIST };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = watchlistAddForm(page);
    await form.getByLabel('監視銘柄コード').fill('6758');
    await form.getByLabel('監視銘柄の市場').selectOption('1'); // 米国
    await form.getByLabel('追加理由').fill('半導体監視');
    await form.getByRole('button', { name: '監視銘柄を追加' }).click();

    await expect(form.getByText('追加しました。')).toBeVisible();
    expect(postPath).toBe('/bff/monitor/watchlist');
    expect(postBody).toEqual({ symbol: '6758', market: 1, reason: '半導体監視' });
  });

  test('削除は明示確認の後に DELETE /monitor/watchlist（body 理由）を叩く', async ({ page }) => {
    let deletePath: string | null = null;
    let deleteBody: unknown = null;
    await installBff(page, {
      ...defaultBff(),
      'DELETE /monitor/watchlist': (route) => {
        deletePath = new URL(route.request().url()).pathname;
        deleteBody = route.request().postDataJSON();
        return { status: 200, body: [WATCHLIST[0]] };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    // 行の「削除」→確認パネル（理由必須）→「監視から削除」で確定する。
    await watchlistTable(page).getByRole('row', { name: /AAPL/ }).getByRole('button', { name: '削除' }).click();
    const confirm = page.getByRole('group', { name: '監視銘柄の削除確認' });
    await expect(confirm).toBeVisible();
    await confirm.getByLabel('削除理由').fill('監視終了');
    await confirm.getByRole('button', { name: '監視から削除' }).click();

    expect(deletePath).toBe('/bff/monitor/watchlist');
    expect(deleteBody).toEqual({ symbol: 'AAPL', market: 1, reason: '監視終了' });
  });

  test('追加の競合（POST 409）でメッセージを表示し破壊的な再試行をしない', async ({ page }) => {
    let postCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'POST /monitor/watchlist': () => {
        postCount += 1;
        return { status: 409, body: { title: '競合', detail: '競合が発生しました。' } };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = watchlistAddForm(page);
    await form.getByLabel('監視銘柄コード').fill('6758');
    await form.getByLabel('追加理由').fill('半導体監視');
    await form.getByRole('button', { name: '監視銘柄を追加' }).click();

    await expect(form.getByRole('alert').filter({ hasText: '競合' })).toBeVisible();
    expect(postCount).toBe(1);
  });

  test('監視銘柄の取得失敗（404・BFF 未結線）は利用不可メッセージへ縮退する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'GET /monitor/watchlist': { status: 404, body: {} },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    // リスク設定本体は表示され、監視銘柄セクションのみ縮退する（別サービスの独立縮退）。
    await expect(page.getByRole('heading', { name: 'リスク設定' })).toBeVisible();
    await expect(page.getByText('監視銘柄設定は利用できません。')).toBeVisible();
  });
});

// SC-02, FR-03, FR-13, UC-06, #423, IADR-0165 決定2:
// **市場監視パラメータ（変動閾値・クールダウン）が SC-01 §2 から SC-02 へ移管された。**
//
// アクセシブル名は**節ごとに完全に分けてある**（本画面には既に「保存」ボタンが 3 つあり、
// 部分一致のロケータは strict mode 違反を起こす）。したがって本節では
// 「変動閾値を保存」「クールダウンを保存」というフォーム固有の名前で参照する。
test.describe('SC-02 市場監視パラメータ（#423）', () => {
  function thresholdForm(page: Page) {
    return page.getByRole('form', { name: '変動閾値の変更' });
  }

  function cooldownForm(page: Page) {
    return page.getByRole('form', { name: 'クールダウンの変更' });
  }

  test('変動閾値とクールダウンを表示し、監視銘柄の近くに置く', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    await expect(page.getByText('市場監視パラメータ（変動閾値・クールダウン）')).toBeVisible();
    // 比率 0.03 → 3%／TimeSpan "00:15:00" → 0.25 時間。
    await expect(thresholdForm(page).getByLabel('変動閾値 %')).toHaveValue('3');
    await expect(cooldownForm(page).getByLabel('クールダウン 時間')).toHaveValue('0.25');
  });

  // **否定形（裁定の中核）**: 移管先にも収集間隔を作らない（質問票 第 13 回 Q11）。
  test('収集間隔の入力欄は SC-02 にも存在しない', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    await expect(page.getByText('市場監視パラメータ（変動閾値・クールダウン）')).toBeVisible();

    await expect(page.getByLabel(/収集間隔/)).toHaveCount(0);
    await expect(page.getByLabel(/ポーリング/)).toHaveCount(0);
  });

  test('変動閾値は理由必須で保存でき、ワイヤは比率である', async ({ page }) => {
    let putBody: unknown = null;
    await installBff(page, {
      ...defaultBff(),
      'PUT /monitor/settings/movement-threshold': (route) => {
        putBody = route.request().postDataJSON();
        return { status: 200, body: MONITOR_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = thresholdForm(page);

    // 理由が空欄のうちは保存できない（監査のため理由必須）。
    await expect(form.getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();

    await form.getByLabel('変動閾値 %').fill('5');
    await form.getByLabel('変動閾値の変更理由').fill('ボラティリティ上昇');
    await form.getByRole('button', { name: '変動閾値を保存' }).click();

    await expect(form.getByText('変動閾値を保存しました。')).toBeVisible();
    // **ワイヤは比率**（5% → 0.05）。百分率のまま送ると閾値が 100 倍になる。
    expect(putBody).toEqual({ movementThresholdRatio: 0.05, reason: 'ボラティリティ上昇' });
  });

  test('クールダウンは理由必須で保存でき、ワイヤは TimeSpan 文字列である', async ({ page }) => {
    let putBody: unknown = null;
    await installBff(page, {
      ...defaultBff(),
      'PUT /monitor/settings/cooldown': (route) => {
        putBody = route.request().postDataJSON();
        return { status: 200, body: MONITOR_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = cooldownForm(page);

    await expect(form.getByRole('button', { name: 'クールダウンを保存' })).toBeDisabled();

    await form.getByLabel('クールダウン 時間').fill('2');
    await form.getByLabel('クールダウンの変更理由').fill('再トリガー抑制を延ばす');
    await form.getByRole('button', { name: 'クールダウンを保存' }).click();

    await expect(form.getByText('クールダウンを保存しました。')).toBeVisible();
    expect(putBody).toEqual({ cooldown: '02:00:00', reason: '再トリガー抑制を延ばす' });
  });

  // **0 を許してはならない。** 0 ではあらゆる変動が閾値超過となり LLM 費用が暴走する。
  test('値域外の変動閾値・クールダウンは保存できずサーバへ送らない', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /monitor/settings/movement-threshold': () => {
        putCount += 1;
        return { status: 200, body: MONITOR_SETTINGS };
      },
      'PUT /monitor/settings/cooldown': () => {
        putCount += 1;
        return { status: 200, body: MONITOR_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    const threshold = thresholdForm(page);
    await threshold.getByLabel('変動閾値の変更理由').fill('検証');
    await threshold.getByLabel('変動閾値 %').fill('0');
    await expect(threshold.getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();
    await threshold.getByLabel('変動閾値 %').fill('51');
    await expect(threshold.getByRole('button', { name: '変動閾値を保存' })).toBeDisabled();

    const cooldown = cooldownForm(page);
    await cooldown.getByLabel('クールダウンの変更理由').fill('検証');
    await cooldown.getByLabel('クールダウン 時間').fill('25');
    await expect(cooldown.getByRole('button', { name: 'クールダウンを保存' })).toBeDisabled();

    expect(putCount).toBe(0);
  });

  test('市場監視パラメータの取得失敗（500）は当該領域のみ縮退する', async ({ page }) => {
    await installBff(page, {
      ...defaultBff(),
      'GET /monitor/settings': { status: 500 },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    // #424, IADR-0162: 取得失敗も**供給が無い**状態の 1 つであり、規約の文言で示す。
    // 文言は `<strong>` を挟んで複数のテキストノードへ分かれるため、**要素の textContent** に対して
    // 検証する（`getByText` の完全一致では拾えない）。SC-01 §2 から本節が移ってきたときに
    // #424 の規約が落ちていたため、ここで固定する。
    // 定数を import せず**リテラルで固定する**——規約文言そのものが計画の裁定内容であり、
    // 定数を参照すると定数を書き換えたときにテストが追随して退行を検知できない。
    const unavailable = page.getByRole('alert').filter({ hasText: '市場監視パラメータを' });
    await expect(unavailable).toContainText('取得できていません（供給元がありません）');
    await expect(unavailable).toContainText('値が無いのではなく、確認できていません。');
    // リスク上限（RiskManagementService 由来）は独立して表示される。
    await expect(limitsForm(page)).toBeVisible();
  });
});

// SC-02, FR-20, FR-13, UC-06, #423, 06_daytrading-review §4.1 条件 3 / §4.3, IADR-0165 決定4〜決定6:
// **Stage 1 の最小取引件数（既定 100・値域 1〜1000）。**
//
// **100 件未満は警告を常時表示するが、設定を妨げない**（裁定が明示）。
// 両方を固定しないと、「警告を足したついでに拒否する」実装が緑のまま通る。
test.describe('SC-02 Stage 1 の最小取引件数（#423）', () => {
  function tradeCountForm(page: Page) {
    return page.getByRole('form', { name: 'Stage 1 の最小取引件数の変更' });
  }

  test('運用段階の参照表示の近くに現在の設定値を表示する', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));

    await expect(page.getByText('Stage 1 の最小取引件数（変更）')).toBeVisible();
    await expect(tradeCountForm(page).getByLabel('Stage 1 の最小取引件数 件')).toHaveValue(
      String(RISK_SETTINGS.stage1MinimumTradeCount),
    );
  });

  test('件数と理由を入れると PUT する', async ({ page }) => {
    let putBody: unknown = null;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/stage1-minimum-trade-count': (route) => {
        putBody = route.request().postDataJSON();
        return { status: 200, body: RISK_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = tradeCountForm(page);

    await expect(form.getByRole('button', { name: '最小取引件数を保存' })).toBeDisabled();

    await form.getByLabel('Stage 1 の最小取引件数 件').fill('150');
    await form.getByLabel('最小取引件数の変更理由').fill('標本を増やす');
    await form.getByRole('button', { name: '最小取引件数を保存' }).click();

    await expect(form.getByText('Stage 1 の最小取引件数を保存しました。')).toBeVisible();
    expect(putBody).toEqual({ minimumTradeCount: 150, reason: '標本を増やす' });
  });

  test('値域外（0・1001）は保存できずサーバへ送らない', async ({ page }) => {
    let putCount = 0;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/stage1-minimum-trade-count': () => {
        putCount += 1;
        return { status: 200, body: RISK_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = tradeCountForm(page);

    await form.getByLabel('最小取引件数の変更理由').fill('値域外');
    await form.getByLabel('Stage 1 の最小取引件数 件').fill('0');
    await expect(form.getByRole('button', { name: '最小取引件数を保存' })).toBeDisabled();
    await form.getByLabel('Stage 1 の最小取引件数 件').fill('1001');
    await expect(form.getByRole('button', { name: '最小取引件数を保存' })).toBeDisabled();

    expect(putCount).toBe(0);
  });

  // **裁定の核心。** 警告を出したうえで保存できる。
  test('100 件未満は警告を出すが保存を妨げない', async ({ page }) => {
    let putBody: unknown = null;
    await installBff(page, {
      ...defaultBff(),
      'PUT /risk-controls/settings/stage1-minimum-trade-count': (route) => {
        putBody = route.request().postDataJSON();
        return { status: 200, body: RISK_SETTINGS };
      },
    });
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = tradeCountForm(page);

    await form.getByLabel('Stage 1 の最小取引件数 件').fill('30');
    await expect(form.getByText(/統計的な根拠/)).toBeVisible();

    await form.getByLabel('最小取引件数の変更理由').fill('検証期間を短縮したい');
    const save = form.getByRole('button', { name: '最小取引件数を保存' });
    await expect(save).toBeEnabled();
    await save.click();

    expect(putBody).toEqual({ minimumTradeCount: 30, reason: '検証期間を短縮したい' });
  });

  // 逆方向の否定形。満たしているのに警告を出すと、警告が常時出て誰も読まなくなる。
  test('100 件以上では警告を出さない', async ({ page }) => {
    await installBff(page, defaultBff());
    await page.goto(pathWithRoles('/settings/risk', ['trading-owner']));
    const form = tradeCountForm(page);

    await form.getByLabel('Stage 1 の最小取引件数 件').fill('100');
    await expect(form.getByText(/統計的な根拠/)).toHaveCount(0);
  });
});
