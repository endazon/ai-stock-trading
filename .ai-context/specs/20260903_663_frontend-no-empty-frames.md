---
title: frontend の .gitkeep だけの枠ディレクトリを撤去し、無いことを機械検査に載せる
type: spec
status: doing
related_ids: [SC-01, SC-02, SC-03, FR-12, FR-13, FR-17, FR-19, FR-20, UC-06]
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0069_frontend-scaffolding-frames-and-absence-semantics.md
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
---

# 仕様書: frontend の `.gitkeep` だけの枠ディレクトリの撤去（#663）

## 起点となる計画書（トレーサビリティ）

- 基盤（microservices-platform）の計画 ADR: `MSP/ADR-0069`（フロントエンドにも空枠を置かない。
  不在は「関心が無い」と「置き場所が違う」を区別する。2026-09-02 確定・利用者裁定）
- 先行する実装 ADR: `IADR-0290`（#529。決定 2 で `.gitkeep` 枠置きの規範を採用していた。本作業はその決定 2 を覆す）
- 実装 issue: #663（起点は `MSP/ADR-0069`。計画側裁定に本リポジトリが追随する作業であり、
  新たな環流は不要——`MSP/ADR-0069` 自身が既に確定済みの裁定であるため）
- 画面: SC-01（設定）/ SC-02（リスク設定）/ SC-03（承認・統制状態参照）

## 目的・背景

`MSP/ADR-0069` は `MSP/ADR-0065` 決定 4（バックエンド 8 要素標準の `.gitkeep` 枠置きの撤回）と
同じ理由（**枠が「適合の見え方」を作る**）がフロントエンドにも及ぶと判断し、次を定めた。

- 決定 1: `.gitkeep` のみのディレクトリを置かない。射程は feature 内部・ユニット直下（`src/` 最上位）・
  雛形の 3 者すべて。
- 決定 3: 不在の意味は 2 通りある。**(a) 関心が無い＝適合**（不在それ自体が情報）、
  **(b) 関心はあるが置き場所が違う＝非適合**（枠の有無にかかわらず非適合）。**枠はこの区別を作らない**
  ——枠を置いても (b) は直らず、「揃っている」ように見せるだけである。
- 決定 4: 共有層の区分（`hooks/ lib/ stores/ types/ utils/`）は「関心のあるモジュールの隣に置けない
  共有物の置き場」であって唯一の置き場ではない。
- 決定 5: 「`.gitkeep` のみのディレクトリが無いこと」を機械検査に載せる。

これは本リポジトリの `IADR-0290` 決定 2（実体の無いディレクトリは `.gitkeep` の枠として置き、
枠である理由を `.gitkeep` に書く）を覆す。**`IADR-0290` の決定 1・3〜10（`src/` 直下 12 項目・
feature 内部 6 分割・依存の向きの 4 層ゾーン化）はいずれも「配置」の決定であり、覆らない。**
覆るのは「実体の無い区分をどう表現するか」という決定 2 だけである。

## 対象範囲

- 対象: `frontend/src` 配下の `.gitkeep` のみのディレクトリ 17 件の撤去と分類記録、
  ESLint 設定の無害性確認、機械検査の新設（`scripts/check-frontend-empty-frames.js`）と CI 配線
- 対象外:
  - `MSP/ADR-0069` 決定 1 が射程外とした `docs/` 配下・`/new-project` が置く枠（本リポジトリには該当なし）
  - 型 (b)（置き場所違い）の常設検査（`MSP/ADR-0069` フォローアップ 4。本作業の実測では該当 0 件のため
    現時点で新設の要否が無い。同型事故が実際に起きたら別途起票する）
  - planning への環流（`MSP/ADR-0069` 自身が計画側の確定裁定であるため、実装側からの指摘は無い）

## 着手前の実測（2026-09-03・`ecb96e6e`＝#529 第 3 段 PR #660 マージ後）

`find frontend/src -name .gitkeep` は **17 件**を返す。

| 場所 | 件数 |
| --- | ---: |
| `src/` 最上位（`app` `assets` `config` `locales` `stores` `types` `utils`） | 7 |
| `src/features/sc01-settings/{hooks,stores}` | 2 |
| `src/features/sc02-risk-settings/{api,hooks,stores,types}` | 4 |
| `src/features/sc03-controls/{api,hooks,stores,types}` | 4 |

