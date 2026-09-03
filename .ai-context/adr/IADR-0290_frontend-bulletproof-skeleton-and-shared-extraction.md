---
title: IADR-0290 フロントエンドの src/ 直下を計画ツリーへ揃え、features/ 配下の非 feature を shared 層へ出す
type: impl-adr
status: Accepted
related_ids: [SC-01, SC-02, SC-03, FR-12, FR-13, FR-17, FR-19, FR-20, UC-06]
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md
  - planning:projects/microservices-platform/07_adr/ADR-0067_frontend-layer-classification-and-composition-point.md
  - planning:projects/microservices-platform/07_adr/ADR-0069_frontend-scaffolding-frames-and-absence-semantics.md
---

# IADR-0290: `src/` 直下を計画ツリーへ揃え、`features/` 配下の非 feature を shared 層へ出す

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: endazon（`MSP/ADR-0066` / `MSP/ADR-0067` / 計画 `13_frontend-stack` §ディレクトリ構成 に従う実装判断）

## 起点・関連

- 関連する計画書 ID: SC-01 / SC-02 / SC-03、FR-12 / FR-13 / FR-17 / FR-19 / FR-20、UC-06
- 基盤（microservices-platform）の計画: `13_frontend-stack` §ディレクトリ構成（`status: fixed`）、
  `MSP/ADR-0066`（feature 間 import の禁止・依存の向き・ESLint による機械強制）、
  `MSP/ADR-0067`（層の分類と合成点。ADR-0066 決定 2 の表を部分改定）
- 裁定: planning#378 → PR planning#382（2026-08-16。**適用範囲は基盤と可変機能ユニットの双方・feature 内部分割まで含む**）、
  planning#445（2026-08-22。**適合は必須。必須とするのはツリー全体への適合であり、名前だけを揃える対応は採らない**）、
  planning#450（2026-08-22。★不採用技術の完了条件は pnpm workspace のメンバ全体）
- 関連する実装仕様書: `.ai-context/specs/20260903_529_frontend-bulletproof-layout.md`
- 実装 issue: #529（環流元 planning#490）。**本 IADR は 3 分割 PR の第 1 段（骨格）である**
- 先行 IADR: `IADR-0080`（単独リポで自己完結するフロント）、`IADR-0288`（#414 の新スタック移行。
  決定 4 が本作業の暫定 `except` と**外す条件**を明記した）

## コンテキストと課題

着手前の実測（2026-09-03・`75075404`＝#414 の PR #651 マージ後）:

| 計画ツリーの項目（`main.tsx` を除く 12） | 本ユニット |
| --- | --- |
| `features/` | ✅ あり（内部分割は無い） |
| `testing/` | ✅ あり（`renderWithProviders.tsx` 1 本。#414 で新設） |
| `app/` `assets/` `components/` `config/` `hooks/` `lib/` `locales/` `stores/` `types/` `utils/` | 🔴 **10 項目すべて不在**（2/12） |

さらに **`features/` の下に feature でないものが 3 つあった**（`risk` / `monitor` / `shared`）。
これは `IADR-0288` 決定 4 が ESLint の `except` で暫定的に追認し、**「#529 が `src/lib/` `src/components/` へ
移したとき、この除外は消える」**と外す条件を書いたものである。**条件を書かない除外は恒久化する**という
方針どおり、条件が満たされたのでここで外す。

### なぜ 3 本の PR に割るか

**1 本にまとめると 40 数ファイルの import が一斉に動く。** 変更の性質（移送／内部分割／規則の拡張）が
混ざると、レビュアーは「どの import 変更がどの決定に属するか」を毎行で判定することになる。
① 骨格 → ② feature 内部分割 → ③ 規則の拡張 の順に割り、**前の PR がマージされてから次を切る。**
③ を先に置くと「規則を置いたが例外だらけ」という `MSP/ADR-0066` §理由 が退けた形になる。

## 決定

### 決定 1 — `src/` 直下を計画ツリーの 12 項目にする（`main.tsx` を除く）

`app/ assets/ components/ config/ features/ hooks/ lib/ locales/ stores/ testing/ types/ utils/` を置く。

