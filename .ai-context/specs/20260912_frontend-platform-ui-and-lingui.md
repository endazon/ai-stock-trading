---
title: 基盤の共有 UI（@platform/ui）・Lingui・lucide を受け入れ、4 画面を hi-fi モックの構造へ合わせる
type: spec
status: done
related_ids: [SC-01, SC-02, SC-03, SC-04, ADR-0001, IADR-0338, IADR-0339, IADR-0288, IADR-0280]
author: claude (Claude Code)
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/ai-stock-trading/05_screens/mockups/hi-fi/sc-01.html
  - planning:projects/ai-stock-trading/05_screens/mockups/hi-fi/sc-02.html
  - planning:projects/ai-stock-trading/05_screens/mockups/hi-fi/sc-03.html
  - planning:projects/ai-stock-trading/05_screens/mockups/hi-fi/sc-04.html
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 仕様書: 基盤の共有 UI（@platform/ui）・Lingui・lucide を受け入れ、4 画面を hi-fi モックの構造へ合わせる

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-01 設定 / SC-02 リスク設定 / SC-03 承認・統制状態参照 / SC-04 OpenD 認証操作（**4 画面すべて**）
- 関連 ADR: ADR-0001（基盤の再利用。**共有 UI を自前で作り直さないことの根拠**）
- 基盤（microservices-platform）の計画 ADR: `MSP/ADR-0031`（フロントエンド採用技術＝React 19 / TanStack /
  Tailwind / Lingui / `@platform/ui`）、`MSP/ADR-0066`（feature 間 import の禁止）
- 計画書: https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/05_screens/01_screens.md
  （隣接クローンの既定パスは `../project-planning`／読み取り専用）
- ハイファイモックアップ: 同リポジトリ `projects/ai-stock-trading/05_screens/mockups/hi-fi/sc-0{1,2,3,4}.html`
  （Nocturne デザインシステム）
- 先行 IADR: IADR-0080（単独リポで自己完結するフロント・`@foundation` スタブ）、IADR-0288（新スタックと
  型付きルート契約。**決定 6 が本作業の直接の起点**）、IADR-0290（`src/` 直下の層分け）、
  IADR-0321（SC-04 の新設）
- 本作業で起草した IADR: **IADR-0338**（`@platform/ui` / Tailwind / Lingui / lucide の受け入れと ui-stub）、
  **IADR-0339**（4 画面のモック適合・`role="alert"` の整理）

### 起点となった検討（**リポジトリに実体を持たない中間成果物。平文で引用しリンクを張らない**）

- **検討メモ 第 3 弾「UI 部品の現在地」（2026-09-12・別途送付）**: 本ユニットのフロントエンドには
  共有 UI（`@platform/ui`）・Tailwind・デザイントークン・アイコン（lucide）が**いずれも 1 つも無い**と
  指摘した。**新しく作るのではなく、基盤に既にあるものを使えば断絶は消える**というのが要点である。
- **検討メモ 第 4 弾「体験の穴」（2026-09-12・別途送付）**: 効く順に (1) 失敗時の再試行導線、
  (2) 待ち・空・エラーの三部品、(3) 画面単位のエラー境界、(4) 画面設計テンプレートの欄追加、
  (5) a11y の機械検査。あわせて**触ってはいけない良さ**として、再試行可否の判定・0 件と失敗の区別・
  確認ダイアログの初期フォーカス（取消側）・色だけに頼らない表示・役割で引く E2E を挙げた。

> **これら 2 本は質問票・作業指示書と同じ中間成果物であり、リポジトリに実体を持たない**（`CLAUDE.md`
> 「中間成果物」）。**裁定・決定の内容そのものは本書と IADR-0338 / IADR-0339 に残す** —— 送付物が
> 手元から失われても決定の根拠を追える状態にする。

### 利用者裁定（2026-09-12・確定。再議しない）

