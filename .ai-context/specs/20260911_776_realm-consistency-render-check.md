---
title: 配備される全経路が同じレルムを指すことを helm 描画で網羅検査し、AST 専用レルムの写しの位置づけを記録する
type: spec
status: done
related_ids: [NFR-05, NFR-06, FR-10, FR-13, FR-17, FR-19, FR-20, UC-06, SC-01, SC-02, SC-03, SC-04, ADR-0038, IADR-0324, IADR-0051, IADR-0093, IADR-0098, IADR-0176, IADR-0283]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0038_linked-deploy-auth-realm-is-the-platform-realm.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
---

# 仕様書: 配備される全経路のレルム一致を描画で機械検査する（#776）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0038**（基盤連結配備の利用者認証レルムは基盤レルムである。AST 専用レルムは単体起動・E2E 用に限る。
  Accepted・2026-09-11・環流 planning#597）。本作業は同 ADR の**フォローアップ 1**（決定 2 の網羅検査）と
  **決定 3 の記録**（写しの位置づけ）に当たる。
- 実装 ADR: [IADR-0324](../adr/IADR-0324_msp-linked-deploy-single-auth-realm.md)（本件の決定。計画 ADR-0038 が
  これを計画側の明文にした）。前提となる経路: [IADR-0051](../adr/IADR-0051_service-to-service-auth.md)（`ServiceAuth`）／
  [IADR-0093](../adr/IADR-0093_kb-writer-cross-realm-s2s.md)（KB の s2s）／
  [IADR-0098](../adr/IADR-0098_owner-realm-client.md)（Discord OwnerAuth）／
  [IADR-0176](../adr/IADR-0176_run-once-authorization-and-cronjob-token.md)（CronJob の token エンドポイント）／
  [IADR-0283](../adr/IADR-0283_deploy-value-preservation-and-kb-realm-fix.md)（KB レルム名の是正）。
- 非機能要件: 🔴 **起点 issue #776 の件名は `NFR-05` を掲げるが、計画の `NFR-05` は「証券口座・API 認証情報の**保管**
  （Vault で秘匿し、コード・リポジトリに含めない）」であり、本作業の内容ではない。** 内容に当たるのは
  **`NFR-06`（発注機能へのアクセス＝利用者本人のみ・Keycloak 認証）**であり、計画 ADR-0038 の §関連 も `NFR-06` を挙げている。
  **件名・ブランチ名は起点 issue の宣言（`NFR-05`）に揃えたまま**にし（既存履歴は書き換えない・PR と issue の
  対応を崩さない）、齟齬は後述「未決事項」に残す。
- 画面 / 要求 / UC: SC-01〜SC-04・FR-10 / FR-13 / FR-17 / FR-19 / FR-20・UC-06（利用者が統合 SPA から操作する経路。
  レルムがずれると 401 になり画面が開かない）。

## 目的・背景

計画 ADR-0038 決定 2 は「発行元と検証元が**配備された全経路**で同じレルムであることを**機械で確かめる**」ことを求め、
現在の実現手段を「**部分的である**」と評価している（同 ADR §統制と現在の実現手段）。

- 既存の描画検査（`helm.yml` の `Assert ServiceAuth token endpoint follows global.authAuthority (#736)`）は
  **env 名の許可リスト**（`ServiceAuth__ClientId` / `ServiceAuth__TokenEndpoint`）で書かれている。**経路が 1 つ増えるたびに
  検査を書き足さないと漏れる。**
- 実際、同型の破れが 2 回起きている —— **#456（CronJob の token エンドポイント）**と **#736（`auth: true` でない
  s2s 発信者 `trade-decision`）**。いずれも「導出元の設定が注入されない経路が 1 つ残り、そこだけコード既定の
  AST レルムへ倒れた」という**同じ壊れ方**である。
- 破れた向きは **fail-closed** である（issuer 不一致 → 401 → 周回は発注へ到達しない）。ADR-0038 決定 2 は
  「これは偶然ではなく、そう設計されていることを計画側でも明記する」と定めた。実装側（IADR-0324）にも明記する。

あわせて ADR-0038 決定 3 は `trading-owner` / `trading-service` と連結配備で使うクライアントの**正本を基盤レルムの宣言**と
定め、AST 専用レルムの同名ロールを**写し**と位置づけた。**写しのずれを検知する手段は無い**（同 ADR §残るもの）ため、
本作業では**位置づけをファイルへ記録する**ところまでを行う。

## 対象範囲