- **`main.tsx` は持たない。** 可変機能ユニットは合成される側であり、SPA のエントリは基盤が持つ。
  姉妹ユニット knowledge も持たず、**11/11 で適合と判定されている**（issue コメントの実測 2026-08-30）。
- **`config/` を含める。** 計画ツリーは `MSP/ADR-0067` 決定 1 で `config` を shared 層の最上位へ出した。
  雛形（`templates/unit-template/frontend/`）と knowledge はこの改定より前であり `config/` を持たない。
  **本ユニットは計画の現行ツリーに合わせる**（雛形は bootstrap の写しであって規範ではない）。

### 決定 2 — 実体の無いディレクトリは `.gitkeep` の枠として置き、**枠である理由を `.gitkeep` に書く**

planning#445 は「名前だけを揃える対応は採らない」と定める。**これは「`foundation/` を `app/` へ改名して
済ませる」ことを禁じたものであり、実体の無い層に枠を置くことを禁じてはいない** —— 姉妹ユニット
knowledge は MSP#785 で 10 ディレクトリを `.gitkeep` だけで置き、**基盤側はこれを適合として扱っている**
（MSP `src/eslint.config.js` のコメントが「いずれも `.gitkeep` だけの枠である」と実測で記録している）。

- 🔴 **ただし空の枠は「未達」と「該当なし」を区別しない。** そこで **`.gitkeep` の中身に、なぜ枠なのかを
  書く**（本ユニットは 7 つが枠になる）。とくに **`locales/` は「該当なし」ではなく「未達」である**
  ——Lingui は `IADR-0288` 決定 6 で本ユニット未採用であり、**枠だけがあって中身が無いのは未達の記録で
  あって達成の記録ではない**。これを書き分けないと、後から数える人が枠の数を適合の数と読む。
- `app/` が枠になるのは、**層としての `app` の実体が合成点 `src/features/index.ts` だから**である
  （`MSP/ADR-0067` 決定 4。置き場が `features/` 直下なのは参照面 `@ai-stock-trading/features` の都合）。

### 決定 3 — `features/` 配下の非 feature を shared 層へ出す

| 移送元 | 移送先 | 判断 |
| --- | --- | --- |
| `features/risk/{contracts,queries}.ts` ＋テスト | `lib/risk/` | 2 画面以上が引く契約型・正規化・クエリ層 |
| `features/monitor/{contracts,queries}.ts` ＋テスト | `lib/monitor/` | 同上 |
| `features/roles.ts` | `lib/roles.ts` | 3 feature が引くロール定数（ドメイン語彙） |
| `features/shared/PaperModeBanner.tsx` | `components/PaperModeBanner.tsx` | 3 画面が出す共通部品 |
| `features/shared/paperMode.ts`（定数 3 つ） | `lib/paperMode.ts` | FR-12 の必須文言。React に依存しない値 |
| `features/shared/paperMode.ts`（`useBrokerProvider`） | `hooks/useBrokerProvider.ts` | 2 画面が引く共有 React フック |
| `features/{risk,monitor}/contractFixtures.ts` | `testing/{risk,monitor}ContractFixtures.ts` | テスト支援 |
| `features/{risk,monitor}/contract-fixtures/*.json`（7 件） | `testing/contract-fixtures/`（1 か所） | 同上 |

- **`paperMode.ts` を 2 つに割った。** 元ファイルは「定数とフックをコンポーネントと同居させない」理由
  （Fast Refresh の制約・文言をテストから直接引けること）を自ら書いていた。**同じ理由が、値とフックを
  分ける側にも効く** —— 計画ツリーは `hooks/` を共有フックの置き場と定めており、`lib/` に React フックを
  置くと `hooks/` が枠のままになる。**枠を 1 つ減らせるなら、そちらが実体である。**
- **フィクスチャの JSON は 2 ディレクトリを 1 つへ寄せた。** 領域はファイル名の前置き
  （`risk-controls.*` / `monitor.*`）が既に表しており衝突しない。`e2e/fixtures.ts` の loader も 2 本から 1 本になった。

### 決定 4 — 移送先の参照はエイリアス（`@ai-stock-trading/...`）で書く

