---
title: フロントエンドの src/ 直下と Feature 内部構成を Bulletproof React（計画 §ディレクトリ構成）へ適合させる
type: spec
status: review
related_ids: [SC-01, SC-02, SC-03, FR-12, FR-13, FR-17, FR-19, FR-20, UC-06]
author: endazon
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md
  - planning:projects/microservices-platform/07_adr/ADR-0067_frontend-layer-classification-and-composition-point.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# 仕様書: フロントエンドのディレクトリ構成適合（#529）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-12（ペーパートレード表示）/ FR-13 / FR-17 / FR-19 / FR-20
- ユースケース（UC）: UC-06
- 画面（SC）: SC-01（設定）/ SC-02（リスク設定）/ SC-03（承認・統制状態参照）
- 関連 ADR（基盤の計画）: `MSP/ADR-0031`（フロントエンド採用技術）/
  `MSP/ADR-0066`（feature 間 import の禁止・依存の向き・`import/no-restricted-paths` の必須配備）/
  `MSP/ADR-0067`（層の分類と合成点。ADR-0066 決定 2 の表を部分改定）
- 計画書リンク: <https://github.com/endazon/project-planning/blob/main/projects/microservices-platform/06_technical/13_frontend-stack.md>
- 先行する実装 ADR: `IADR-0080`（単独リポで自己完結するフロント）/ `IADR-0288`（#414 の新スタック移行）
- 実装 issue: #529（環流元 planning#490）。裁定は planning#378 → planning#445 → planning#450

## 目的・背景

計画 `13_frontend-stack` §ディレクトリ構成 は設計を **Bulletproof React（Feature First Architecture）** と定め、
`src/` 直下の項目と feature 内部の 6 分割（`api/ components/ hooks/ routes/ stores/ types/`）まで規範化している。
**この構成は基盤と可変機能ユニット（本ユニット）の双方に適用され、適合は必須である**（planning#378 → PR382 の裁定 2026-08-16、
planning#445 の裁定 2026-08-22）。planning#445 は 🔴 **「必須とするのはツリー全体への適合であり、名前だけを揃える対応は採らない」**
と明示している。

さらに `MSP/ADR-0066` は Bulletproof React の中核規範のうち計画に落ちていなかった 2 点
（feature 間 import の禁止・`shared → features → app` の一方向依存）を加え、決定 3 で
**ESLint `import/no-restricted-paths` による機械強制を必須**とした。可変ユニットは
**基盤の ESLint からは是正できない**ため自リポジトリで同じ規則を持つ。

`MSP/ADR-0067` はその層の分類を原典へ戻した（`config` を shared 層の最上位へ・`i18n` の実行時部分を `lib/i18n/` へ・
合成点を `app` 層として扱う・`testing/` をテスト専用の第 4 層とする）。

### 着手前の実測（2026-09-03・`75075404`＝#414 の PR #651 マージ後）

`frontend/src/` 直下（計画ツリーの項目のうち `main.tsx` は SPA ホストのみのため本ユニットは対象外）:

| 計画ツリーの項目 | 本ユニット（着手前） |
| --- | --- |
| `features/` | ✅ あり（**内部分割は無い**。5 ディレクトリすべて 1 階層） |
| `testing/` | ✅ あり（`renderWithProviders.tsx` 1 本。#414 で新設） |
| `app/` `assets/` `components/` `config/` `hooks/` `lib/` `locales/` `stores/` `types/` `utils/` | 🔴 **10 項目すべて不在**（2/12） |

feature 内部（`api/ components/ hooks/ routes/ stores/ types/`）: 🔴 **0/5**。
さらに **`features/` の下に feature でないものが 3 つある**（`risk` / `monitor` / `shared`）。これは `IADR-0288`
決定 4 が ESLint の `except` で暫定的に追認し、**「#529 が `src/lib/` `src/components/` へ移したとき、この除外は消える」**
と外す条件を明記したものである。

### 姉妹ユニットの先例

`microservices-platform` の `knowledge/frontend/src/` は MSP#785 で本構成へ適合済みであり（11/11）、
**`features/` 以外の 10 ディレクトリは `.gitkeep` だけの枠である**（MSP `src/eslint.config.js` のコメントが実測として明記）。
すなわち **「枠を置き、実体が生じたら入れる」形が姉妹ユニットの先例**である。本ユニットは
`components/` `hooks/` `lib/` `testing/` に**実体を入れる**（移送対象が実在するため）。

