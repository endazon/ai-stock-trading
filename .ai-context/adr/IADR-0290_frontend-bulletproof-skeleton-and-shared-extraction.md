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