相対パス（`../../lib/risk/contracts`）にしない。**PR ② で feature 内部を `components/` `routes/` へ割ると
相対の段数が変わり、同じ行を 2 回書き換えることになる。** エイリアスは `tsconfig.json`（`@ai-stock-trading/*` → `src/*`）と
`vitest.config.ts` の両方に既に張られている。

### 決定 5 — `apiFetch` 禁止の適用範囲を `src/features/**` から `src/**` へ広げる

🔴 **クエリ層が `src/lib/` へ出た瞬間、`files: ['src/features/**']` の禁止は移送先に掛からなくなる。**
`IADR-0288` 決定 3 の不変条件（`apiFetch` を呼んでよいのは薄いクエリ層だけ）が、**ディレクトリを動かした
だけで静かに消える**形だった。

- **禁止は `src/**` 全体に掛け、呼んでよい場所だけを `ignores` で名指しする**
  （`src/lib/*/queries.ts` と `src/features/*/*Queries.ts`）。
- 実測で確認した: `apiFetch` を実 import しているのは移送後も **3 ファイルだけ**である
  （`lib/risk/queries.ts` / `lib/monitor/queries.ts` / `features/sc01-settings/assumptionsQueries.ts`）。
- **規則が実際に働くことを実測した。** 画面（`SettingsPage.tsx`）へ `apiFetch` の import と
  他 feature（`sc02-risk-settings`）の import を一時的に足し、`no-restricted-imports` と
  `import/no-restricted-paths` の**両方が error になること**を確認して戻した
  （`IADR-0288` 決定 4 が「resolver を与えないと静かに 0 件検査になる」を実測で得た教訓に従う）。

### 決定 6 — 挙動を変えない。テストの `expect` を 1 行も書き換えない

**本 PR は移送と再配置に限る。** 受け入れの証拠は既存テストがそのまま緑であることであり、
`expect` に手を入れる必要が生じたら移送ではない変更が混入した合図である。
実測: 単体 19 ファイル 362 件・E2E 60 件が緑。`src/` 配下の差分は **import 行・ファイル位置・コメント**のみである。

## 理由

- **決定 1・2 は planning#445 の「ツリー全体への適合が必須」に素直に従う。** 姉妹ユニットが
  `.gitkeep` の枠で適合と扱われている以上、枠を置かない選択は本ユニットだけを外れた状態に留める。
  一方で**枠に理由を書く**のは、planning#445 が退けた「名前だけを揃える対応」との違いを、
  読む人が判定できるようにするためである。
- **決定 3 の判断基準は `MSP/ADR-0066` 決定 1 の「2 つ以上の feature が要るか」だけを使った。**
  1 feature しか使わないもの（`assumptionsQueries.ts`）は動かしていない。
- **決定 5 を本 PR に含めるのは、移送と同時でなければ穴が開くからである。** 規則の恒久形（4 層ゾーン）は
  PR ③ だが、**この 1 点だけは移送と不可分**である（先送りすると、その間だけ `lib/` が無防備になる）。

## 結果

- **良い影響**:
  - `src/` 直下が 2/12 から **12/12** になった。`features/` の下に feature でないものが無くなった。
  - `IADR-0288` 決定 4 の暫定 `except`（4 項目）が**外す条件どおりに消えた**。ESLint の `except` は
    「自分自身」だけになり、feature 間禁止が例外なしで働く。
  - `apiFetch` の閉じ込めが `src` 全体へ広がり、**移送で穴が開く経路が塞がった**。
  - `components/` `hooks/` `lib/` `testing/` に実体が入った（枠は 7 つ）。姉妹ユニット knowledge は
    `features/` 以外すべてが枠であり、**本ユニットのほうが実体を持つ**。
- **悪い影響 / トレードオフ**:
  - **`src/` 直下のディレクトリが 10 増え、うち 7 つは枠である。** 枠の存在それ自体は何も保証しない。
  - **import の行が 40 数ファイルで動いた。** レビューの負荷は PR を 3 本へ割ることで下げたが、消えてはいない。
  - **`app/` は枠のまま残る。** 合成点が層としては `app` でありながら `features/index.ts` に置かれるためで、
    これは計画側の決定（`MSP/ADR-0067` 決定 4）に由来する。**「適合していない」と読むべきかは計画側の判断**であり、
    実装は枠を置いて理由を記録するにとどめる。