## 対象範囲

- 対象:
  - `frontend/src/` 直下を計画ツリーの形にする（`app/ assets/ components/ config/ features/ hooks/ lib/ locales/ stores/ testing/ types/ utils/`）
  - `features/` 配下の非 feature（`risk` / `monitor` / `shared` / `roles.ts`）を shared 層（`lib/` `components/` `hooks/`）へ移送
  - `contract-fixtures/`（計画の 6 区分に無い）を `src/testing/` へ移送（#529 のスコープ 3 点目・issue コメントの指針）
  - 各 feature（`sc01-settings` / `sc02-risk-settings` / `sc03-controls`）の内部を `api/ components/ hooks/ routes/ types/` へ分割し、
    公開面を `index.ts` の再輸出に限る
  - ESLint の `import/no-restricted-paths` を `shared → features → app` の一方向＋ feature 間禁止へ拡張し、
    `IADR-0288` の暫定 `except`（`risk` / `monitor` / `shared` / `roles.ts`）を外す
- 対象外:
  - `MSP/ADR-0031` の未達技術（Lingui / `@platform/ui` / orval）の導入（`IADR-0288` 決定 6。単独リポでは解決できない）。
    したがって `locales/` は枠のみになる
  - 画面の振る舞い・BFF 契約・バックエンドの変更（**本作業は移送と再配置に限り、挙動を変えない**）
  - `test/foundation-stub/` `e2e/` の構成（`src/` ではない。参照の追随のみ行う）
  - npm → pnpm への移行（本リポは `package-lock.json`。§計画書との差異 を参照）

## 設計

### 分割方針（PR を 3 本に分ける）

**1 本にまとめると 40 数ファイルの import が一斉に動き、レビューできない。** 変更の性質ごとに 3 本へ割る。

| PR | 内容 | 性質 |
| --- | --- | --- |
| ① 骨格 | `src/` 直下の 12 項目を用意し、**非 feature の共有物を shared 層へ移送**する | 移送（`git mv`）＋ import の追随 |
| ② feature 内部分割 | 3 feature の内部を `api/ components/ hooks/ routes/ types/` へ割り、公開面を `index.ts` に限る | 移送（`git mv`）＋ import の追随 |
| ③ 規則の拡張 | `import/no-restricted-paths` を 4 層のゾーンへ拡張し、暫定 `except` を外す | 検査器のみ |

**順序は ①→②→③ で固定する。** ③ の規則は ①② の配置が済んでいないと**必ず赤くなる**（規則を先に置くと
「規則を置いたが例外だらけ」という `MSP/ADR-0066` §理由 が退けた形になる）。**前の PR がマージされてから
次を `origin/develop` 基点で切る。**

### 層の分類（`MSP/ADR-0067` 決定 5 をそのまま採る）

| 層 | ディレクトリ | 参照してよい先 |
| --- | --- | --- |
| shared | `components` / `hooks` / `lib` / `types` / `utils` / `stores` / `config` / `assets` / `locales` | shared のみ |
| features | `features/`（**合成点 `features/index.ts` を除く**） | shared |
| app | `app/` ＋ **合成点 `features/index.ts`** | shared と features |
| testing（テスト専用） | `testing/` | shared と app。**本番コードから参照しない** |

### 差分表（現状の全ファイル → 移送先）

#### PR ① — `src/` 直下の骨格と共有物の移送