| # | 項目 | 裁定 |
| --- | --- | --- |
| 3 | i18n | **Lingui を導入する。ただし英訳は不要**（`ja` カタログのみ。`en` は ja へ倒す） |
| 4 | 実装の担い手 | Fable の使用を許可（2〜3 タスク限定。本作業の配線部分がその 1 つ） |
| 8 | 適合範囲 | **全画面をモックの構造へ合わせる**（本ユニットは 4 画面すべて） |

裁定 1（テーマ切替）・5（Dialog の土台に `@base-ui/react` を採る）・7（a11y を error で導入）は
**基盤側の作業**であり、本ユニットは基盤の成果物（`@platform/ui`）を使う側として受ける。

## 目的・背景

本ユニットの 4 画面は `MSP/ADR-0031` が定める採用技術のうち **Lingui・`@platform/ui`・orval** を
満たしていなかった。これは IADR-0288 決定 6 が「**単独リポジトリでは解決できない**」として
意図的に未達のまま残したものである（`@platform/ui` は基盤の pnpm workspace パッケージ
`workspace:*` であり、単独リポからは解決できない）。

**この前提は、基盤側が本作業と同日に共有 UI を拡充したことで変わった。** 解決手段は
「基盤へ依存を張る」ではなく「**`@foundation` と同じ二重性**」—— 合成時は実体、単独リポでは
`test/ui-stub` —— であり、これは IADR-0080 決定 2 が既に敷いた骨格の適用にすぎない。

同時に、検討メモ 第 4 弾が挙げた**失敗時の再試行導線が 1 つも無い**という穴を塞ぐ。従前の 4 画面は
`'loading' | 'ok' | 'notFound' | 'error'` 型の手書き union（**実測 12 個**）を画面ごとに持ち、
失敗は文言だけを出して**利用者が取り直す手段を持たなかった**。

## 母集合の引き直し（着手前・必須。キット規則 1〜8）

**依頼文の列挙は母集合ではない。** 引いた結果を以下に置く。**走査は実装済みコミット
（`9967ff1` 配線 / `04cf472` 適合）に対して行い、比較の基準は親コミット `3d35c7a` である。**

### 軸 1 —— 複製された `Section` ヘルパ（誤りの側＝`function Section` から引く）

```
git grep -n "function Section" <ref> -- frontend/src
```

| 時点 | ヒット | 内訳 |
| --- | --- | --- |
| `3d35c7a`（着手前） | **11** | 画面ファイル内の**複製 9 件** ＋ 共有部品の `export function Section` 1 件 ＋ 同ファイルのコメント 1 行 |
| `04cf472`（着地） | **2** | 共有部品の export 1 件 ＋ コメント 1 行（**複製 0 件**） |

複製 9 件の所在: `SettingsPage.tsx` / `MonitorParametersForm.tsx` / `RiskSettingsPage.tsx` /
`Stage1TradeCountForm.tsx` / `WatchlistForm.tsx` / `ControlStatusPage.tsx` /
`ShortSellingStatusSection.tsx` / `GatewayStateSection.tsx` / `OpendAuthPage.tsx`。

> **共有部品 `Section` の残存利用は 2 か所だけである**（`ShortSellingStatusSection.tsx` /
> `OpendAuthPage.tsx`）。残り 7 か所は `Section` へ寄せたのではなく **`@platform/ui` の `Panel` へ
> 置き換わった** —— モックの区画は `details` の折りたたみではなく `panel` だからである。
> 「9 → 1 へ集約した」と読むと**実態より単純に見える**ため、内訳をここに残す。

### 軸 2 —— 待ち・失敗の手書き分岐（誤りの側＝`'loading' |` の union から引く）

```
git grep -nE "'(loading|idle|ready)'\s*\|" <ref> -- frontend/src ':!*.test.tsx'
```

| 時点 | union の宣言 | 置き換え先 |
| --- | --- | --- |
| `3d35c7a` | **12 個**（7 ファイル） | —— |
| `04cf472` | **0 個** | `<QueryPhase>` の利用 **13 か所**（`git grep -o '<QueryPhase' -- frontend/src ':!*.test.tsx'`） |