- **残余リスク**:
  - **feature 内部の 6 分割は未達のまま**である（0/3）。#529 の PR ② が引き受ける。
  - **依存の向き（`shared → features → app`）はまだ機械で強制していない。** 本 PR が強制するのは
    feature 間禁止と `apiFetch` の閉じ込めだけである。4 層のゾーン化は PR ③ が引き受ける。
    **それまでは、shared から feature を引く逆流は目視でしか止まらない。**
  - **`locales/` が枠であることは未達の記録である**（Lingui 未採用。`IADR-0288` 決定 6）。
    枠の数を適合の数と読まないよう `.gitkeep` に書いたが、**書いたものが読まれる保証は無い。**

## 関連

- Supersedes: なし（`IADR-0288` 決定 4 の**暫定除外を、同決定が書いた条件どおりに解消**した。
  同決定そのものを覆していない）
- Superseded by: なし
- 後続: #529 の PR ②（feature 内部の 6 分割）・PR ③（`import/no-restricted-paths` の 4 層ゾーン化）

---

## 追記（2026-09-03・#529 第 2 段: feature 内部の 6 分割）

**本 IADR に決定 7・8 を加える。新しい IADR は起こさない**——第 1 段（骨格）と同じ判断基準
（`MSP/ADR-0066` 決定 1 の「2 つ以上の feature が要るか」と `MSP/ADR-0067` の層分類）を
feature の**内部**へ適用しただけであり、覆す決定も新しい軸も無いためである。

### 決定 7 — 内部配置は基盤の姉妹ユニットの形をそのまま採る

`microservices-platform` の `knowledge/frontend/src/features/sc04-wiki/` を実ツリーで確認し、同じ形にした。

```
<feature>/
  index.ts          ← 再輸出のみ（公開面。ADR-0066 決定 4 が barrel の維持を決めている）
  routes/           ← createRoute factory ＋ NavItem ＋ アクセス制御テスト
  components/       ← 画面・区画とそのテスト
  api/ hooks/ stores/ types/
```

| feature | 実体が入った区分 | 枠（`.gitkeep`）になった区分 |
| --- | --- | --- |
| `sc01-settings` | `api/` `components/` `routes/` **`types/`** | `hooks/` `stores/` |
| `sc02-risk-settings` | `components/` `routes/` | `api/` `hooks/` `stores/` `types/` |
| `sc03-controls` | `components/` `routes/` | `api/` `hooks/` `stores/` `types/` |

- 🔴 **枠を埋めるために抽象を作らない。** `hooks/` が 3 feature とも枠なのは実測の結果である
  （画面内で閉じたフックは 0 件。サーバー状態は TanStack Query、フォームのローカル状態は
  各コンポーネントの `useState` に閉じている）。**「6 分割だから 6 つ埋める」は計画外の抽象化であり、
  CLAUDE.md の禁止事項に当たる。**
- **`api/` が sc02 / sc03 で枠なのは、共有側にあることの帰結である**（欠落ではない）。両画面が読む
  端点は 2 つ以上の画面が消費するため、クエリ層は第 1 段で `src/lib/` へ出た。
  **この理由を `.gitkeep` 本文へ書く**（第 1 段の決定 2 と同じ扱い——空の枠は「該当なし」と「未達」を
  区別しないため）。

### 決定 8 — `api/` と `components/` の双方が要る型だけを `types/` へ出す

`sc01-settings` の `assumptionsQueries.ts` は値（クエリ）と型 5 件が同居しており、
🔴 **画面は型を得るためだけに「取得の実装」を import していた**（`import type { ChangeEntry,
TradingAssumptions } from './assumptionsQueries'`）。分割後は `api/` と `components/` が別ディレクトリに
なるため、この同居は**層をまたぐ依存を型の都合で作る**形になる。型を `types/index.ts` へ出す。

- **`api/` から再輸出しない。** 再輸出すると参照先が 2 つになり、「どちらから引くのが正しいか」が
  ファイルごとに割れる。**参照先は `../types` の 1 つに保つ。**