| 現在 | 移送先 | 層 | 根拠 |
| --- | --- | --- | --- |
| `src/features/roles.ts` | `src/lib/roles.ts` | shared | 3 feature が引くドメイン語彙（`ADR-0066` 決定 1「2 つ以上の feature が要るものは `lib/`」） |
| `src/features/risk/contracts.ts` | `src/lib/risk/contracts.ts` | shared | 契約型と正規化。SC-02 / SC-03 と共有バナーが引く |
| `src/features/risk/contracts.test.ts` | `src/lib/risk/contracts.test.ts` | testing | 移送先へ同伴 |
| `src/features/risk/contracts.contract.test.ts` | `src/lib/risk/contracts.contract.test.ts` | testing | 同上 |
| `src/features/risk/queries.ts` | `src/lib/risk/queries.ts` | shared | `apiFetch` を閉じ込めるクエリ層。SC-02 / SC-03 / 共有フックが引く |
| `src/features/monitor/contracts.ts` | `src/lib/monitor/contracts.ts` | shared | 同上（SC-02 の 2 フォームが引く） |
| `src/features/monitor/contracts.test.ts` | `src/lib/monitor/contracts.test.ts` | testing | 移送先へ同伴 |
| `src/features/monitor/queries.ts` | `src/lib/monitor/queries.ts` | shared | 同上 |
| `src/features/shared/PaperModeBanner.tsx` | `src/components/PaperModeBanner.tsx` | shared | 3 画面が出す共通部品（`ADR-0066` 決定 1「共有部品は `components/`」） |
| `src/features/shared/paperMode.ts`（定数 3 つ） | `src/lib/paperMode.ts` | shared | FR-12 の必須文言。値であり React に依存しない |
| `src/features/shared/paperMode.ts`（`useBrokerProvider`） | `src/hooks/useBrokerProvider.ts` | shared | 2 画面が引く共有フック。計画ツリーの `hooks/` はこれのための枠である |
| `src/features/risk/contractFixtures.ts` | `src/testing/riskContractFixtures.ts` | testing | #529 スコープ 3 点目。issue コメントの指針「`testing/` 相当として出す」 |
| `src/features/monitor/contractFixtures.ts` | `src/testing/monitorContractFixtures.ts` | testing | 同上 |
| `src/features/risk/contract-fixtures/*.json`（5 件） | `src/testing/contract-fixtures/*.json` | testing | 同上。ファイル名が `risk-controls.*` / `monitor.*` で既に前置されており衝突しない |
| `src/features/monitor/contract-fixtures/*.json`（2 件） | `src/testing/contract-fixtures/*.json` | testing | 同上 |
| （新規） | `src/app/.gitkeep` `src/assets/.gitkeep` `src/config/.gitkeep` `src/locales/.gitkeep` `src/stores/.gitkeep` `src/types/.gitkeep` `src/utils/.gitkeep` | — | 計画ツリーの枠。姉妹ユニット（knowledge）と同じ形 |
| `src/features/index.ts` | **動かさない** | app | `ADR-0067` 決定 4。置き場が `features/` 直下なのは参照面（`@ai-stock-trading/features`）の都合であり、層は `app` |
| `src/testing/renderWithProviders.tsx` | 動かさない | testing | 既に適合 |

**移送に伴う参照の追随**（`git mv` 後に import を書き換える。挙動は変えない）:

- `src/features/*/**` からの `../risk/...` `../monitor/...` `../shared/...` `../roles` → **`@ai-stock-trading/lib/...` などのエイリアス**を使う
  （`tsconfig.json` の `@ai-stock-trading/*` → `src/*` は既に張られており、`vitest.config.ts` にも同じ alias がある）。
  相対パス（`../../lib/...`）にすると feature 内部分割（PR ②）で段数が変わり、**同じ行を 2 回書き換えることになる。**
- `e2e/fixtures.ts` の 4 箇所（`../src/features/{risk,monitor}/contracts` と `contract-fixtures/` の URL）
- `src/features/*/**` のテストが引く `../../testing/renderWithProviders` は段数が変わらないため据え置き（PR ② で変わる）

**ESLint の追随**（PR ① で必要な最小限。恒久形は PR ③）:

- `SHARED_INSIDE_FEATURES` = `['risk','monitor','shared']` と `SHARED_FILES_INSIDE_FEATURES` = `['roles.ts']` は
  **移送で参照先が `features/` の外へ出るため空になる**。`IADR-0288` 決定 4 が書いた「外す条件」がここで満たされる。
- `apiFetch` を呼んでよい場所の `ignores`（`src/features/*/queries.ts` / `src/features/*/*Queries.ts`）は、
  `queries.ts` が `src/lib/` へ出るため **`src/features/**` の files に一致しなくなる**。`src/lib/*/queries.ts` を
  呼んでよい場所として明示する（`src/features/*/*Queries.ts` は PR ② で `api/` へ移るまで残す）。

#### PR ② — feature 内部の 6 分割