- 対象:
  1. `.github/workflows/helm.yml` に**値のパターン（`/realms/<name>`）で経路を列挙する**新しい描画検査を足す。
  2. `infra/keycloak/realm-export.json` の冒頭へ「ここにある `trading-owner` / `trading-service` と
     連結配備で使うクライアントは**写し**であり、正本は基盤レルムの宣言である」旨を記録する。
  3. [IADR-0324](../adr/IADR-0324_msp-linked-deploy-single-auth-realm.md) へ日付つき追記（計画 ADR-0038 が本 IADR を
     計画側の明文にしたこと・fail-closed は設計であること・決定 3 の写しの位置づけ）と、
     `.ai-context/adr/README.md` の索引行の更新。
  4. `.claude/rules/traceability.repo.md` の計画 ADR レンジを実測へ追随させる（`ADR-0001..0037` → `ADR-0001..0039`）。
     **ADR-0038 をコミット件名のスコープに書くため、実在性検査（`check-commit-messages.js`）が読む宣言レンジを
     先に更新しないと赤になる。**
- 対象外:
  - **写しのずれの機械突合**（ADR-0038 フォローアップ 2）。AST の CI から基盤リポジトリを読めないため受け皿は基盤側であり、
    本 issue は記録に留める（issue #776 本文の明示）。
  - **compose スタック（`docker-compose.yml`）のレルム表記**。`/realms/` を 9 箇所持つが、ADR-0038 決定 1 の
    「基盤と連結して配備するとき」は k8s の連結プロファイルを指し、compose は MSP#283 決定 2e で既に揃っている
    （IADR-0324 §背景）。**本検査は helm 描画に限る**（issue #776 の射程）。
  - **新しい IADR の起票**。本作業は ADR-0038 が既に確定させた決定の実装であり、**新たな決定を含まない**
    （検査の実装細目＝列挙の方法・期待値の取り方・下限件数は、本仕様書と `helm.yml` のコメントで再現できる）。
  - **`values-local.yaml` のリテラル 4 件をテンプレート導出へ変える改修**（後述「未決事項」）。

## 走査した母集合

ADR-0038 決定 2 の「配備された全経路」を**誤りの側から**引くため、**env 名ではなく値のパターン（`/realms/`）**で走査した。
走査は helm v4.2.1（CI の `azure/setup-helm` と同版）の描画に対して行った。

### 軸 1: 既定描画（本番 `values.yaml`）—— 13 件（期待レルム `ai-stock-trading`）

| kind / workload | env |
| --- | --- |
| Deployment / audit-service | `Auth__Authority` |
| Deployment / configuration-service | `Auth__Authority` |
| Deployment / cost-control-service | `Auth__Authority` |
| Deployment / cost-control-service | `ServiceAuth__TokenEndpoint` |
| Deployment / information-collection-service | `Auth__Authority` |
| Deployment / information-collection-service | `ServiceAuth__TokenEndpoint` |
| Deployment / market-monitor-service | `Auth__Authority` |
| Deployment / market-monitor-service | `ServiceAuth__TokenEndpoint` |
| Deployment / notification-service | `Notifications__Discord__OwnerAuth__TokenEndpoint` |
| Deployment / report-service | `Auth__Authority` |
| Deployment / report-service | `ServiceAuth__TokenEndpoint` |
| Deployment / risk-management-service | `Auth__Authority` |
| Deployment / trade-decision-service | `ServiceAuth__TokenEndpoint` |

### 軸 2: `values-local.yaml` 描画（経路B・連結配備）—— 17 件（期待レルム `platform`）

軸 1 の 13 件に加えて **4 件**。いずれも `values-local.yaml` の `extraEnv` に**リテラルで**置かれており、
**テンプレート導出ではない**（`--set global.authAuthority=…` に追随しない）。

| kind / workload | env | 由来 |
| --- | --- | --- |
| Deployment / information-collection-service | `KnowledgeBase__Auth__Authority` | `values-local.yaml` のリテラル |
| Deployment / report-service | `KnowledgeBase__Auth__Authority` | 同上 |
| Deployment / report-service | `LlmGateway__Auth__Authority` | 同上 |
| Deployment / trade-decision-service | `LlmGateway__Auth__Authority` | 同上 |

🔴 **この 4 件は #736 の検査（env 名の許可リスト）では 1 件も見えない。** 値のパターンで引いて初めて母集合へ入る。

### 軸 3: `values-local.yaml` ＋ 全フィーチャフラグ ON —— 18 件（期待レルム `platform`）

軸 2 の 17 件に加えて **1 件**。既定 `false` のフラグを ON にしないと**描画されない**ため、
軸 1・軸 2 だけでは検査の外に落ちる。**#456 で破れた当の経路である。**