- **sc02 / sc03 は出さない。** 契約型は共有側（`src/lib/{risk,monitor}/contracts.ts`）にあり、
  画面内だけで閉じる型（`ShortSellingState`）は使う側と同じ `components/` にある。
  **切り出す理由が無いものを切り出さない。**

### あわせて直したもの（いずれも移送と不可分）

1. **ESLint の `ignores` を `src/features/*/*Queries.ts` → `src/features/*/api/*.ts` へ。**
   `IADR-0288` 決定 6 が「#529 で `api/` へ移すときは同時に更新すること」と指定した追随点である。
   🔴 **直さないと 2 通りに壊れる**——`api/` が `apiFetch` 禁止に掛かって lint が赤くなるか、
   （移送前に直した場合は）古い glob が何にも一致せず**「例外を書いたつもりで実は無い」**状態になる。
2. **他 feature の内部パスを指すコメント 2 件を是正。** `sc02` / `sc03` のルートが
   `` `../sc01-settings/index.tsx` `` を指していた。**`ADR-0066` 決定 1 が禁じた向きを文章の側に
   残すことになり**、しかも本 PR の移送で実在しないパスになる。公開面の名前（SC-01）だけを指す形へ改めた。
3. **`components/` 配下のテストが引く共有ハーネスをエイリアスへ。** 段数が変わるため
   （決定 4 と同じ理由）。

### 結果（第 2 段）

- **feature 内部分割 0/3 → 3/3**（3 feature × 6 区分 ＝ 18 ディレクトリすべて実在）。
- **feature の外から内部ディレクトリを参照する箇所は 0 件**——合成点 `src/features/index.ts` は
  各 feature の `index.ts` だけを引く。
- 規則の実効性を再度実測した: 画面へ `apiFetch` と**他 feature の深いパス**
  （`../../sc02-risk-settings/components/RiskSettingsPage`）を一時的に足し、両規則が error になることを
  確認して戻した。**分割で相対パスが深くなっても `import/no-restricted-paths` は効いている。**
- 挙動は変えていない（テストの `expect` は 1 行も動かさず、単体 362 件・E2E 60 件が緑）。
- **残余**: 依存の向き（`shared → features → app`）の 4 層ゾーン化は第 3 段が引き受ける。
  それまでは shared から feature を引く逆流は目視でしか止まらない。

---

## 追記（2026-09-03・#529 第 3 段: 依存の向きの機械強制）

**本 IADR に決定 9・10 を加える。新しい IADR は起こさない**——`MSP/ADR-0066` 決定 2・3 と
`MSP/ADR-0067` 決定 5 の層分類を、そのままゾーン定義へ写しただけであり、覆す決定も新しい軸も無い。
**#529 はこれで完了する。**

### 決定 9 — `src/` 直下を 4 層へ網羅的に割り、6 本のゾーンで一方向を強制する

`MSP/ADR-0067` 決定 5 の表をそのままゾーンにした。**表が `src/` 直下を網羅していないと
ゾーン定義を書き切れない**——同決定が表を改定した理由がこれであり、実際そのまま書けた。

| # | ゾーン | 根拠 |
| --- | --- | --- |
| ① | `features/<A>` → `features`（`except` は自分自身のみ） | `ADR-0066` 決定 1 |
| ② | shared 9 ディレクトリ → `features` / `app` | `ADR-0066` 決定 2 / `ADR-0067` 決定 5 |
| ③ | `features/*/**` → `app` | `ADR-0066` 決定 2 |
| ④ | `testing` → `features` | `ADR-0067` 決定 5（第 4 層） |
| ⑤ | 本番コード → `testing` | 同上（`testing` は参照される側にならない） |

- **shared は 9 ディレクトリを名前で持つ**（`components` / `hooks` / `lib` / `types` / `utils` /
  `stores` / **`config`** / `assets` / `locales`）。🔴 **`config` は shared である**——原典が
  `config` を `app` の兄弟と定めており、計画のツリーが `app/` の注釈へ折り畳んでいたことが
  乖離の起点だった（`ADR-0067` 決定 1）。