| 現在 | 移送先 | 区分 |
| --- | --- | --- |
| `src/features/sc01-settings/index.tsx` | `src/features/sc01-settings/index.ts`（再輸出のみ）＋ `routes/sc01SettingsRoute.tsx` | routes |
| `src/features/sc01-settings/SettingsPage.tsx` | `components/SettingsPage.tsx` | components |
| `src/features/sc01-settings/SettingsPage*.test.tsx` `access.test.tsx` | `components/` / `routes/` の各テスト | — |
| `src/features/sc01-settings/assumptionsQueries.ts` | `api/assumptionsQueries.ts` | api |
| `src/features/sc02-risk-settings/index.tsx` | `index.ts` ＋ `routes/sc02RiskSettingsRoute.tsx` | routes |
| `src/features/sc02-risk-settings/RiskSettingsPage.tsx` ほか 3 フォーム | `components/` | components |
| `src/features/sc03-controls/index.tsx` | `index.ts` ＋ `routes/sc03ControlsRoute.tsx` | routes |
| `src/features/sc03-controls/ControlStatusPage.tsx` `ShortSellingStatusSection.tsx` | `components/` | components |

- **`api/` が空になる feature（sc02 / sc03）** は、取得・更新が共有クエリ層（`src/lib/{risk,monitor}/queries.ts`）に
  あるためである。**空の `api/` に `.gitkeep` を置く**（枠は計画の規範であり、実体が無いことは移送先が
  shared にあることの帰結である）。`hooks/` `stores/` `types/` も同様に枠のみになる見込みであり、
  **PR ② の着手時に実測して確定する**（画面内に閉じたフック・型があれば切り出す）。
- 公開面は `index.ts` の再輸出に限る（`ADR-0066` 決定 4 が barrel の維持を明示的に決めている）。
  合成点 `src/features/index.ts` は各 feature の `index.ts` だけを引く。

#### PR ③ — `import/no-restricted-paths` の 4 層ゾーン化

- ゾーンを次の 4 本＋ feature 間禁止へ拡張する:
  - shared（9 ディレクトリ） → `features` / `app` を参照しない
  - `features/<各ディレクトリ>` → 他の feature を参照しない（`except` は自分自身のみ）
  - `features`（合成点を除く） → `app` を参照しない
  - 本番コード → `testing/` を参照しない
- 🔴 **テストファイルの扱いを明示する。** `src/lib/risk/contracts.contract.test.ts` は shared に置かれるが
  `src/testing/` のフィクスチャを引く。`ADR-0067` 決定 5 の「本番コードから `testing/` を参照しない」は
  **テストコードを縛らない**ため、ゾーンの `target` から `**/*.test.*` を外す形で書く。
  **これは規則の緩和ではなく、規則が縛る対象（本番コード）の明示である。**
- `IADR-0288` 決定 4 の暫定 `except`（`risk` / `monitor` / `shared` / `roles.ts`）が空になったことを、
  **配列を消す前に実測で確かめる**（`except` を空にした状態で `npm run lint` が緑であること）。

### 挙動を変えないことの担保

**本作業は移送と再配置に限る。** 受け入れ基準は「既存のテストが 1 件も落ちない・1 件も書き換わらない（import 行を除く）」である。
テストの本文（`expect`）に手を入れる必要が生じたら、それは移送ではない変更が混入した合図であり、いったん止める。

## 受け入れ基準

### PR ①（骨格）

- [ ] `frontend/src/` 直下が計画ツリーの 12 項目（`app assets components config features hooks lib locales stores testing types utils`）を持つ
- [ ] `src/features/` の直下に feature 以外（`risk` / `monitor` / `shared` / `roles.ts`）が無い
- [ ] `contract-fixtures/`（JSON 7 件）と 2 つの `contractFixtures.ts` が `src/testing/` にある
- [ ] `eslint.config.js` の `SHARED_INSIDE_FEATURES` / `SHARED_FILES_INSIDE_FEATURES` が空になり、`npm run lint` が緑
- [ ] `apiFetch` を呼ぶファイルが `src/lib/{risk,monitor}/queries.ts` と `src/features/sc01-settings/assumptionsQueries.ts` の 3 つだけである（`IADR-0288` 決定 3 の不変条件を維持）
- [ ] `npm run typecheck` / `npm run lint` / `npm test` / Playwright E2E がいずれも緑
- [ ] テストの `expect` が 1 行も変わっていない（変更は import 行とファイル位置のみ）