いずれも同階層に追跡下の他ファイルが無い（真に空）。

## 分類（`MSP/ADR-0069` 決定 3 の (a)/(b)）

17 件すべてを実測した。**(b)（関心はあるが置き場所が違う）は 0 件だった。** 理由: #529 の PR①（骨格）
が着手前に `features/risk` `features/monitor` `features/shared` `features/roles.ts` という**実際に
存在した (b) 型の誤配置**を `src/lib/` `src/components/` `src/hooks/` へ既に是正済みであり、
残る 17 件は「是正済みの残り」——最初から実体を伴わない (a) 型である。

| # | ディレクトリ | 分類 | 根拠（`.gitkeep` 本文の要約） |
| --- | --- | --- | --- |
| 1 | `src/app/` | (a) | 層としての実体は合成点 `src/features/index.ts`（`MSP/ADR-0067` 決定 4）。置き場が features 直下なのは参照面の都合であり、`app/` 自体に置くべき実体は無い |
| 2 | `src/assets/` | (a) | 自己ホスト資産を持たない（3 画面は素の HTML 要素で描画。`IADR-0288` 決定 6）。フォント・アイコンは基盤 SPA の資産を使う |
| 3 | `src/config/` | (a) | 実行時構成は基盤（`@foundation`）から受け取り、自前の構成を持たない |
| 4 | `src/locales/` | (a) | Lingui 未採用（`IADR-0288` 決定 6）。導入の入力が基盤の pnpm workspace にあり単独リポジトリでは解決できないため、この単位には i18n カタログという関心そのものが無い（表示文言は日本語直書き） |
| 5 | `src/stores/`（共有） | (a) | Zustand 未導入。サーバー状態は TanStack Query が持ち、画面をまたぐクライアント状態が無い |
| 6 | `src/types/`（共有） | (a)† | 共有型（BFF 契約型）は存在するが、正規化関数と不可分なため `src/lib/{risk,monitor}/contracts.ts` に値と同居させている（型だけを分離すると型と正規化が別ファイルへ割れ、片方だけ直る事故を招く）。`MSP/ADR-0069` 決定 4 は共有層区分が唯一の置き場ではないと定めており、`lib/`（他の shared 区分）への正当な同居は非適合ではない |
| 7 | `src/utils/`（共有） | (a)† | 同上。純関数（契約の正規化・判定）は `src/lib/{risk,monitor}/contracts.ts` に同居。ドメインに紐づかない汎用関数は現状無い |
| 8 | `sc01-settings/hooks/` | (a) | 画面内で閉じたフックが無い（サーバー状態は TanStack Query、フォームのローカル状態は各コンポーネントの `useState`） |
| 9 | `sc01-settings/stores/` | (a) | Zustand 未導入 |
| 10 | `sc02-risk-settings/api/` | (a) | 端点は 2 画面以上が消費するため、クエリ層は共有側 `src/lib/{risk,monitor}/queries.ts` にある。この feature 固有の api は無い |
| 11 | `sc02-risk-settings/hooks/` | (a) | 同 8 |
| 12 | `sc02-risk-settings/stores/` | (a) | 同 9 |
| 13 | `sc02-risk-settings/types/` | (a) | 契約型は共有側にあり、画面内だけで閉じる型（`ShortSellingState` 等）は使う側のコンポーネントに同居 |
| 14 | `sc03-controls/api/` | (a) | 同 10 |
| 15 | `sc03-controls/hooks/` | (a) | 同 8 |
| 16 | `sc03-controls/stores/` | (a) | 同 9 |
| 17 | `sc03-controls/types/` | (a) | 同 13 |

† **#6・#7 は本作業で最も判断が割れやすい 2 件である。** 文字どおりには「実体はあるがツリーが定めた
場所に無い」という (b) の定義文に一致するように読めるが、`MSP/ADR-0069` 決定 4 が「共有層の区分は
唯一の置き場ではない」と明示しており、`lib/` も shared 9 区分の 1 つである以上、**shared 区分間の
再配置は非適合ではない**（features/app への越境ではない）。加えて型と正規化ロジックを分離すると
`IADR-0290` が明示的に避けた分割事故を再導入する。**したがって物理的な移送は行わない**——移送すると
计画外の抽象化（値のためだけに型ファイルを新設する）になる。

