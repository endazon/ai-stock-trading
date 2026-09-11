---
title: IADR-0338 基盤の共有 UI・Lingui・lucide を受け入れ、単独リポでは test/ui-stub へ解決する
type: impl-adr
status: Accepted
related_ids: [SC-01, SC-02, SC-03, SC-04, ADR-0001]
author: endazon (with Claude Code)
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# IADR-0338: 基盤の共有 UI・Lingui・lucide を受け入れ、単独リポでは test/ui-stub へ解決する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: endazon（利用者裁定 2026-09-12 #3・#4・#8 に従う実装判断）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: SC-01 / SC-02 / SC-03 / SC-04、ADR-0001（基盤の再利用）
- 基盤（microservices-platform）の計画 ADR: `MSP/ADR-0031`（フロントエンド採用技術）、
  `MSP/ADR-0066`（feature 分離と import の向き）
- 基盤の実装 ADR: `MSP/IADR-0120`（基盤の Lingui 抽出範囲に可変ユニットを含めない）、
  `MSP/IADR-0121`（SPA スタック移行の段・生成物のコミット）、
  `MSP/IADR-0125`（共有 UI パッケージ `@platform/ui` の射程・i18n カタログ）
- 先行する本リポの IADR: IADR-0080（単独リポの自己完結と `@foundation` スタブ）、
  IADR-0288（新スタックと型付きルート契約。**決定 6 を本 IADR が supersede する**）、
  IADR-0290（`src/` 直下の層分けと `@ai-stock-trading/...` エイリアス）
- 関連する実装仕様書: `.ai-context/specs/20260912_frontend-platform-ui-and-lingui.md`
- 起点となった検討: **検討メモ 第 3 弾「UI 部品の現在地」（2026-09-12・別途送付）** ——
  本ユニットには共有 UI・Tailwind・トークン・アイコンが 1 つも無く、**基盤に既にあるものを使えば
  断絶は消える**と指摘した。**リポジトリに実体を持たない中間成果物であり、リンクは張らない。**

## コンテキストと課題

`MSP/ADR-0031` は基盤 SPA の採用技術として React 19 / TanStack Router / TanStack Query /
Tailwind CSS v4 / **Lingui** / **`@platform/ui`** / orval を定める。本ユニットはその前 5 つを
IADR-0288 で満たしたが、**Lingui・`@platform/ui`・orval の 3 つは未達のまま残した**
（IADR-0288 決定 6）。理由は「**単独リポジトリでは解決できない**」——`@platform/ui` は基盤の
pnpm workspace パッケージ（`workspace:*`）であり、orval の入力は基盤の OpenAPI である。

結果として本ユニットの画面は**素の HTML 要素と日本語直書き**であり、基盤の画面と並べたときに
見た目・文言の扱い・部品の語彙がすべて食い違っていた。

**IADR-0288 決定 6 が置いた前提は 2 点で崩れた。**

1. **`@platform/ui` は「解決できない」ものではない。** IADR-0080 決定 2 が `@foundation` に対して
   既に敷いた**二重性**（合成時は実体・単独リポではスタブ）を、そのまま `@platform/ui` へ
   適用できる。パッケージを依存として宣言せず、`paths` / `alias` で解決先を切り替えればよい。
2. **Lingui は単独リポで完結する。** 基盤の抽出範囲は本ユニットを含まない（`MSP/IADR-0120`）ため、
   本ユニットが自前の `lingui.config.ts` を持ち、自分のカタログを生成し、**合成点へ渡す**だけで足りる。

残る orval だけが真に「単独リポでは解決できない」（本ユニットの BFF 端点が基盤の
`docs/api/openapi.yaml` に載らないため。IADR-0091）。

さらに利用者裁定（2026-09-12 #3）が **「Lingui を導入するが英訳は不要」** と定めた。
英訳を伴わない i18n をどう成立させるかを決める必要がある。

## 検討した選択肢