> **分岐の「箇所数」は未計測である。** union の宣言数（12）と `QueryPhase` の利用数（13）は
> 機械的に数えられるが、三項演算子の連鎖として散っていた分岐の総数は、どこまでを 1 分岐と
> 数えるかで値が変わり**追試できない**。コミット本文が挙げた「35 箇所」はその意味で概数である。
> **本書は数えられた 2 つの値だけを述べる。**

### 軸 3 —— ARIA ロールの整理（誤りの側＝`role="note"` と過剰な `role="alert"` から引く）

行コメント・JSX コメント・テストファイルを除いた**属性としての出現**を数える。

| パターン | `3d35c7a` | `04cf472` | 備考 |
| --- | --- | --- | --- |
| `role="alert"` | 47 | **27** | 結果通知と運用上の割り込みに限定（IADR-0339 決定 2） |
| `role="status"` | 32 | **19** | 待ち・進捗の告知は `QueryPhase` 経由で三部品が持つ |
| `role="note"` | 1 | **0** | 🔴 **`note` は ARIA の有効なロールではない**（`SettingsPage.tsx` に 1 件）。`Note` 部品へ置換 |

> **除外を明示する**（規則 6）: `*.test.tsx` / `*.test.ts` は**検査する側**であり、期待値として
> 役割名を書く。ここを母集合へ入れると「テストを直せば数が減る」という誤った作業になる。
> 行コメント・JSX コメント内の出現も除いた（規約を説明する散文であり、画面には出ない）。

### 軸 4 —— インラインスタイルとトークン記法（誤りの側から引く）

| パターン | `3d35c7a` | `9967ff1` | `04cf472` | 備考 |
| --- | --- | --- | --- | --- |
| `style={{`（`frontend/src`） | 31 | 31 | **0** | Tailwind のクラス名へ移した |
| `style={{`（`frontend` 全体・lock 除く） | 32 | 32 | **2** | 残る 2 件は `test/foundation-stub/ui/NotFound.tsx` と `test/ui-stub/ProgressBar.tsx`。**いずれも test-only のスタブ**であり、合成時には実体（基盤側）が使われる。**除外の理由をここに残す** |
| `[--color-` | 0 | **2** | **0** | 🔴 **配線コミット `9967ff1` が `Section.tsx` に持ち込み、適合コミット `04cf472` で消した。** `text-[--color-x]` 記法は **Tailwind 4.3 で無効**であり、名前付きユーティリティ（`border-divider` / `bg-surface`）だけを使う（IADR-0338 決定 2） |

### 軸 5 —— 規則 8（自分の記録が母集合へ入る）

本書と IADR-0338 / IADR-0339 は `.ai-context/` 配下であり、**軸 1〜4 の走査対象（`frontend/src`）に
入らない**。よって本書を書く行為が上の数を動かすことはない。**走査基準は 3 つのコミット SHA で
固定してあり、追試できる。**

## 対象範囲

### 対象

- **配線**（コミット `9967ff1`）
  - `frontend/test/ui-stub/`（新設・23 ファイル）と `frontend/test/ui-stub/index.ts`（公開面）。
    `tsconfig.standalone.json` の `paths` ／ `vitest.config.ts` ・ `e2e/vite.harness.config.ts` の
    `alias` で `@platform/ui` をここへ解決する。`tsconfig.json`（合成時）は実体
    （`../../packages/ui/src`）を指す。
  - `frontend/lingui.config.ts`（`sourceLocale: 'ja'` / `locales: ['ja']` / `compileNamespace: 'ts'` /
    `POT-Creation-Date` を落とす決定的フォーマッタ）、`frontend/src/locales/ja/messages.{po,ts}`（生成物・コミット）。
  - `frontend/src/lib/i18n.ts`（`aiStockTradingMessages`）と `frontend/src/features/index.ts` からの公開。
  - `frontend/src/components/QueryPhase.tsx` ＋ `QueryPhase.test.tsx`、`frontend/src/components/Section.tsx`。
  - `frontend/package.json`（`lucide-react` / `@lingui/core` / `@lingui/react` ＋ devDeps ＋ `i18n` スクリプト）。
  - `frontend/test/setup.ts`（テストで ja を活性化）、`frontend/eslint.config.js`。