**(b) が実際にあれば移送先は shared 側（`src/lib/` `src/components/` `src/hooks/`）であり、既存の
`import/no-restricted-paths` ゾーンがそのまま検査する。** #529 で配備済みのため、本作業で新設は不要。

## 作業内容

1. 17 件の `.gitkeep` と、その結果空になるディレクトリを削除する（`git rm`）。
2. `frontend/eslint.config.js` の無害性を確認する。
   - `FEATURE_AREA_DIRS` は `readdirSync('./src/features')` で実ディレクトリを走査するが、
     **feature の内部ディレクトリ（`hooks/` 等）は走査対象ではない**（走査対象は `sc01-settings` 等の
     feature 名そのもの）。feature 内部の枠撤去はこの走査に影響しない。
   - `SHARED_LAYER_DIRS` は名前の静的配列であり、`import/no-restricted-paths` の `zones[].target` /
     `from` はファイルパスに対する glob 一致であって、対象ディレクトリの存在を要求しない
     （存在しないディレクトリを指すゾーンは、単に一致するファイルが無いだけで規則自体は成立する）。
     **したがって `app/` `assets/` `config/` `locales/` `stores/`（共有）`types/`（共有）`utils/`
     （共有）が消えても ESLint は壊れない。**
   - 実測で確認する（`npm run lint` が緑のまま）。
3. **規則の実効性を再実測する。** `MSP/ADR-0069` の教訓（`resolver` が無いと静かに 0 件検査になる）
   に従い、`.gitkeep` 撤去後も陽性 6 ゾーンが機能することを一時プローブで確認し、削除する
   （`IADR-0290` 決定 9・10 の実効性実測を再現する。新しいゾーンは追加しないため、全ゾーンの再確認で足りる）。
4. 機械検査 `scripts/check-frontend-empty-frames.js` を新設する。
   - `frontend/src` 配下を再帰走査し、**追跡下ファイルが `.gitkeep` のみ（または 0 件）のディレクトリ**
     があれば非 0 で終了する。
   - `--self-test` で自己診断する（一時ディレクトリに `.gitkeep` だけのケースと実体ありのケースを
     作り、前者を検出・後者を見逃さないことを検証する）。
   - `scripts/README.md` に登録する。
   - `.github/workflows/ci.yml` の `static-checks` ジョブへ 1 ステップとして追加する
     （ジョブ名は変更しない。`check-workflow-job-refs` の対象を崩さない）。
   - `scripts/scripts.repo.test.js` に `--self-test` 呼び出しの回帰テストを追加する。
5. `IADR-0290` へ日付付き追記を行う（決定 2 の撤回と (a)/(b) 分類表）。新規 IADR は起こさない
   ——判断基準は `MSP/ADR-0069` の決定をそのまま実装側へ写しただけであり、覆す決定も新しい軸も無い。

## 挙動を変えないことの担保

- **本作業は削除と検査器の追加に限る。** 画面の振る舞い・BFF 契約・テストの `expect` は変更しない。
- 受け入れの証拠は既存テストが緑のまま・E2E が緑のままであることである。

## 受け入れ基準

- [ ] `frontend/src` 配下に `.gitkeep` のみのディレクトリが 0 件である
- [ ] 17 件それぞれの (a)/(b) 分類が本仕様書に記録されている
- [ ] `npm run typecheck` / `npm run lint` / `npm test` / Playwright E2E がいずれも緑
- [ ] `import/no-restricted-paths` の 6 ゾーンが引き続き機能することを実測で示す（プローブは削除済み）
- [ ] `scripts/check-frontend-empty-frames.js --self-test` が緑
- [ ] `ci.yml` の `static-checks` ジョブへ検査ステップが追加されている（ジョブ名は不変）
- [ ] `scripts/check-trace-blocks.js` / `check-adr-index-sync.js` / `check-doc-links.js` /
      `node scripts/scripts.test.js` が通る
- [ ] `IADR-0290` に決定 2 の撤回と分類表が追記されている
- [ ] PR は作成するがマージしない

## テスト方針

- **新しいテストを増やさない前提は #529 と異なる。** 本作業は検査器（`check-frontend-empty-frames.js`）
  を新設するため、その自己診断テストのみを追加する。既存の単体・E2E テストは変更しない。
