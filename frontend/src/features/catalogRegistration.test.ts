import { describe, it, expect, vi, beforeEach } from 'vitest';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';

// SC-01, SC-02, SC-03, SC-04, IADR-0340: **文言カタログの登録が、画面の遅延チャンク側に載っていること**の固定。
//
// ここで固定するのは「画面が何を描くか」ではなく、**バンドル分割の不変条件**である。
// カタログ（ja・442 キー / 25,267 B）は従前 `src/features/index.ts` から再公開され、基盤の合成点の
// 静的 import 経由で**初期チャンク**に入っていた（合成時の初期ロード増 +25,907 B の 97.5%。実測）。
// IADR-0340 でこれを `src/lib/i18n.ts` の登録へ移し、**import するのを 4 画面の Page（＝
// `lazyRouteComponent` が動的 import する遅延チャンクの入口）だけに閉じた。**
//
// 🔴 **本ファイルが feature の内部（`sc0N-*/components/*Page`）を直接 import するのは意図的である。**
// 検証対象が**モジュールグラフの形そのもの**——「`lazyRouteComponent` が読む先が登録を連れているか」
// ——であり、barrel（feature の公開面）はまさにその形を隠すからである。**素通りにしないため**、
// 下の「母集合」テストが route factory の実ソースから動的 import 先を抜き出し、
// 本ファイルが試す 4 本と**完全一致する**ことを突き合わせる（Page の改名・追加で静かに空回りしない）。
//
// 🔴 **捕まえるのは「Page が登録を連れてこなくなった」側だけである。** 逆向き（初期ロード側が
// 誤ってカタログを引き込んだ）のうち、**合成点からの静的辺**は最後のテストが見る。
// それ以外の経路（基盤側の合成の仕方）は**単独リポでは見えない**——見るのは基盤の
// `check-chunk-budget` である（単独リポに「初期ロード」という概念が無い）。この非対称は意図的。

const mocks = vi.hoisted(() => ({ registerUnitMessages: vi.fn() }));
vi.mock('@foundation/i18n', async () => {
  const actual = await vi.importActual<typeof import('@lingui/core')>('@lingui/core');
  return { registerUnitMessages: mocks.registerUnitMessages, i18n: actual.i18n };
});

// 画面は BFF を叩くクエリ層を import するが、本テストはモジュールを**評価するだけ**で描画しない。
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: vi.fn() }));

/** 4 画面の Page モジュール（route factory の `lazyRouteComponent` が動的 import する先と同一）。 */
const PAGE_MODULES = [
  ['SC-01', 'sc01-settings', 'SettingsPage', () => import('./sc01-settings/components/SettingsPage')],
  ['SC-02', 'sc02-risk-settings', 'RiskSettingsPage', () => import('./sc02-risk-settings/components/RiskSettingsPage')],
  ['SC-03', 'sc03-controls', 'ControlStatusPage', () => import('./sc03-controls/components/ControlStatusPage')],
  ['SC-04', 'sc04-opend-auth', 'OpendAuthPage', () => import('./sc04-opend-auth/components/OpendAuthPage')],
] as const;

const ROUTE_FILES = [
  ['sc01-settings', 'sc01SettingsRoute'],
  ['sc02-risk-settings', 'sc02RiskSettingsRoute'],
  ['sc03-controls', 'sc03ControlsRoute'],
  ['sc04-opend-auth', 'sc04OpendAuthRoute'],
] as const;

beforeEach(() => {
  // モジュールレジストリを分け、各画面が**単独で**登録を連れてくることを見る
  // （「1 つでも連れてくれば通る」にしない）。
  vi.resetModules();
  mocks.registerUnitMessages.mockClear();
});

describe('文言カタログの登録が画面の遅延チャンク側に載っている（IADR-0340）', () => {
  it.each(PAGE_MODULES)(
    '%s（%s）の Page モジュールを評価すると ja カタログが基盤へ登録される',
    async (_sc, _dir, _name, importPage) => {
      await importPage();

      expect(mocks.registerUnitMessages).toHaveBeenCalledTimes(1);
      const [registered] = mocks.registerUnitMessages.mock.calls[0] as [Record<string, unknown>];
      // ja 単独カタログ（IADR-0338 決定 3・利用者裁定 2026-09-12 #3「英訳は不要」）。
      expect(Object.keys(registered)).toEqual(['ja']);
      // 空の表を「登録した」と読まない（生成物が空でも呼び出しは成立してしまう）。
      expect(Object.keys(registered.ja as Record<string, unknown>).length).toBeGreaterThan(0);
    },
  );

  it('母集合が一致する: route factory の動的 import 先が、上で試した 4 本と過不足なく同じ', () => {
    // 規則 1（誤りの側から引く）: 「テストが試した集合」ではなく**実ソースの側**から引く。
    // `lazyRouteComponent(() => import('../components/XxxPage'), 'XxxPage')` の import 先を抜く。
    const found = ROUTE_FILES.map(([dir, file]) => {
      // 🔴 `import.meta.url` は jsdom 環境では `http://localhost/...` になりファイルを指さない（実測）。
      // 🔴 `process.cwd()` から解決してはならない —— 本テストは基盤（MSP）の合成 `test:coverage` でも
      // 横断実行され、そこでは cwd が `src/`（pnpm workspace の root）で ENOENT になる（MSP の bump で実測）。
      // **本ファイル自身の絶対パス**（vitest の `expect.getState().testPath`）から解決すると、
      // 単独（cwd = `frontend/`）でも合成（cwd = `src/`）でも同じ場所を指す。
      const testPath = expect.getState().testPath;
      expect(testPath, 'vitest が testPath を返さない').toBeTruthy();
      const source = readFileSync(resolve(dirname(testPath!), `${dir}/routes/${file}.tsx`), 'utf-8');
      const match = /lazyRouteComponent\(\s*\(\)\s*=>\s*import\('\.\.\/components\/([A-Za-z0-9_]+)'\)/.exec(
        source,
      );
      // 🔴 **見つからないことを「変更なし」と読まない**（正規表現が現実に付いていかなくなったら落とす）。
      expect(match, `${dir}/routes/${file}.tsx に lazyRouteComponent の動的 import が無い`).not.toBeNull();
      return [dir, match![1]] as const;
    });

    expect(found).toEqual(PAGE_MODULES.map(([, dir, name]) => [dir, name]));
  });

  it('登録は冪等である（複数の画面が同じモジュールを import しても 1 回だけ）', async () => {
    const mod = await import('@ai-stock-trading/lib/i18n');

    // モジュール評価時に 1 回。明示的に呼び直しても増えない。
    expect(mocks.registerUnitMessages).toHaveBeenCalledTimes(1);
    mod.registerAiStockTradingMessages();
    mod.registerAiStockTradingMessages();
    expect(mocks.registerUnitMessages).toHaveBeenCalledTimes(1);
  });

  it('合成点（features/index.ts）は登録経路を持たない＝初期ロードへカタログを引き込まない', async () => {
    await import('./index');

    // 🔴 ここが 1 回でも呼ばれたら、合成点から `lib/i18n` への静的辺ができている
    // （＝カタログが基盤の初期チャンクへ戻っている）。**ビルドは成功するので、機械で止める。**
    expect(mocks.registerUnitMessages).not.toHaveBeenCalled();
  });
});