| kind / workload | env | 描画条件 |
| --- | --- | --- |
| CronJob / trading-cycle-trigger | `TOKEN_ENDPOINT` | `tradingCycle.cronjob.enabled=true` |

### 軸 4: `--set global.authAuthority` で差し替えた既定描画 —— 13 件

軸 1 と同じ 13 件が**すべて差し替え後のレルムへ追随する**（本番描画にレルムのリテラルが 1 つも無いことの確認）。

### 除外したものと、その理由

| 除外 | 件数 | 理由 |
| --- | --- | --- |
| 値が空文字の `KnowledgeBase__Auth__Authority` / `LlmGateway__Auth__Authority`（既定描画） | 5 | **値に `/realms/` を含まない**（機能が無効＝レルムを指していない）。値のパターンで引く以上、自動的に母集合の外に落ちる |
| `valueFrom.secretKeyRef` で与える env（`ServiceAuth__ClientId` 等） | — | 描画にレルムが現れない（Secret 側の値であり、描画の検査対象にできない） |
| `docker-compose.yml` の `/realms/` | 9 | compose スタックは MSP#283 決定 2e で既に基盤レルムへ揃っており、本 issue の射程（helm 描画）外 |
| `scripts/e2e-local-infra.sh` の `/realms/${REALM_NAME}` | 1 | 単体 E2E の Keycloak 死活確認。**ADR-0038 決定 1 が AST 専用レルムを認めている用途**であり、連結配備の経路ではない |
| `deploy/helm/**` の**コメント中**の `/realms/` | 3 | 描画されない（`values.yaml` の注記・`values-local.yaml` の注記） |

**走査が母集合を取りこぼしていないことの確認**: 3 つの描画それぞれについて、`grep -c '/realms/'`（生の行数）と
列挙器の出力件数が一致する（13 / 17 / 18）。**すなわち描画中の `/realms/` の出現は 100% が Deployment / CronJob の
env であり、ConfigMap・Secret・注釈には 1 件も無い。**

## 設計

### 1. `helm.yml` の新しい step（検査の本体）

`Assert every rendered realm matches global.authAuthority (#776)` を、既存の #736 の step の**直後**に置く。

- **列挙器**（`enumerate`）: 描画を `---` で分割し、`kind: Deployment|CronJob|Job` の文書について
  **`/realms/` を含む行をすべて**拾う。直前の `- name:` を env 名として添える（`value:` 行でなければ `(非env行)` と
  記録する —— インラインスクリプト等に焼き込まれたレルムも取りこぼさない）。**env 名の許可リストを持たない。**
- **期待値**（`authority_of`）: `global.authAuthority` を **values ファイルから**取る（描画から取ると自己参照になり、
  「全部同じ間違ったレルム」を緑にしてしまう）。複数指定は後勝ちで、helm の `-f` の優先順位と同じにする。
- **判定**: 列挙したすべての行のレルムが期待レルムと一致すること。1 件でも違えば `::error::` で当該行を出して赤。
- **空振り防止**: 件数が下限（10 件）を下回ったら赤にする。**抽出が壊れると「差分なし＝緑」で静かに壊れる**ため
  （既存の `Assert values-local drops no env from prod default` と同じ作法）。
- **呼び出し**: 軸 1〜軸 4 の 4 描画に対して同じ関数を掛ける。

### 2. `infra/keycloak/realm-export.json` への記録

JSON にコメント構文は無く、**このファイルは Keycloak の `--import-realm` が実際に取り込む**
（`scripts/e2e-local-infra.sh` / Testcontainers の `KeycloakOwnerOnlyEndpointE2ETests`）。
**未知のトップレベルキー（`_comment` 等）を足すと import が落ちる可能性があり、ローカルで Keycloak を起動して
確かめられない**（本環境に docker デーモンが無い）。

→ **`RealmRepresentation` が持つ既定フィールドだけで書く**。

- ファイル冒頭（`"realm"` の直後）に **`"attributes"`** を置き、`_sourceOfTruth` / `_scope` の 2 キーで位置づけを記す。
  realm attributes は自由形式の `Map<String,String>` であり、**スキーマ上の正規のフィールド**である（import で
  そのまま格納され、トークン発行には影響しない）。
- あわせて `trading-owner` / `trading-service` の `description` と、連結配備で使う 2 クライアント
  （`ai-stock-trading-svc` / `ai-stock-trading-owner`）の `description` に「写し。正本は基盤レルムの宣言
  （`microservices-platform` リポジトリ `deploy/keycloak/microservices-platform-realm.json`）」を追記する。
  **基盤側の同ファイルは既に逆向きの記述（「MSP 連結配備では AST が本レルムで検証するため AST レルムから写す」）を
  持っており、表記を対にする。**