| # | 案 | 解決するか | 却下理由 |
| --- | --- | --- | --- |
| A | **`paths` / `alias` の二重性 ＋ `test/ui-stub`**（採用） | する | —— |
| B | `@platform/ui` を **npm の git 依存**（`github:endazon/microservices-platform#…`）として宣言する | する | **planning への依存の再導入と同型の事故**である。参照先のコミットを pin することになり、鮮度の管理が要る。基盤は同一の親リポジトリに submodule として本ユニットを抱えており、**同じ木の 2 つの版を同時に持つ**状態になる |
| C | `@platform/ui` を **npm レジストリへ公開**する | する | 基盤リポジトリの意思決定であり、本ユニットからは決められない。公開すれば版の固定・公開面の安定化・リリース手順が要り、**本作業の射程を大きく超える**。IADR-0288 決定 6 が「別 issue が要る」と述べたのはこの案である |
| D | 基盤の**隣接クローン**（`../microservices-platform`）を `paths` で参照する | しない | **単独リポの CI に基盤のクローンを要求する**（現在は要求していない）。隣接クローンの版を固定する手段が無く、B と同じ鮮度の問題を持つ |
| E | 共有 UI を**本ユニットで作り直す** | する | ADR-0001（基盤の再利用）に正面から反する。**同じ部品が 2 つになり、片方が必ず古くなる** |

i18n の英訳について:

| # | 案 | 却下理由 |
| --- | --- | --- |
| a | **`ja` 単独カタログ ＋ 合成点で `en` に `ja` を流す**（採用） | —— |
| b | `en` カタログを空で持つ | **空の翻訳は本番でキーのハッシュを出す**（下記の落とし穴）。空とは「翻訳が無い」であって「日本語を出す」ではない |
| c | `en` カタログへ日本語をそのまま書く | 「英訳した」という嘘の記録が残る。**英訳を始めるときに何が未訳かを機械で数えられなくなる** |

## 決定

### 決定 1 — `@platform/ui` を使う。実体は合成時に解決し、単独リポは `test/ui-stub` へ差し替える

`package.json` に `@platform/ui` を**宣言しない**（`workspace:*` は単独リポで解決できない）。
解決は 4 つの設定で行う。

| 面 | 解決先 | 設定箇所 |
| --- | --- | --- |
| 合成時（基盤の pnpm workspace 内） | 実体 `../../packages/ui/src` | `frontend/tsconfig.json` の `paths` |
| 単独リポの型検査 | `./test/ui-stub` | `frontend/tsconfig.standalone.json` の `paths` |
| 単独リポの単体テスト | `./test/ui-stub` | `frontend/vitest.config.ts` の `alias` |
| 単独リポの E2E ハーネス | `./test/ui-stub` | `frontend/e2e/vite.harness.config.ts` の `alias` |

**これは `@foundation` スタブと同型である**（IADR-0080 決定 2）。新しい骨格を発明していない。

**スタブの写し方の規律**（`test/ui-stub/index.ts` の冒頭に置く）:

- **各部品を素の HTML へ写し、`role` / `aria-*` / テキストの出方を実物と一致させる。**
  テストは役割とテキストで引くためである。**見た目（クラス名・アイコン・cva）は写さない。**
- 🔴 **実物が付けない `role` をスタブでも付けない**（例: `EmptyState` / `Note`）。
  スタブが余計な役割を持つと、**単独リポのテストだけが通って合成時に落ちる**。
- **文言を持たない**（実物と同じ。`MSP/IADR-0125` 決定 1）。

🔴 **網羅の安全弁は基盤側の `pnpm -r run typecheck` である。** 実物の公開面に在ってスタブに無い
export を使えば単独リポの型検査が落ちるので気付ける。**逆（スタブに在って実物に無い）は単独リポでは
気付けず、合成時の typecheck / build が初めて落とす。** 実物の公開面が変わったらスタブの一覧を揃える。

### 決定 2 — Tailwind のクラス名は文字列で書き、AST は tailwind を依存に持たない

単独リポには CSS ビルドが無い（本ユニットは feature ユニットであり、実行アプリを持たない）。
クラス名は**ただの文字列**として書き、**合成時に基盤の Vite プラグインが走査して拾う**。
基盤側は `packages/ui/src/styles.css` の `@source` に本ユニットの `src` を含める。

- **`tailwindcss` を本ユニットの依存に入れない。** 入れると基盤と二重にビルドすることになる。
- 🔴 **`text-[--color-x]` の任意値記法は Tailwind 4.3 で無効である。** **名前付きユーティリティだけを
  使う**（`border-divider` / `bg-surface` / `text-fg` / `text-accent` / `text-danger`）。
  本作業の配線コミットは `Section.tsx` に `border-[--color-divider]` / `bg-[--color-surface]` を
  一度持ち込んでおり（実測 2 件）、適合コミットで名前付きへ直した。**無効な記法は黙って何も
  当たらない**——赤くならないため、レビューで気付けない種類の誤りである。

### 決定 3 — Lingui は `ja` 単独カタログとし、英訳を持たない。カタログは合成点へ公開する