- **4 画面の適合**（コミット `04cf472`）
  - `frontend/src/features/sc0{1,2,3,4}-*/components/*.tsx`（10 ファイル）と各 `index.ts` / `routes/*.tsx`。
  - `frontend/src/components/ScreenHeader.tsx`（新設）・`PaperModeBanner.tsx`・`Section.tsx`。
  - `frontend/src/features/index.ts`（**パンくず宣言 `aiStockTradingBreadcrumbs` の公開**）と
    `frontend/test/foundation-stub/routing/featureRegistry.ts`（同型のスタブ）。
  - `frontend/e2e/harness/main.tsx`（E2E ハーネスで ja を活性化）。
- **記録**: 本仕様書・IADR-0338・IADR-0339・`.ai-context/adr/README.md` の索引・
  `docs/screens/*.md` 4 本への 1 節追記。

### 対象外（理由つき。黙って落とさない）

| 対象外 | 理由 |
| --- | --- |
| **Tailwind 本体の依存・ビルド** | 単独リポには CSS ビルドが無い。**クラス名は文字列として書き、合成時に基盤の Vite プラグインが拾う**（IADR-0338 決定 2）。AST に `tailwindcss` を入れると基盤と二重にビルドすることになる |
| **`@platform/ui` の依存宣言（`package.json`）** | `workspace:*` は基盤の pnpm workspace の中でしか解決できない。単独リポの `npm ci` が壊れる。解決は `paths` / `alias` の二重性で行う |
| **エラー境界（ErrorBoundary）** | **画面単位の境界は合成時に基盤のルータが張る**（第 4 弾 (4) は基盤側の作業）。ユニット側に置くと二重になる。E2E ハーネスにだけ最小の fallback を置く |
| **orval 生成フック** | 生成の入力は基盤の `docs/api/openapi.yaml` の `/bff/` 配下であり、本ユニットの端点はそこに載らない（IADR-0288 決定 3・IADR-0091）。**`MSP/ADR-0031` の 3 つのうち 1 つは未達のまま残る** |
| **`en` カタログ・英訳** | 利用者裁定 #3。`registerUnitMessages` が `en` へ `ja` を流す |
| **`@platform/ui` 側の部品追加・修正** | 別リポジトリ（microservices-platform）の所有物。本作業は**使う側**である |
| **a11y の機械検査（jsx-a11y）** | 裁定 #7 は基盤側の作業（`src/eslint.config.js` の新ブロック）。本ユニットは違反の側を直すだけである |
| **`lib/risk/contracts.ts` と表示規約の定数** | `METRIC_NOT_SUPPLIED_TEXT` 等は**供給が無い値の表示規約**（IADR-0162）の単一情報源であり、UI の作り替えで意味が動いてはならない。**1 行も触っていない** |
| **既存テストの `expect`** | 受け入れ基準の写像そのものである。**397 件・E2E 60 件を 1 件も書き換えずに緑**であることを本作業の条件とした |

## 設計

### 1. `@platform/ui` の二重解決（`@foundation` と同型）

| 面 | 解決先 | 設定箇所 |
| --- | --- | --- |
| 合成時（基盤の pnpm workspace 内） | 実体 `../../packages/ui/src` | `frontend/tsconfig.json` の `paths` |
| 単独リポの型検査 | `./test/ui-stub` | `frontend/tsconfig.standalone.json` の `paths` |
| 単独リポの単体テスト | `./test/ui-stub` | `frontend/vitest.config.ts` の `alias` |
| 単独リポの E2E ハーネス | `./test/ui-stub` | `frontend/e2e/vite.harness.config.ts` の `alias` |