### PR ②（feature 内部分割）

- [ ] 3 feature が `api/ components/ hooks/ routes/ types/`（＋必要なら `stores/`）を持つ
- [ ] feature の外から参照されるのは `src/features/<name>/index.ts` だけである
- [ ] `npm run typecheck` / `lint` / `test` / E2E が緑

### PR ③（規則の拡張）

- [ ] `import/no-restricted-paths` が shared / features / app / testing の 4 層と feature 間禁止を強制する
- [ ] `except` から `risk` / `monitor` / `shared` / `roles.ts` が消えている
- [ ] 規則が実際に働くことを実測で示す（意図的な違反 import を一時的に置き、error になることを確認して戻す）
- [ ] `npm run typecheck` / `lint` / `test` / E2E が緑

## テスト方針

- **新しいテストを増やさない。** 本作業は挙動を変えないため、既存の 15 テストファイル（約 100 ケース）が
  そのまま緑であることが唯一の証拠である。
- **移送の網羅は機械で数える。** `find frontend/src -type f` の前後差分と、`git log --follow` が
  移送を rename として認識することを確認する（`git mv` を使い、内容の書き換えは import 行に限る）。
- **E2E（`frontend/e2e`）を必ず走らせる。** `e2e/fixtures.ts` が `src/features/**` を相対パスで直接引いており、
  ここが**単体テストでは検出されない参照**である。
- **規則の実効性は PR ③ で実測する**（`IADR-0288` 決定 4 が「resolver を与えないと静かに 0 件検査になる」を
  実測で得た教訓に従う。規則を足したら、それが赤くなることを一度見る）。

## 計画書との差異

- **差異 1（記録のみ・是正しない）: 本リポは npm（`package-lock.json`）であり pnpm workspace のメンバではない。**
  計画 `13_frontend-stack` §採用技術一覧 はパッケージ管理を pnpm（★採用）とし、planning#450 の裁定は
  ★不採用技術の完了条件を「**pnpm workspace のメンバ全体**から消えていること」と定める。
  本ユニットは `microservices-platform` へ submodule として合成されたときにそのメンバ（`'*/frontend'`）となる一方、
  **単独リポジトリとしては npm で自己完結している**（`IADR-0080` 決定 2）。
  **#529 の射程は §ディレクトリ構成 であり、パッケージ管理は射程外である。** 環流の要否は
  既存 issue を検索したうえで判断する（`gh issue list -R endazon/project-planning --search "pnpm workspace" --state all`）。
- **差異 2: `main.tsx` は本ユニットに存在しない。** 計画ツリーの末尾 `main.tsx` は SPA ホスト（platform）のものであり、
  可変機能ユニットは合成される側であるため持たない。姉妹ユニット knowledge も持たない（11/11 で適合と判定されている）。
  したがって本ユニットの適合は **12/12**（`main.tsx` を除く）で測る。
- **差異 3: `config/` は枠のみになる。** `ADR-0067` 決定 1 が `config` を shared 層の最上位へ置いたが、
  本ユニットは実行時構成を基盤（`@foundation`）から受け取るため自前の構成を持たない。
  **枠は置く**（ツリー全体への適合が必須であるため）。姉妹ユニット knowledge は `config/` をまだ持たない
  （MSP#785 が `ADR-0067` より前であるため）。**本ユニットのほうが計画の現行ツリーに近い形になる。**
- **差異 4: `locales/` は枠のみになる。** Lingui は `IADR-0288` 決定 6 で本ユニット未採用（単独リポでは解決できない）。

## 未決事項

- **PR ② の `hooks/` `stores/` `types/` に実体が入るかは着手時の実測で決める。** 画面内に閉じたフック・型が
  無ければ枠のみになる。**無理に切り出さない**（計画外の抽象化は CLAUDE.md の禁止事項）。
- **`src/app/` に実体が入らない。** 合成点は `ADR-0067` 決定 4 により層としては `app` だが、置き場は
  `features/index.ts` のままである（参照面 `@ai-stock-trading/features` の都合）。したがって `app/` は枠のみになる。
  **これを「適合していない」と読むべきかは計画側の判断であり、実装は枠を置いて記録する。**

---

## PR ② の実測と確定（2026-09-03・骨格 PR #653 マージ後）