- `frontend/lingui.config.ts`: `sourceLocale: 'ja'` / `locales: ['ja']` / `compileNamespace: 'ts'`。
  抽出・コンパイルは `npm run i18n` で**本リポジトリ内に完結**する（基盤の抽出範囲は本ユニットを
  含まない。`MSP/IADR-0120`）。生成物（`src/locales/ja/messages.{po,ts}`）は**コミットする**
  （基盤の生成物と同じ作法。`MSP/IADR-0121` 決定 3）。
- `@lingui/format-po` は **`POT-Creation-Date` に実行時刻を毎回書き込む**。内容が変わらなくても
  バイト列が変わり、「再生成に差分が出ないこと」の検査が常に赤になる。無効化するオプションは
  無いため、**serialize の出力から当該行を落とす決定的フォーマッタ**を挟む。
  **固定の日時を書かない**（抽出日時は意味を持つ情報であり、嘘の値を残さない）。
- `src/lib/i18n.ts` が `aiStockTradingMessages: { ja }` を組み立て、`src/features/index.ts` が
  再公開する。基盤の合成点が `registerUnitMessages(aiStockTradingMessages)` を呼び、
  **与えられたロケール（ja）を追加ロードし、与えられていないロケール（en）には ja を流す。**
  本ユニットの画面は en ロケールでも日本語で出る。
- **CI の検査所在**（PR #791 のレビュー指摘への追随）: `@lingui/cli@6.x` は `engines.node >=22.19` を
  要求するため、`.github/workflows/ci.yml` の `frontend` / `frontend-e2e` ジョブと `.nvmrc` を
  **Node 22** へ揃え（合成先の基盤も 22）、`frontend/package.json` に `engines.node` を宣言した。
  `frontend` ジョブは `npm run i18n` 後に `git diff --exit-code -- frontend/src/locales` で
  **再生成差分が無いこと**を検査する（基盤の `check-i18n-catalogs` に相当する差分検査。未訳検査は
  ja 単独のため不要）。

🔴 **本番ビルドでは `msg` マクロが `message` を落とし、ID（ハッシュ）だけを残す**
（`@lingui/babel-plugin-lingui-macro` の `descriptorFields: 'auto'` ＝ production では `id-only`）。
**カタログが基盤の i18n に載っていないと、本番の画面にハッシュがそのまま出る。**
開発・テストでは `message` が残るため気付けない。**合成点への配線を外してはならない。**

### 決定 4 — 文言は `i18n._(msg…)` に統一し、`<Trans>` を使わない

すべての表示文言を ``i18n._(msg`…`)`` の形で書く。`<Trans>` コンポーネントは使わない。

- **理由 1**: 文言が**文字列として得られる**ため、`aria-label` / `title` / `placeholder` / 配列の
  ラベル表など、**React 要素を置けない場所と同じ書き方で済む**。2 つの書き方が混ざると、
  「ここは `<Trans>` が使えないから直書き」という抜け道が育つ。
- **理由 2**: 既存テストは役割とテキストで引く。`<Trans>` は要素を挟むため、**テキストの分断**
  （`getByText` が部分一致で外れる）が起こり得る。**`expect` を書き換えない**という本作業の条件と
  相性が悪い。
- **`renderWithProviders` を変更しない。** ロケールの活性化は `test/setup.ts`（単体）と
  `e2e/harness/main.tsx`（E2E）で行う。

### 決定 5 — lucide-react を依存に持つ

アイコンは `lucide-react` を**本ユニットの依存として宣言する**（`@platform/ui` と違い、
通常の npm パッケージであり単独リポで解決できる）。版は合成時に基盤と同一へ hoist される
前提で幅を持たせる。**外部 CDN・Web フォントからアイコンを取らない**（基盤のデータ持ち出し
方針に従う）。

### 決定 6 — `QueryPhase` は薄いローカル部品として置く（`@foundation/ui/QueryState` は使えない）

基盤には同じ役割の `@foundation/ui/QueryState` があるが、**`@foundation` スタブに無く単独リポから
見えない**。よって `@platform/ui` の三部品（`LoadingState` / `EmptyState` / `ErrorState`）と
`query.refetch` を直接つなぐ**薄いローカル部品** `src/components/QueryPhase.tsx` を置く。

- **判定順は `isError` → `isPending` → `isEmpty` → 本体**（順を変えてはならない）。
  **失敗を先に見るのは 0 件と失敗を混同しないため**である。