- `infra/README.md` の当該節にも同じ位置づけを書く（人が最初に読む場所）。

### 3. IADR-0324 への日付つき追記と索引更新

`.ai-context/` は凍結記録であり本文プロズを書き換えない。**`［YYYY-MM-DD 追記 / #NNN］` 書式の経過追記**で、
(a) 計画 ADR-0038 が本 IADR を計画側の明文にしたこと、(b) fail-closed は設計であること、
(c) 決定 3（正本は基盤レルム／AST 専用レルムのロールは写し）、(d) 本 issue で入れた網羅検査、を残す。
`.ai-context/adr/README.md` の IADR-0324 行にも同じ趣旨を 1 文で足す。

## 受け入れ基準

- [x] `helm.yml` の新 step が、描画中の `/realms/` を**値のパターンで**列挙し、`global.authAuthority` の
      レルムと違うものが 1 件でもあれば赤になる（**陰性対照を実行して赤を確かめる**）。
- [x] 既定描画（本番）についても同じ列挙を行い、全件が `ai-stock-trading`（既定のレルム）で一致する。
- [x] CronJob を含む描画（フラグ ON）も検査に掛かる（#456 の経路が母集合に入る）。
- [x] 検査の抽出が壊れたら（件数が下限未満）赤になる。
- [x] `helm lint --strict` が通り、`helm template` の 4 描画が develop と同じ結果を返す（**chart は 1 バイトも変えない**）。
- [x] `infra/keycloak/realm-export.json` は **JSON として妥当**で、`realm` / `roles` / `users` / `clients` の
      既存の値が変わらない（追加は `attributes` と `description` のみ）。
- [x] IADR-0324 に日付つき追記があり、索引行も追随している。
- [x] 文書系検査器（trace-blocks / doc-links / cross-repo-refs / plan-id-qualification / knowledge-graph /
      reading-budget / adr-index-sync）が通る。
- [x] `node scripts/check-commit-messages.js` が通る（`ADR-0038` の実在性を含む）。

## テスト方針

本作業の成果物は**ワークフローの検査 step** と**記録**であり、xUnit の写像先を持たない。
検証は **CI と同一のコマンドをローカルで実走する**ことで行う（証跡を PR 本文へ貼る）。

1. 新 step の本体をそのまま抽出して `bash` で実行し、4 描画すべてが緑になることを確かめる。
2. **陰性対照（mutation）**: `values-local.yaml` のリテラル 1 件（`KnowledgeBase__Auth__Authority`）を
   AST レルムへ書き換えて同じ step を走らせ、**赤になり当該行が名指しされる**ことを確かめてから元に戻す。
   **この 1 件は #736 の検査では検出できない**（env 名が許可リストに無い）ため、本検査の増分を直接示す。
3. `helm lint --strict`（既定・`values-local`）。
4. chart 無改修の確認: `helm template` の出力を develop と突き合わせる。
5. `node -e` で `realm-export.json` を読み、JSON 妥当性と既存キーの不変を確かめる。

## 計画書との差異

- 差異: なし。本作業は ADR-0038 決定 2・3 とフォローアップ 1 をそのまま実装する。
  **フォローアップ 2（写しのずれの突合）は実装しない**（issue #776 が明示的に射程外としている）。

## 未決事項

- 🔴 **`values-local.yaml` の 4 件のリテラル**（`KnowledgeBase__Auth__Authority` / `LlmGateway__Auth__Authority`）は
  テンプレート導出ではないため、`global.authAuthority` を動かしても追随しない。**本検査はそのずれを赤で捕まえるが、
  ずれを作らない構造にはしていない。** テンプレート導出へ寄せるかは別 issue とする（chart のバイト等価に触れるため、
  本 issue の射程〔検査を足す〕を超える）。
- 写しのずれの突合（ADR-0038 フォローアップ 2）の受け皿は基盤側である。基盤側 issue の起票は本 issue の外。
- 🔴 **起点 ID `NFR-05` の当て違い**（前掲「起点となる計画書」）。本作業は認証情報の**保管**（`NFR-05`）ではなく
  **アクセス制御**（`NFR-06`）に当たる。**件名は起点 issue に揃えた**ため恒久履歴には `NFR-05` が残る。
  **遡及書き換えはしない**（force push 禁止）。以後の同系統の作業は `NFR-06` を起点 ID に用いる。
  `related_ids` には両方を載せて機械集計の取りこぼしを避ける。