**スタブの写し方の規律**（`test/ui-stub/index.ts` 冒頭に明記）: 各部品を素の HTML へ写し、
`role` / `aria-*` / テキストの出方を実物と一致させる。**実物が付けない `role` をスタブが付けない**
（付けると単独リポだけ緑で合成時に落ちる）。見た目（クラス名・アイコン・cva）は写さない。
**網羅の安全弁は基盤側の `pnpm -r run typecheck` である。**

### 2. Lingui（`ja` 単独）

- 抽出・コンパイルは**本リポジトリで完結**する（`npm run i18n`）。基盤の `src/lingui.config.ts` は
  本ユニットを抽出範囲に含めない（`MSP/IADR-0120`）。
- `POT-Creation-Date` はフォーマッタで落とす。**実行時刻が毎回書き込まれると再生成差分検査が常に赤になる**。
- 文言は ``i18n._(msg`…`)`` に統一し、`<Trans>` は使わない（IADR-0338 決定 4）。
- 🔴 **本番ビルドでは `msg` マクロが `message` を落とし ID（ハッシュ）だけを残す。** カタログが
  基盤の i18n に載っていないと**本番の画面にハッシュがそのまま出る**。開発・テストでは `message` が
  残るため気付けない —— 合成点への配線（`registerUnitMessages`）を外してはならない。

### 3. `QueryPhase`（待ち・失敗・空・本体の 1 か所）

判定順は **`isError` → `isPending` → `isEmpty` → 本体**。

- **失敗を先に見るのは 0 件と失敗を混同しないため**である。取得に失敗したのに「該当なし」と描くと、
  統制が働いていない画面と見分けがつかなくなる。
- `isEmpty` は**成功したデータに対してだけ**評価する。
- `role="status"` / `role="alert"` は三部品（`LoadingState` / `EmptyState` / `ErrorState`）が持ち、
  `QueryPhase` は重ねて付けない。
- **再試行可否は `QueryPhase` が判断しない** —— `canRetry` prop で受ける（既定 `true`）。
  404（BFF 未登録＝存在秘匿）は再試行しても直らないため画面側が `canRetry={false}` を渡す
  （実測 4 か所: SC-01 / SC-02 の 2 か所 / SC-03）。

### 4. 合成点へ渡す契約は 4 本になる

`frontend/src/features/index.ts` が公開するもの:

| 契約 | 公開名 | 合成点での受け |
| --- | --- | --- |
| ルート | `createAiStockTradingRoutes` | `createUnitRoutes` へスプレッド |
| ナビ | `aiStockTradingNavItems` | `unitNavGroups` の `items` |
| **パンくず**（新設） | `aiStockTradingBreadcrumbs` | `registerBreadcrumbs` |
| **文言カタログ**（新設） | `aiStockTradingMessages` | `registerUnitMessages` |

🔴 **4 本は独立している。** 片方だけ渡すと「画面は開けるのに帯が空」「本番だけハッシュが出る」
のような、**気付きにくい欠落**になる。

## 計画書との差異

**モックと計画本文が食い違うとき、計画本文（と既存テスト）を優先した**（IADR-0339 決定 4）。
以下 4 点はいずれも**環流の候補**である。

