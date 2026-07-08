# docs — 実装リポジトリのドキュメント

この実装リポジトリの仕様書置き場である。計画リポジトリ（`project-planning`）の上流ドキュメント（要求・UC・画面設計・技術検討・ADR）を、実装向けに**詳細化**した仕様書を管理する。**作業着手前に仕様書を作成し、それに沿って実装する**運用とする。

## 構成

```text
docs/
├── templates/    # 各仕様書のひな形（spec / functional / screen / api / data / tech / test /
│                 #   operations / security / adr / observability / authz / integration /
│                 #   batch / migration / error / infra）
├── specs/        # 作業仕様書（作業/PR 単位の横断仕様）
├── functional/   # 機能仕様書        ├── operations/    # 運用仕様書
├── screens/      # 画面仕様書        ├── security/      # セキュリティ仕様書
├── api/          # 通信仕様書        ├── adr/           # 実装ADR（IADR-XXXX）
├── data/         # データ仕様書      ├── observability/ # ログ・可観測性（任意）
├── tech/         # 技術要件書        ├── authz/         # 権限・認可（任意）
├── tests/        # テスト仕様書      ├── integration/   # 外部連携（任意）
│                                     ├── batch/         # バッチ・ジョブ（任意）
│                                     ├── migration/     # 移行（任意）
│                                     ├── errors/        # エラー・メッセージ（任意）
│                                     └── infra/         # インフラ・構成（任意）
```

## 必須の仕様書

対象が存在する限り作成・維持する。`/new-spec <種別> <ID|topic>` で作成。

| 種別 | 文書 | 出力先 | 粒度 | 計画書の一次情報 |
| --- | --- | --- | --- | --- |
| `work` | 作業仕様書 | `docs/specs/` | 作業/PR 単位 | 該当する FR/UC/SC |
| `functional` | 機能仕様書 | `docs/functional/` | 機能（FR）単位 | 02_requirements / 03_usecases / 04_workflows |
| `screen` | 画面仕様書 | `docs/screens/` | 画面（SC）単位 | 05_screens |
| `api` | 通信仕様書 | `docs/api/` | API/IF 単位 | 03_usecases / 04_workflows / 06_technical |
| `data` | データ仕様書 | `docs/data/` | エンティティ単位 | 02_requirements / 06_technical / 07_adr |
| `tech` | 技術要件書 | `docs/tech/` | リポ単位（1つ） | 06_technical / 07_adr / NFR |
| `test` | テスト仕様書 | `docs/tests/` | 機能（FR）単位 | 02_requirements（受け入れ基準）/ 03_usecases |
| `operations` | 運用仕様書 | `docs/operations/` | リポ単位（1つ） | NFR（運用・保守） |
| `security` | セキュリティ仕様書 | `docs/security/` | リポ単位（1つ） | NFR（セキュリティ）/ 07_adr |
| `adr` | 実装ADR（`IADR-XXXX`） | `docs/adr/` | 決定単位 | 06_technical / 07_adr（実装に閉じた判断） |

## 任意の仕様書

必要に応じて作成する。

| 種別 | 文書 | 出力先 |
| --- | --- | --- |
| `observability` | ログ・可観測性仕様書 | `docs/observability/` |
| `authz` | 権限・認可仕様書 | `docs/authz/` |
| `integration` | 外部連携仕様書 | `docs/integration/` |
| `batch` | バッチ・ジョブ仕様書 | `docs/batch/` |
| `migration` | 移行仕様書 | `docs/migration/` |
| `error` | エラー・メッセージ仕様書 | `docs/errors/` |
| `infra` | インフラ・構成仕様書 | `docs/infra/` |

## 補助成果物の自動生成

補助成果物は生成可能なら必ず生成し、CI（`.github/workflows/`）で自動更新する。

- **CHANGELOG**（`CHANGELOG.md`）: コミット履歴から自動生成（`scripts/gen-changelog.js` / `changelog.yml`）。
- **OpenAPI**（`docs/api/openapi.yaml`）: コードからの生成コマンドがあればそれを、無ければ通信仕様書から雛形を生成（`scripts/gen-openapi-skeleton.js` / `openapi.yml`）。

## 運用ルール

1. **作業着手前に必ず作業仕様書を作成する**（`/new-spec`）。
2. 必須の仕様書は対象が存在する限り作成・維持する。任意の仕様書は必要に応じて作成する。
3. 重要な実装判断は**実装ADR（`docs/adr/`、`IADR-XXXX`）に残す**。計画ADR（計画リポ `ADR-XXXX`）とは別系統。
4. すべての仕様書に起点 ID（FR/UC/SC/ADR）と計画書リンクを記入し、関連仕様書を相互リンクする。
5. 計画書の誤り・不足・新たな制約は `/plan-feedback` で計画リポジトリへ環流する。

詳細な開発規約は `CLAUDE.md` を参照すること。
