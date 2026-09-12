---
title: IADR-0340 文言カタログの登録を画面の遅延チャンク側へ移し、合成時の初期ロードから外す
type: impl-adr
status: Accepted
related_ids: [SC-01, SC-02, SC-03, SC-04, ADR-0001, IADR-0338, IADR-0288, IADR-0080]
author: endazon (with Claude Code)
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# IADR-0340: 文言カタログの登録を画面の遅延チャンク側へ移し、合成時の初期ロードから外す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: endazon（[#792](https://github.com/endazon/ai-stock-trading/issues/792) への実装判断）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: SC-01 / SC-02 / SC-03 / SC-04、ADR-0001（基盤の再利用）。
  非機能は `NFR`（初期ロード量。無採番＝`traceability.repo.md`「`NFR` はレンジを持たない」）
- 基盤（microservices-platform）の計画 ADR: `MSP/ADR-0031`（フロントエンド採用技術＝Lingui）
- 基盤の実装 ADR: `MSP/IADR-0134`（画面はルート単位の遅延チャンクへ分ける）、
  `MSP/IADR-0124` 決定 1（型付きルート factory）、`MSP/IADR-0125` 決定 3（共有 UI と i18n カタログ）、
  `MSP/IADR-0120`（基盤から本ユニットは直せない。逆も同じ）
- 先行する本リポの IADR: IADR-0080（単独リポの自己完結と `@foundation` スタブ）、
  IADR-0288（型付きルート契約）、**IADR-0338 決定 3**（`ja` 単独カタログ。**本 IADR が一部を supersede する**）