| # | 差異 | 採った側 | 理由 |
| --- | --- | --- | --- |
| ① | **SC-03 の維持率区画の位置**。hi-fi モック `sc-03.html` はこれを画面**下部**（`line 460`。3 統制・上限使用率・保有ポジション・段階ゲートの後ろ）に置くが、実装は**最上位**のまま | **計画本文** | 計画 `01_screens.md` が「**本画面の最上位に置く**」と 2 か所で明記し（`ADR-0016` 決定 15 由来）、E2E `sc03-controls.spec.ts`「維持率を画面最上位に置き…」が固定している。**モックの並びを採ると既存 E2E が赤くなる。**「マージンコールは口座を失う唯一の経路」という位置づけの指標であり、**下げる根拠が計画側に無い** |
| ② | **SC-01 の画面見出し**。モックは「取引システム設定」、実装は「設定」のまま | **既存の実装（＝ナビ・パンくずと一致）** | ナビ項目・パンくず・ルート（`/settings`）がすべて「設定」で、既存テストも `heading` で引く。**見出しだけをモックに寄せると、同じ画面が 2 つの名前で呼ばれる。** モック側の字面は装飾であり、計画本文は画面名を「設定画面」としている |
| ③ | **他画面への導線を `<a href>` で書いた**（`ScreenLink`）。モックの `<a href="sc-02.html">` に対応する | **素の `<a>`** | `@tanstack/react-router` の `Link` はルータ文脈が無いと例外を投げ、**画面単体テスト（`renderWithProviders`）が全滅する**。SPA 内では全読み込みになるが**遷移先は同じ画面に着く**（主たる導線は左レールのナビで、ここは補助）。🔴 **合成後に基盤側で `Link` へ差し替えるのが望ましい** —— 本ユニット単独では差し替えられない |
| ④ | **モックの「状態例」区画と `.badges` 行を実装しない** | **実装しない** | いずれも**モックが読み手へ説明するための装置**である。`.badges`（`AST SC-01` / `FR-17` / `UC-06` / `実装済 (PR #185)` / `仕様書 ↗`）は**画面の要素ではなくトレーサビリティの表示**であり、製品画面に計画 ID を出すことになる。「状態例」（SC-02 の equity 未取得時・SC-03 の空売り比率の 3 状態・段階ゲートの警告例）は**同じ画面の別状態を並べて見せる**もので、実行時には該当する 1 つだけが出る |

> ①②は**計画側の記述とモックの食い違い**であり、環流（planning への issue）で決着させるのが筋である。
> ③は**本ユニットからは直せない**（基盤側の合成の問題）。④は差異ではなく**モックの読み方**の問題であり、
> 環流は要らない。

## 受け入れ基準

- [x] **Given** 単独リポジトリ（基盤の workspace の外）で **When** `npm run typecheck` / `npm run lint` /
      `npm run e2e:typecheck` を実行する **Then** いずれも成功する（`@platform/ui` が `test/ui-stub` へ解決される）
- [x] **Given** 既存の単体テスト **When** `npm run test` を実行する **Then** **397 件（22 ファイル）が
      1 件も書き換えられずに緑**である
- [x] **Given** 既存の E2E **When** `npx playwright test` を実行する **Then** **60 件（4 ファイル）が
      1 件も書き換えられずに緑**である
- [x] **Given** コミット済みのカタログ **When** `npm run i18n`（extract → compile）を実行する
      **Then** `frontend/src/locales` に**差分が出ない**
- [x] **Given** 取得が失敗した領域 **When** 画面を描く **Then** 失敗の告知に**再試行ボタンが伴う**。
      ただし 404（BFF 未登録）では**再試行ボタンを出さない**
- [x] **Given** 取得が成功して 0 件 **When** 画面を描く **Then** **失敗と区別できる**（「該当なし」と
      「取得できていません」を取り違えない）
- [x] **Given** SC-03（参照専用画面）の正常系 **When** 画面を描く **Then** **ボタンが 1 つも無い**
      （再試行ボタンは失敗時にだけ現れる。`ControlStatusPage.readonly.test.tsx` と E2E が固定）
- [x] **Given** リポジトリ直下 **When** `node scripts/check-frontend-empty-frames.js` を実行する **Then** 成功する
- [x] **Given** 文書 **When** `check-trace-blocks` / `gen-knowledge-graph --check` / `check-cross-repo-refs` /
      `check-plan-id-qualification` / `check-doc-links` / `check-reading-budget` を実行する **Then** すべて成功する