- **feature の列挙は引き続き `readdirSync` で実ディレクトリから作る**（列挙を手で持たない）。
  shared 側は逆に**名前で持つ**——`src/` 直下は層の分類そのものであり、走査で拾うと
  新しい層が既定で shared 扱いになる（緩い側へ倒れる）。**倒れる向きが逆なので、持ち方も逆にする。**

### 決定 10 — 合成点と「本番コード」を、除外リストではなく `target` のグロブで表す

🔴 **除外リスト（`except`）を伸ばす形を採らない。** `MSP/ADR-0066` §理由 が退けた「共有 feature」の
例外と同じで、**許可リストの保守が人に戻る**。

| 外したいもの | 書き方 | なぜ成り立つか |
| --- | --- | --- |
| 合成点 `src/features/index.ts`（層としては `app`。`ADR-0067` 決定 4） | ゾーン ③ の `target` を `./src/features/*/**` にする | 合成点は**深さが足りず一致しない**。計画が単一パスを名指しして固定しているため「成長する例外」にならない |
| テストコード（`testing/` を引くのが目的） | ゾーン ⑤ の `target` を `./src/!(testing)/**/!(*.test\|*.spec).{ts,tsx}` にする | `ADR-0067` 決定 5 が縛るのは**本番コード**である。縛る対象の明示であって規則の緩和ではない |

**⑤ の対象からテストを外さないと、`src/lib/risk/contracts.contract.test.ts`（shared に置かれた
テスト）が違反になる。** テストユーティリティは「実アプリと同じ木でテストを走らせる」ために在るので、
これを違反とする規則は現実と噛み合わない。

### 実効性の実測（規則を足したら、それが赤くなることを一度見る）

🔴 **`import/no-restricted-paths` は解決できた import しか検査しない**——resolver が無ければ
**静かに 0 件検査**になる（`IADR-0288` 決定 4 の実測）。**したがって「lint が緑」は
「規則が働いている」の証拠にならない。** 一時ファイル（プローブ）を置いて全ゾーンを測った。

| 種別 | 検査 | 結果 |
| --- | --- | --- |
| 陽性 | ① feature → 他 feature | ✅ error |
| 陽性 | ②a shared → features | ✅ error |
| 陽性 | ②b shared → app | ✅ error |
| 陽性 | ③ feature → app | ✅ error |
| 陽性 | ④ testing → features | ✅ error |
| 陽性 | ⑤ 本番コード → testing | ✅ error |
| **陰性** | shared に置かれた**テスト** → testing | ✅ error にならない（縛りすぎていない） |
| **陰性** | 合成点の位置（`features/` 直下） → app | ✅ error にならない（`ADR-0067` 決定 4 のとおり） |

**`app/` は本ユニットでは枠である**ため、②b と ③ は一時ファイル `src/app/__probe.ts` を置いて測った
（参照先が存在しないと「規則が働いた」のか「import が解決できず素通りした」のかを区別できない）。
プローブはすべて削除済みである。

### 既存コードの違反は 0 件だった

着手前に実測したところ、**shared → features / app は 0 件**、`src/testing/` を参照するのは
**すべて `*.test.*` ファイル**（15 ファイル）であった。第 1 段・第 2 段の移送が先行していたためであり、
**本 PR は検査器を足すだけで、本番コードを 1 行も直していない。**

### 残余（意図的に閉じていない穴・記録として残す）

**`src/features/` 直下に野良ファイルを置くと、どの feature ゾーンの `target` にも入らない。**
（合成点を除外するために `target` を `./src/features/*/**` としたことの裏返しである。）

- 🔴 **ただし穴は塞がっている方向がある。** 野良ファイルは**どの feature からも import できない**
  ——ゾーン ① の `from` が `./src/features` であり `except` は自分自身だけなので、
  `features/sc01-settings/**` から `../../__stray` を引くと error になる（**実測で確認した**）。
  したがって**野良ファイルは「共有の裏口」にはならない**。使えるのは合成点だけである。
- **これ以上グロブを複雑にしない**（`!(index|index.test|index.spec)` のような形は、
  合成点のテストが将来 `app` を引く必要が出たときに黙って壊れる）。**残余として記録し、
  同型の事故が実際に 2 回起きたら検査器を足す**（規約の追加は同型事故 2 回から）。