- **再試行可否は部品が判断しない** —— `canRetry` prop で受ける（既定 `true`）。
  404 が再試行で直らないという判断は**画面側の知識**であり、部品に埋めると画面ごとの事情を
  部品が抱え込む。
- `role` は三部品の側が持ち、`QueryPhase` は重ねない。
- **エラー境界は置かない。** 画面単位の境界は合成時に基盤のルータが張る。E2E ハーネスにだけ
  最小の fallback を置く。

> **`QueryState` がスタブへ来たら本部品は不要になる。** **外す条件をここに書く** ——
> `@foundation` スタブが `QueryState` を持ち、`canRetry` 相当で再試行可否を外から受けるように
> なったとき、`QueryPhase` は削除して差し替える。

### 決定 7 — IADR-0288 決定 6 のうち 2 つを supersede する

IADR-0288 決定 6（Lingui・`@platform/ui`・orval を採らない）は、本 IADR の決定 1〜4 が
**Lingui と `@platform/ui` について置き換える**。**orval は引き続き未達**である
（本ユニットの BFF 端点が基盤の OpenAPI に載らない限り解消しない。IADR-0091）。

IADR-0288 には `Superseded by IADR-0338（決定 6 のみ）` を追記する。**本文プロズは書き換えない**
（凍結記録。当時の判断は当時の前提の下で正しかった）。

## 理由

- 決定 1 は**新しい仕組みを増やさない**。`@foundation` で既に運用している二重性の 2 例目であり、
  破れ方（スタブと実物の乖離）も既知で、安全弁も既にある。代案 B / C / D はいずれも
  **参照先の版を管理する責務**を新たに生む。
- 決定 2 は「本ユニットはビルドを持たない feature ユニットである」という既存の性質をそのまま使う。
  Tailwind を本ユニットへ入れると、**単独リポで CSS をビルドできてしまうがそれは合成時の
  見た目と一致しない**——「単独で緑だが合成で違う」を最も作りやすい形である。
- 決定 3 の a 案（en に ja を流す）を採るのは、**「未訳である」という事実を壊さないため**である。
  カタログに `ja` しか無いことが、そのまま「英訳していない」という記録になる。
  英訳を始めるときは `locales` に `en` を足すだけでよく、**そのとき初めて未訳の数が数えられる**。
- 決定 6 の `canRetry` は、検討メモ 第 4 弾が挙げた**触ってはいけない良さ**の 1 つ
  （再試行可否の判定が画面の知識である）をそのまま守る形である。

## 結果

- **良い影響**:
  - `MSP/ADR-0031` の採用技術に対する未達が **3 → 1**（orval のみ）へ減った。
  - 基盤と同じ部品語彙（`Panel` / `Kv` / `Stat` / `Table` / `Note` / `StatusBadge` / `Alert` /
    三部品）で画面が書けるようになり、**見た目の断絶が消えた**。
  - 文言がカタログへ集約され、**表示文言の一覧が機械で取れる**ようになった（実測 442 件）。
  - 失敗に再試行導線が付いた（決定 6 ＋ IADR-0339 決定 3）。
- **悪い影響・トレードオフ**:
  - **スタブの保守が要る**。実物の公開面が動いたら追随する。追随漏れは**合成時にしか落ちない**。
  - 依存が増える（`lucide-react` ・ `@lingui/core` ・ `@lingui/react` ＋ devDeps 3 つ）。
    submodule の SHA を前進させると**基盤の `pnpm-lock.yaml` が動く**。
  - 単独リポのテストは**スタブに対して**通っているにすぎない。合成時の見た目は別途確かめる必要がある。
- **フォローアップ**:
  - 🔴 **合成時に本ユニットから `@platform/ui` が解決できるかは基盤側の設定に依存する。**
    pnpm は既定で hoist しないため、基盤 `src/pnpm-workspace.yaml` の `publicHoistPattern` に
    `@platform/ui` が要る。**基盤側の同日の PR に含まれる。**
  - orval の未達は残る。解消には本ユニットの BFF 端点を基盤の OpenAPI へ載せる別の決定が要る。
  - `QueryPhase` は `QueryState` がスタブへ来たら撤去する（決定 6 の「外す条件」）。

## 関連

- Supersedes: IADR-0288（**決定 6 のうち Lingui と `@platform/ui` に関する部分のみ**。
  orval に関する部分と他の決定 1〜5 は現行のまま）
- Superseded by: なし
- 併走: IADR-0339（本 IADR が敷いた部品語彙で 4 画面をモックへ合わせる決定）