- 関連する実装仕様書: `.ai-context/specs/20260912_792_unit-catalog-lazy-registration.md`
- 起点 issue: [#792](https://github.com/endazon/ai-stock-trading/issues/792)

## コンテキストと課題

#791（4 画面を `@platform/ui` と Lingui へ載せ替え）を基盤へ合成した結果、基盤の初期ロード合計が
**705,386 B → 731,293 B（+25,907 B）** へ増えた（基盤 `scripts/chunk-budget-baseline.json`）。

issue #792 と基盤側の記録は、この増分を「**本ユニットの route factory が `component:` へ画面を
直接渡しているため、4 画面の本体が初期チャンクに入っている**」と帰属し、knowledge ユニットと同じ
`lazyRouteComponent` 方式への載せ替えを求めた。

**この帰属は事実に反する。** 実測（すべて `7780edc` および比較基準 `3d35c7a` に対して追試できる）:

1. **4 画面は既に `lazyRouteComponent` 方式である。** `git grep -c "lazyRouteComponent"` は
   `3d35c7a`（bump 前）・`7780edc`（bump 後）の**どちらでも 4 ファイルすべてで 2**。導入は
   #723（`a5e2fe6`）/ #691（`9324f41`）であり、**bump で形は変わっていない。**
   ガードは `GuardedSc0N…`（`RequireRole`）として**同期のまま外側**に在り、`wrapInSuspense: true` も
   付いている——**issue が手本に挙げた基盤 `sc17UsersRoute.tsx` と同一の形**である。
2. **合成ビルドの成果物でも画面は遅延側に在る。** 基盤の `dist`（submodule = `7780edc` の実ビルド。
   初期ロード合計が床 731,293 B と 1 バイト一致）で、`index.html` が読み込むのは 5 本
   （`index` 296,953 / `vendor-react` 196,828 / `vendor-baseui` 114,124 / `ui` 77,786 / `vendor-query` 45,602）。
   4 画面は**独立した遅延チャンク**で、`index.html` から参照されない:
   `SettingsPage` 8,538 / `RiskSettingsPage` 32,960 / `ControlStatusPage` 16,158 / `OpendAuthPage` 8,526
   （計 66,182 B）。**画面本体の初期ロードへの寄与は 0 B。**
3. **増分の実体は Lingui のカタログである。** `index-*.js` 内の `JSON.parse('…')` を全数走査すると
   literal は 2 本で、**821 キー / 46,087 B（基盤の ja＋en）** と **442 キー / 25,267 B（本ユニットの ja）**。
   **25,267 / 25,907 = 97.5%** が本ユニットのカタログ 1 本で説明される（残り約 640 B が lucide・
   ナビ／パンくず等）。

課題は「画面をどう遅延させるか」ではなく、**IADR-0338 決定 3 が敷いた配線——`src/features/index.ts`
（＝基盤の合成点が静的 import する公開面）からカタログを再公開し、合成点が同期で
`registerUnitMessages` を呼ぶ——が、カタログを構造的に初期チャンクへ載せてしまうこと**である。

**本ユニットのナビ・パンくずのラベルは素の文字列であり `msg` マクロを使っていない。**
よって 442 文言のうち**初回描画までに要るものは 1 つも無い。**

## 検討した選択肢

| 案 | 内容 | 判定 |
| --- | --- | --- |
| **A** | **登録を `src/lib/i18n.ts` のモジュール評価へ移し、import するのを 4 画面の Page（遅延チャンク）だけに閉じる** | **採用** |
| B | 基盤へ「カタログのローダ（`() => import(...)`）を受け取る `registerUnitMessages`」を足してもらう | 不採用。**基盤の API 改修を待つ**（`MSP/IADR-0120`: 本リポからは直せない）。本ユニット側だけで同じ効果が出る |
| C | 本ユニットが `i18n.load` を直接呼ぶ | 不採用。`en` フォールバックの意味論（未登録 ID だけ ja を流す＝**基盤の英訳を日本語で上書きしない**）が基盤側にしかなく、**写すと 2 か所に持つ**ことになる |
| D | カタログを画面別に 4 分割する | 不採用。効果は「1 画面しか開かない利用者が他画面の文言を引かない」だけで、4 画面は**同一の利用者（`trading-owner`）が使う**。複雑さに見合わない |
| E | 何もしない（issue を「前提の誤り」として閉じる） | 不採用。**帰属は誤りだが、初期ロードを減らせるという issue の意図は正しい** |

## 決定

### 決定 1 — 4 画面の `lazyRouteComponent` 化は**既済**であり、増分の帰属は誤りである

issue #792 の作業項目 1 に対応する変更は**無い**。上の実測 1〜3 を本 IADR と仕様書に残し、
**「載せ替えた」という記録を作らない**（実体の無い変更を記録すると、次に同じ調査をする者が
「対処済みなのにまだ重い」と読む）。

**基盤側の記録（`chunk-budget-baseline.json` の `$comment_initialTotalBytes_20260912_ast-bump` と
合成点のコメント）は本リポジトリからは直せない**（`MSP/IADR-0120`）。訂正の依頼は下の残余リスクへ。

### 決定 2 — カタログの登録を `src/lib/i18n.ts` へ移し、import を 4 画面の Page だけに閉じる

- `src/features/index.ts` からの `aiStockTradingMessages` の**再公開をやめる**（空カタログでも残さない）。
  基盤の合成点は `if (astOptionalSurface.aiStockTradingMessages)` の**任意項目読み**なので、
  **基盤を 1 バイトも変えずに skip する**（順序非依存。合成点のコメント自身が意図した形である）。
- `src/lib/i18n.ts` がモジュール評価時に `registerUnitMessages({ ja })` を**1 回だけ**呼ぶ
  （`registerAiStockTradingMessages()`。ES モジュールは 1 度しか評価されないが、
  **テストが `vi.resetModules()` でレジストリを分ける**ためフラグで明示的に閉じる）。
- 4 画面の Page は `i18n` を**`@ai-stock-trading/lib/i18n` から**受け取る（`@lingui/core` から直接取らない）。
  こうすると**「文言を描く入口」と「カタログの登録」が同じ import で結ばれ、片方だけ消せない。**
- **Page の子部品**（`QueryPhase` / `PaperModeBanner` / 各 Form 等・8 件）は `@lingui/core` のままでよい。
  **必ず Page を経由して描かれる**ため描画時点で登録済みであり、入口を 4 か所に閉じるほうが
  「どの import が不変条件を担っているか」がはっきりする。
- **ルート factory・ナビ・パンくず・`src/features/index.ts` から `lib/i18n` を import しない。**
  1 本でも静的辺ができた瞬間にカタログは初期ロードへ戻る——**ビルドは成功し、誰も気付かない。**

順序は保たれる: `@foundation/i18n` はモジュール評価時に基盤カタログを `load` / `activate` 済みで、
`lib/i18n.ts` はそれを import してから登録する。Page は動的 import の**解決後**に描画されるので
描画時点で必ず登録済みであり、`i18n._()` は呼び出し時点の表を引く（**再描画は要らない**）。

### 決定 3 — 単独リポは `@foundation/i18n` を `test/foundation-stub/i18n.ts` へ解決する

`@foundation` と同型の二重性（IADR-0080 決定 2）を i18n にも張る。

- **`paths` を足すのは `frontend/tsconfig.json`（合成時の向き先）だけ**である。
  `tsconfig.standalone.json` は `@foundation/*` の**ワイルドカード**、`vitest.config.ts` と
  `e2e/vite.harness.config.ts` は `@foundation` の**prefix alias** で既に解決する。
  **4 か所すべてに足すのは誤り**——同じことを 2 か所で言うと、片方だけ直る事故の種になる。
- スタブは「渡されたカタログが `i18n` に載る」ことだけを担い、**実体の `en` フォールバックは写さない**
  （単独リポは `en` カタログを持たず、写しても検証できる差が無い。**スタブにだけ在る挙動**は
  単独で緑・合成で赤の原因になる。`test/ui-stub/index.ts` 冒頭の規律と同じ）。

### 決定 4 — E2E ハーネスは自前で `load` せず、活性化だけを残す

ハーネスが `i18n.load('ja', messages)` を続けると、**登録が壊れても E2E が緑のまま**になる。
`i18n.activate('ja')` だけを残す（Lingui はロケール未活性だと `i18n._()` が例外を投げる）。

🔴 **ただし E2E は登録の破れを捕まえない**（実測）。ハーネスは `vite dev` で動き、開発ビルドでは
`msg` マクロが `message` を残すため、**カタログが無くても日本語が出る**。
**登録を止めて `sc03-controls.spec.ts` を走らせたところ 10/10 が通った。**
→ **不変条件を守るのは下の単体テストであり、E2E ではない。** この非対称を明示する。

### 決定 5 — 不変条件を単体テストで固定する（ESLint 規則は足さない）

`src/features/catalogRegistration.test.ts` が 4 画面それぞれについて、
**`vi.resetModules()` でレジストリを分けたうえで Page モジュールを単独で評価し**、
`registerUnitMessages` が `ja` カタログ付きで 1 回呼ばれることを固定する。あわせて
**合成点（`features/index.ts`）を評価しても 1 回も呼ばれないこと**（＝初期ロードへ引き込んでいないこと）と、
**母集合の一致**（route factory の実ソースから `lazyRouteComponent` の動的 import 先を抜き、
テストが試す 4 本と過不足なく一致すること）を固定する。

**ESLint 規則（「Page で `@lingui/core` の `i18n` を使わせない」）は足さない。** 運用標準
「**検査器・規約の追加は同型の事故が 2 回起きたら**」——**1 回目である**。テストは規約ではなく
事実の固定なので、この制限には当たらない。

変異試験（実測）:

| 変異 | 結果 |
| --- | --- |
| SC-01 の Page を `@lingui/core` へ戻す | **1 失敗 / 5 合格**（当該画面だけが落ちる） |
| 合成点へ `lib/i18n` からの再輸出を足す | **1 失敗 / 5 合格**（「初期ロードへ引き込まない」が落ちる） |
| 登録を止めて E2E（`sc03-controls`）を走らせる | **10 合格＝捕まらない**（決定 4 の 🔴） |

## 理由

- 決定 2 が**基盤を一切変えずに済む**のは、基盤の合成点が AST の公開面を**任意項目として読む**形で
  書かれているためである。これは「submodule の前進より先に合成点が develop へ入る」順序問題への
  対処として基盤側が意図的に採った形であり、**その性質をそのまま使う。**
- `registerUnitMessages` を**通し続ける**（案 C を採らない）ことで、`en` フォールバックの
  意味論は基盤の 1 か所に残る。**本ユニットが変えたのは「いつ呼ぶか」だけで、「何をするか」ではない。**
- 決定 5 が **E2E ではなく単体テスト**に寄るのは、決定 4 の実測（E2E は開発ビルドなので捕まらない）に
  基づく。**「E2E が緑だから大丈夫」と書かない**ために、捕まらないことを測って記録した。

## 結果

- **良い影響**:
  - 合成時の初期ロードから **25,267 B（442 キー）** が外れる（実測は仕様書 §検証記録 B）。
  - 「合成点の公開面から辿れるものは初期チャンクに入る」という**構造の性質が明文化され、
    機械で守られる**ようになった（決定 5 の 4 つ目のテスト）。
- **悪い影響・トレードオフ**:
  - **本ユニットの画面を初めて開く利用者は 25 kB を遅延で引く**（4 画面共有の 1 チャンク）。
    基盤の利用者の大多数（knowledge しか使わない）は**払わなくなる**——この交換を受け入れる。
  - **Page の import 元が子部品と非対称**になった（Page は `lib/i18n`、子部品は `@lingui/core`）。
    決定 2 の理由をコメントとして各 Page に残し、テストで固定した。
- **フォローアップ（基盤側への依頼。本リポジトリからは直せない）**:
  - 🔴 **合成点の「bump 後に名前付き import へ戻してよい」というコメントは撤回してもらう必要がある。**
    戻された時点で、再公開をやめた本ユニットに対して**基盤の tsc が落ちる**。
    **任意項目読みを維持する**ことを基盤側の記録（コメントと該当 IADR）へ残してもらう。
  - `chunk-budget-baseline.json` の `$comment_initialTotalBytes_20260912_ast-bump` の
    **帰属（A＝画面本体）は誤り**である。上の実測 1〜3 で訂正してもらう。
  - 床の更新（初期ロードの減少分）は**基盤の bump PR で行う**（本リポジトリでは測れない値ではないが、
    床を持っているのは基盤である）。

## 関連

- Supersedes: **IADR-0338（決定 3 のうち「`aiStockTradingMessages` を合成点へ公開し、合成点が
  `registerUnitMessages` を呼ぶ」という配線の部分のみ）**。
  **`ja` 単独カタログで英訳を持たないこと・`en` へ ja を流すこと・`POT-Creation-Date` の扱いは現行のまま。**
- Superseded by: なし
- 併走: なし