### 結果（第 3 段・#529 の完了）

- **`ADR-0066` 決定 3 が必須とした機械強制が、本ユニットへ配備された。** 計画の
  「現在の実現手段」欄が「🔴 未配備である」と書いていた状態が解消する（基盤側は knowledge ユニットに
  留まっており、**本ユニットは `ADR-0067` の 4 層分類をそのまま配備した最初の実装**である）。
- **#529 の 3 段すべてが完了した**——`src/` 直下 12/12・feature 内部 18/18・依存の向き 6 ゾーン。
- 挙動は変えていない（**本番コードの差分 0 行**。検査器の追加のみ。単体 362 件・E2E 60 件が緑）。
- **未達として残るのは `MSP/ADR-0031` の 3 技術**（Lingui・`@platform/ui`・orval）である。
  いずれも単独リポジトリでは解決できず（`IADR-0288` 決定 6）、#529 の射程外である。

---

## 追記（2026-09-03・#663: 決定 2 の撤回。`.gitkeep` 枠置きを撤去する）

**本 IADR に決定 11 を加える。新しい IADR は起こさない**——判断基準は `MSP/ADR-0069` の決定を
そのまま実装側へ写しただけであり、覆す決定も新しい軸も無いためである。

### 決定 11 — 決定 2 を撤回する。実体の無いディレクトリに `.gitkeep` の枠を置かない

計画 `MSP/ADR-0069`（2026-09-02 確定・利用者裁定）は、`MSP/ADR-0065` 決定 4（バックエンド
8 要素標準の `.gitkeep` 枠置きの撤回）と同じ理由（**枠が「適合の見え方」を作る**）が
フロントエンドにも及ぶと定めた。

- 決定 1: `.gitkeep` のみのディレクトリを置かない。射程は feature 内部・ユニット直下
  （`src/` 最上位）・雛形の 3 者すべて。
- 決定 3: 不在の意味は 2 通りある。**(a) 関心が無い＝適合**（不在それ自体が情報）、
  **(b) 関心はあるが置き場所が違う＝非適合**（枠の有無にかかわらず非適合）。
  **枠はこの区別を作らない**——枠を置いても (b) は直らず、「揃っている」ように見せるだけである。
- 決定 4: 共有層の区分（`hooks/ lib/ stores/ types/ utils/`）は「関心のあるモジュールの隣に
  置けない共有物の置き場」であって唯一の置き場ではない。
- 決定 5: 「`.gitkeep` のみのディレクトリが無いこと」を機械検査に載せる。

**これは本 IADR の決定 2（実体の無いディレクトリは `.gitkeep` の枠として置き、枠である理由を
`.gitkeep` に書く）を覆す。** 決定 1・3〜10（`src/` 直下 12 項目・feature 内部 6 分割・
依存の向きの 4 層ゾーン化）はいずれも「配置」の決定であり、覆らない。覆るのは
「実体の無い区分をどう表現するか」という決定 2 だけである。

### (a)/(b) 分類（実測。#663）

`frontend/src` 配下の `.gitkeep` 枠 17 件を全数、`MSP/ADR-0069` 決定 3 の (a)/(b) で分類した。
**(b)（関心はあるが置き場所が違う）は 0 件だった**——#529 の PR①（骨格）が着手前に実際に
存在した (b) 型の誤配置（`features/risk` `features/monitor` `features/shared`
`features/roles.ts`）を `src/lib/` `src/components/` `src/hooks/` へ既に是正済みであり、
残る 17 件は「是正済みの残り」＝最初から実体を伴わない (a) 型である。