着手時に「未決事項」としていた点を実測で確定した。

### 参照した実装（基盤の姉妹ユニット）

`microservices-platform` の `knowledge/frontend/src/features/sc04-wiki/` を実ツリーで確認した。
**本 PR はこの形をそのまま採る。**

```
sc04-wiki/
  index.ts            ← 再輸出 1 行のみ（公開面）
  routes/sc04WikiRoute.ts
  components/WikiAccessPage.tsx ＋ .test.tsx
  api/ hooks/ stores/ types/    ← いずれも .gitkeep のみ
```

### 確定した内部配置

| feature | api/ | components/ | hooks/ | routes/ | stores/ | types/ |
| --- | --- | --- | --- | --- | --- | --- |
| `sc01-settings` | `assumptionsQueries.ts` | 画面 1 ＋テスト 2 | 枠 | ルート ＋ access テスト | 枠 | **`index.ts`（型 5 件）** |
| `sc02-risk-settings` | 枠 | 画面 1 ＋フォーム 3 ＋テスト 6 | 枠 | ルート ＋ access テスト | 枠 | 枠 |
| `sc03-controls` | 枠 | 画面 1 ＋区画 1 ＋テスト 4 | 枠 | ルート ＋ access テスト | 枠 | 枠 |

- **`hooks/` は 3 feature とも枠になった。** 実測すると画面内で閉じたフックは 1 つも無い
  （`export function use` / `const use` が 0 件）。サーバー状態は TanStack Query、フォームの
  ローカル状態は各コンポーネントの `useState` に閉じている。**枠を埋めるために抽象を作らない。**
- **`stores/` は 3 feature とも枠。** Zustand は本ユニット未導入（`IADR-0288` 決定 6）。
- **`api/` は sc02 / sc03 が枠。** 両画面が読む端点（`/risk-controls/*` ／ `/monitor/*`）は
  **2 つ以上の画面が消費する**ため、クエリ層は骨格 PR で共有側（`src/lib/`）へ出た。
  **枠であることは、共有側にあることの帰結である**（欠落ではない）。
- 🔴 **`types/` は sc01 だけ実体が入った。** `assumptionsQueries.ts` に値（クエリ）と型 5 件が
  同居しており、**画面は型のためだけに「取得の実装」を import していた**。`api/` と `components/` の
  双方が要る型なので `types/index.ts` へ出す。sc02 / sc03 は契約型が共有側にあり、画面内だけで
  閉じる型（`ShortSellingState`）は使う側と同じ `components/` にあるため、切り出す理由が無い。

### あわせて直したもの

- **他 feature の内部パスを指すコメント 2 件**（`sc02` / `sc03` のルートが
  `` `../sc01-settings/index.tsx` `` を指していた）。**参照の禁止（`ADR-0066` 決定 1）を文章の側に
  残すことになり、しかも本 PR の移送で実在しないパスになる。** 公開面の名前（SC-01）だけを指す形に改めた。
- **ESLint の `ignores` を `src/features/*/*Queries.ts` → `src/features/*/api/*.ts` へ**
  （`IADR-0288` 決定 6 が「#529 で `api/` へ移すときは同時に更新すること」と指定した追随点）。
  **直さないと `api/` が禁止に掛かって赤くなるか、古い glob が何にも一致せず「例外を書いたつもりで
  実は無い」状態になる。**
- **`components/` 配下のテストが引く共有ハーネス**は段数が変わるため、エイリアス
  （`@ai-stock-trading/testing/renderWithProviders`）へ寄せた（骨格 PR の決定 4 と同じ理由）。

### 実測（受け入れ）

- 3 feature × 6 区分 ＝ **18 ディレクトリすべて実在**（`0/3` → `3/3`）
- **feature の外から内部ディレクトリを参照する箇所は 0 件**（合成点は barrel のみを引く）
- 規則の実効性を再度実測: 画面へ `apiFetch` と他 feature の**深いパス**
  （`../../sc02-risk-settings/components/RiskSettingsPage`）を一時的に足し、
  `no-restricted-imports` と `import/no-restricted-paths` の両方が error になることを確認して戻した
- `typecheck` / `lint` / `test`（19 ファイル 362 件）/ `e2e:typecheck` / `e2e`（60 件）すべて緑。
  **テストの `expect` は 1 行も変えていない**
