---
description: 各種仕様書（作業/機能/画面/通信/データ/技術/テスト/運用/セキュリティ/実装ADR ほか）をテンプレから作成する
argument-hint: "[種別] <FR-xx | UC-xx | SC-xx | topic>"
allowed-tools: Read, Grep, Glob, Write, Bash(ls:*), Bash(mkdir:*)
---

引数 `$ARGUMENTS`: 先頭に種別（省略時は `work`）、続けて起点 ID または概要。

種別・テンプレート・出力先の対応（必須/任意は `CLAUDE.md` の仕様書一覧を参照）:

| 種別 | 文書 | テンプレート | 出力先 | 粒度 |
| --- | --- | --- | --- | --- |
| `work` | 作業仕様書 | `spec_template.md` | `docs/specs/` | 作業（PR）単位 |
| `functional` | 機能仕様書 | `functional_spec_template.md` | `docs/functional/` | 機能（FR）単位 |
| `screen` | 画面仕様書 | `screen_spec_template.md` | `docs/screens/` | 画面（SC）単位 |
| `api` | 通信仕様書 | `api_spec_template.md` | `docs/api/` | API/IF 単位 |
| `data` | データ仕様書 | `data_spec_template.md` | `docs/data/` | エンティティ単位 |
| `tech` | 技術要件書 | `tech_requirements_template.md` | `docs/tech/` | リポ単位（原則1つ） |
| `test` | テスト仕様書 | `test_spec_template.md` | `docs/tests/` | 機能（FR）単位 |
| `operations` | 運用仕様書 | `operations_spec_template.md` | `docs/operations/` | リポ単位（原則1つ） |
| `security` | セキュリティ仕様書 | `security_spec_template.md` | `docs/security/` | リポ単位（原則1つ） |
| `adr` | 実装ADR | `adr_template.md` | `docs/adr/` | 決定単位（`IADR-XXXX` 採番） |
| `observability` | ログ・可観測性仕様書 | `observability_spec_template.md` | `docs/observability/` | 任意 |
| `authz` | 権限・認可仕様書 | `authz_spec_template.md` | `docs/authz/` | 任意 |
| `integration` | 外部連携仕様書 | `integration_spec_template.md` | `docs/integration/` | 外部システム単位 |
| `batch` | バッチ・ジョブ仕様書 | `batch_spec_template.md` | `docs/batch/` | ジョブ単位 |
| `migration` | 移行仕様書 | `migration_spec_template.md` | `docs/migration/` | 任意 |
| `error` | エラー・メッセージ仕様書 | `error_spec_template.md` | `docs/errors/` | リポ単位 |
| `infra` | インフラ・構成仕様書 | `infra_spec_template.md` | `docs/infra/` | 任意 |

手順:

1. 先頭トークンを種別として解決する。既知の種別でなければ `work`（作業仕様書）とみなし、全体を起点 ID/概要として扱う。
2. 対応テンプレート（`docs/templates/<...>`）を読む。
3. 計画リポジトリ（既定 `../project-planning`、submodule の場合 `planning/`）から、起点 ID に対応する計画書
   （要求・UC・画面・技術検討・ADR）を読み、各セクションの素案を埋める。
4. ファイル名を決めて作成する。
   - 通常: `<出力先>/<YYYYMMDD>_<概要のケバブケース>.md`。
   - リポ単位（`tech`/`operations`/`security`/`error`）: 既存があればそれを更新、無ければ既定名（例 `docs/operations/operations.md`）で作成。
   - `adr`: `docs/adr/` の既存 `IADR-\d{4}` から最大連番を調べ、次の連番（4桁ゼロ埋め）で `IADR-XXXX_<タイトルのケバブケース>.md` を作成する。欠番・重複を作らない。作成後 `docs/adr/README.md` の一覧に追記する。
5. メタ情報（`type`・`related_ids`・`plan_refs`・`created`/`updated`=本日・`status`=`draft`（ADR は `Proposed`））を埋める。起点 ID（FR/UC/SC/ADR）と計画書リンクを「起点となる計画書」欄に、関連仕様書へのリンクを「関連仕様」欄に記入する。
6. 作成したパスと未決事項を報告する。

注意:

- 実装着手前に少なくとも `work`（作業仕様書）を作成する（`CLAUDE.md` の最優先ルール）。
- 必須（機能/画面/通信/データ/技術/テスト/運用/セキュリティ/実装ADR）は対象が存在する限り作成・維持する。任意の各仕様書は必要に応じて作成する。