- [x] **Given** 供給が無い値の表示規約（`lib/risk/contracts.ts` の定数群）**When** 差分を見る
      **Then** **1 行も変更されていない**

## テスト方針

- **新規部品には新規テストを書く**（`QueryPhase.test.tsx`。判定順・`canRetry=false`・空判定の陰性対照）。
- **既存の受け入れ基準は既存テストのまま守る。** 本作業は表示の器を替えるものであり、
  **`expect` を書き換えたらそれは退行の隠蔽である**。397 件・60 件が無変更で緑であることを
  作業の終了条件に置いた。
- **役割とテキストで引く** —— `getByRole` / `getByText`。クラス名・DOM 構造では引かない
  （ui-stub と実体でクラス名が違うため、引いた瞬間に合成時と食い違う）。

## 検証記録（2026-09-12 実測）

| 検証 | 結果 |
| --- | --- |
| `npm run typecheck`（`tsconfig.standalone.json`） | 成功 |
| `npm run lint` | 成功（指摘 0 件） |
| `npm run e2e:typecheck` | 成功 |
| `npm run test` | **397 passed（22 files）** |
| `npx playwright test` | **60 passed（4 files・58.0s）** |
| `npm run i18n` → `git diff -- frontend/src/locales` | **差分なし**（カタログ **442 件**） |
| `node scripts/check-frontend-empty-frames.js` | `frontend/src: 枠なし` |
| `node scripts/check-trace-blocks.js` | OK（45 件） |
| `node scripts/gen-knowledge-graph.js --check` | OK |
| `node scripts/check-cross-repo-refs.js` | OK |
| `node scripts/check-plan-id-qualification.js` | OK |
| `node scripts/check-doc-links.js` | OK |
| `node scripts/check-reading-budget.js` | OK（3 集合すべて 51,200 バイト内） |

> **合成ビルド（基盤の workspace 内での typecheck / build）は本書の範囲外**であり、基盤側の
> 検証で実測する。**単独リポで緑であることは合成時の緑を保証しない**（下記「残余リスク」）。

## 残余リスク

1. 🔴 **合成時の `@platform/ui` の解決は基盤側の設定に依存する。** pnpm は既定で依存を hoist しない
   ため、submodule として取り込まれた本ユニットから `@platform/ui` が見えない。基盤側
   `src/pnpm-workspace.yaml` の `publicHoistPattern` に `@platform/ui` を入れる必要があり、
   **これは基盤リポジトリの同日の PR に含まれる**。本ユニット単独では確かめられない。
2. **submodule の SHA を前進させると基盤の `pnpm-lock.yaml` が動く**（`lucide-react` ・ Lingui の
   追加ぶん）。本ユニットのマージ後に基盤側で lock を更新する手順が要る。
3. **ui-stub の網羅は単独リポでは検知できない。** スタブに在って実物に無い export（逆向き）は
   単独リポの型検査を通ってしまい、**合成時の typecheck / build が初めて落とす**。
   安全弁は基盤側の `pnpm -r run typecheck` である。実物の公開面が変わったら `test/ui-stub/index.ts`
   の一覧を揃える（2026-09-12 時点の実物と一致）。
4. **`ScreenLink` の `<a href>` は SPA 内で全読み込みになる**（差異③）。合成後に基盤側で `Link` へ
   差し替えるのが望ましい。**外す条件**: 基盤の合成点が、ルータ文脈の有無に依らず使える遷移部品を
   公開したとき。
5. **orval 生成フックは依然として未達である**（IADR-0288 決定 6 の 3 つのうち 1 つが残る）。
   本ユニットの BFF 端点が基盤の `openapi.yaml` に載らない限り解消しない。

## 未決事項

- 差異①②を計画へ環流するか（モック側を直すか、計画本文の「最上位」を維持したままモックを直すか）は
  **利用者の裁定事項**である。本作業では**計画本文を優先して実装し、差異を記録した**（IADR-0339 決定 4）。