| ディレクトリ | 分類 | 根拠 |
| --- | --- | --- |
| `src/app/` | (a) | 層としての実体は合成点 `src/features/index.ts`（`MSP/ADR-0067` 決定 4） |
| `src/assets/` | (a) | 自己ホスト資産を持たない（3 画面は素の HTML 要素で描画。`IADR-0288` 決定 6） |
| `src/config/` | (a) | 実行時構成は基盤（`@foundation`）から受け取り、自前の構成を持たない |
| `src/locales/` | (a) | Lingui 未採用（`IADR-0288` 決定 6）。単独リポジトリでは解決できない未達であり、この単位には i18n カタログという関心そのものが無い |
| `src/stores/`（共有） | (a) | Zustand 未導入。画面をまたぐクライアント状態が無い |
| `src/types/`（共有） | (a)† | 共有型は正規化関数と不可分なため `src/lib/{risk,monitor}/contracts.ts` に同居。`MSP/ADR-0069` 決定 4 が共有層区分は唯一の置き場ではないと定めており、shared 区分間の再配置は非適合ではない |
| `src/utils/`（共有） | (a)† | 同上（純関数の同居） |
| `sc01-settings/hooks/` `sc02-risk-settings/hooks/` `sc03-controls/hooks/` | (a) | 画面内で閉じたフックが無い（TanStack Query + `useState` で完結） |
| `sc01-settings/stores/` `sc02-risk-settings/stores/` `sc03-controls/stores/` | (a) | Zustand 未導入 |
| `sc02-risk-settings/api/` `sc03-controls/api/` | (a) | 端点は 2 画面以上が消費するため、クエリ層は共有側 `src/lib/{risk,monitor}/queries.ts` にある |
| `sc02-risk-settings/types/` `sc03-controls/types/` | (a) | 契約型は共有側にあり、画面内だけで閉じる型は使う側のコンポーネントに同居 |

† 物理的な移送は行わない。`src/types/` `src/utils/`（共有）へ型・純関数だけを分離すると、
`IADR-0290` が最初から避けた「型と正規化ロジックの分割事故」を再導入する。**計画外の抽象化
（値のためだけに型ファイルを新設する）は避ける。**

詳細な分類根拠は作業仕様書 `.ai-context/specs/20260903_663_frontend-no-empty-frames.md` を参照。

### 実施したこと

- 17 件の `.gitkeep` と、その結果空になったディレクトリを撤去した。
- `frontend/eslint.config.js` の無害性を実測で確認した——`SHARED_LAYER_DIRS` は名前の静的配列で
  あり、`import/no-restricted-paths` の `zones[].target` / `from` はファイルパスへの glob 一致
  であって対象ディレクトリの存在を要求しない。撤去後も `npm run lint` は緑のままだった。
- **規則の実効性を再実測した**（陽性 6 ゾーンすべてが引き続き error になることをプローブで確認し、
  削除した）。撤去は依存の向きの機械強制（決定 9・10）を弱めない。
- 機械検査 `scripts/check-frontend-empty-frames.js` を新設した（`MSP/ADR-0069` 決定 5）。
  `frontend/src` 配下の葉ディレクトリを走査し、直下のファイルが `.gitkeep` だけ、または 0 件なら
  枠と判定する。`--self-test` あり。`.github/workflows/ci.yml` の `static-checks` ジョブへ配線した
  （ジョブ名は変更しない）。

### 結果

- **良い影響**: フロントエンドの `.gitkeep` 枠置きが撤去され、`MSP/ADR-0069` 決定 1 に適合した。
  再発（新しい枠の追加）は機械検査が止める。ESLint のゾーン定義は無傷であり、規則の実効性は
  維持されている（陽性 6・撤去前と同数）。
- **悪い影響 / トレードオフ**: `src/types/` `src/utils/`（共有）の 2 件は文字どおりには
  「実体はあるが置き場所が違う」(b) の定義に近く読めるが、決定 4 の shared 区分間再配置の許容と
  `IADR-0290` 自身の分割事故回避の判断を優先し、物理的な移送はしなかった。**この判断は境界事例
  であり、将来 `MSP/ADR-0069` フォローアップ 4（型 (b) の常設検査）が配備されたときに
  再検証の対象になり得る。**
- **残余リスク**: `MSP/ADR-0069` 決定 5 が明示するとおり、本検査は「枠が無いこと」しか見ない。
  型 (b)（置き場所違い）の常設検査は本作業の射程外であり、同型の事故が実際に起きたら別途起票する
  （CLAUDE.md「検査器の追加は同型事故 2 回から」）。

### 関連

- Supersedes: なし（決定 2 を本追記が覆す。決定 1・3〜10 は不変）
- Superseded by: なし
- 実装 issue: #663（起点は `MSP/ADR-0069`）
