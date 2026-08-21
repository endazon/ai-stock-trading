<!-- trace:
ids: [FR-10, FR-12, FR-15, FR-19, FR-20, NFR]
adrs: [ADR-0029]
iadrs: []
specs: [20260720_required-spec-coverage-arbitration]
issues: [#211]
-->

# docs — 実装リポジトリのドキュメント

この実装リポジトリの仕様書置き場である。計画リポジトリ（`project-planning`）の上流ドキュメント（要求・UC・画面設計・技術検討・ADR）を、実装向けに**詳細化**した仕様書を管理する。**作業着手前に仕様書を作成し、それに沿って実装する**運用とする。

> **`docs/` は「人が読む生きた文書」である。** AI 向けの文脈資料・凍結記録（作業仕様書・実装ADR）はルート直下の [`.ai-context/`](../.ai-context/README.md) が持つ（資料再編の計画 ADR 決定 1）。この主従は README の但し書きではなくディレクトリで表す。

## 構成

```text
docs/
├── templates/    # 各仕様書のひな形（spec / functional / screen / api / data / tech / test /
│                 #   operations / security / adr / observability / authz / integration /
│                 #   batch / migration / error / infra / runbook / how_to）
├── functional/   # 機能仕様書        ├── operations/    # 運用仕様書
├── screens/      # 画面仕様書        ├── security/      # セキュリティ仕様書
├── api/          # 通信仕様書        ├── observability/ # ログ・可観測性（任意）
├── data/         # データ仕様書      ├── authz/         # 権限・認可（任意）
├── tech/         # 技術要件書        ├── integration/   # 外部連携（任意）
├── tests/        # テスト仕様書      ├── batch/         # バッチ・ジョブ（任意）
│                                     ├── migration/     # 移行（任意）
│                                     ├── errors/        # エラー・メッセージ（任意）
│                                     ├── infra/         # インフラ・構成（任意）
│                                     └── how-to/        # 手順ガイド（任意）

.ai-context/      # AI 向け文脈資料・凍結記録（docs/ とは主従が異なる。.ai-context/README.md 参照）
├── specs/        # 作業仕様書（作業/PR 単位の横断仕様）
└── adr/          # 実装ADR（IADR-XXXX）
```

`docs/` 直下には、上記の分類に属さない**リポジトリ横断の記録**を置く。仕様書ではないため
`/new-spec` の対象ではなく、テンプレートも持たない。

| ファイル | 内容 |
| --- | --- |
| `DEFINITION_OF_DONE.md` | 完了の定義（`/verify` が突き合わせる） |
| `ai-workflow.md` | 起票→実装→検証→レビュー→マージの運用全体 |
| `blocked-tasks.md` | **実機確認・権限が必要で AI が完了できない作業の一覧**。特定 PR の point-in-time 記録ではなく、解消されるまで参照され続ける。解消した項目は削除せず「解消済み」として日付とともに残す |

## 必須の仕様書

対象が存在する限り作成・維持する。`/new-spec <種別> <ID|topic>` で作成する。**必須は 10 種で、置き場は主従で 2 つに分かれる。**

### `.ai-context/` へ出す 2 種（AI 向け文脈資料・凍結記録）

| 種別 | 文書 | 出力先 | 粒度 | 計画書の一次情報 |
| --- | --- | --- | --- | --- |
| `work` | 作業仕様書 | `.ai-context/specs/` | 作業/PR 単位（**着手前に必須**） | 該当する機能要求 / UC / 画面 |
| `adr` | 実装ADR | `.ai-context/adr/` | 決定単位（重要判断ごとに必須） | 06_technical / 07_adr（実装に閉じた判断） |

**この 2 種は `docs/` に置かない。** 書いた時点の判断・経緯をそのまま保存する凍結記録であり、本文プロズを後から書き換えない。詳細は [`.ai-context/README.md`](../.ai-context/README.md)。

### `docs/` へ出す 8 種（人が読む生きた文書）

| 種別 | 文書 | 出力先 | 粒度 | 計画書の一次情報 |
| --- | --- | --- | --- | --- |
| `functional` | 機能仕様書 | `docs/functional/` | 機能要求 単位 | 02_requirements / 03_usecases / 04_workflows |
| `screen` | 画面仕様書 | `docs/screens/` | 画面 単位 | 05_screens |
| `api` | 通信仕様書 | `docs/api/` | API/IF 単位 | 03_usecases / 04_workflows / 06_technical |
| `data` | データ仕様書 | `docs/data/` | エンティティ単位 | 02_requirements / 06_technical / 07_adr |
| `tech` | 技術要件書 | `docs/tech/` | リポ単位（1つ） | 06_technical / 07_adr / 非機能要件 |
| `test` | テスト仕様書 | `docs/tests/` | 機能要求 単位 | 02_requirements（受け入れ基準）/ 03_usecases |
| `operations` | 運用仕様書 | `docs/operations/` | リポ単位（1つ） | 非機能要件（運用・保守） |
| `security` | セキュリティ仕様書 | `docs/security/` | リポ単位（1つ） | 非機能要件（セキュリティ）/ 07_adr |

> **機能仕様書・テスト仕様書の必須範囲（網羅裁定・作業仕様書 20260720）**:
> 機能仕様書（`docs/functional/`）とテスト仕様書（`docs/tests/`）は、**安全・統制の中核となる機能要求
> ＝リスク統制・ペーパートレード・バックテスト・取引ガード・段階ゲート**を
> 必須範囲とする（これらは設定駆動・横断的で、独立した機能/テスト仕様書が統制価値を持つため）。それ以外の実装済み機能要求は、
> 作業仕様書（`.ai-context/specs/`・PR 単位の point-in-time 記録）と xUnit テスト（起点 ID コメント付）を正の記録とし、機能/テスト
> 仕様書は任意とする。1 つのテスト/機能仕様書が関連する複数の機能要求をまとめてよい（例:
> [統制系コア（リスク統制・ペーパートレード・取引ガード・段階ゲート）のテスト仕様書](tests/FR-10_risk-guard-core-tests.md)）。
>
> **この裁定の内容は資料再編の前後で変わっていない。** 変わったのは作業仕様書の置き場（旧 `docs/specs/` →
> `.ai-context/specs/`）と、裁定 issue・作業仕様書を**本文のリンクではなく trace ブロックで引く**ことだけである。

## 任意の仕様書

必要に応じて作成する。

| 種別 | 文書 | 出力先 |
| --- | --- | --- |
| `observability` | ログ・可観測性仕様書 | `docs/observability/` |
| `authz` | 権限・認可仕様書 | `docs/authz/`（未作成。必要になった時点で作る） |
| `integration` | 外部連携仕様書 | `docs/integration/` |
| `batch` | バッチ・ジョブ仕様書 | `docs/batch/`（未作成。必要になった時点で作る） |
| `migration` | 移行仕様書 | `docs/migration/`（未作成。必要になった時点で作る） |
| `error` | エラー・メッセージ仕様書 | `docs/errors/`（未作成。必要になった時点で作る） |
| `infra` | インフラ・構成仕様書 | `docs/infra/` |
| `runbook` | 運用 Runbook（運用仕様書の**下位**にあたる手順書） | `docs/operations/` |
| `how-to` | 手順ガイド（開発環境の起動・デプロイ・ローカル基盤の立ち上げなど） | `docs/how-to/` |

> `operations` はリポ単位で 1 つと定めているため、手順書が複数必要になると置き場が無くなる。
> **状態の単一情報源は `operations.md` に置き、Runbook は手順に特化して複数存在してよい**。
> 本リポジトリの Runbook 例: [実弾解禁 Runbook](operations/live-trading-cutover-runbook.md)・
> [発注経路の区別と識別 Runbook](operations/broker-execution-paths-runbook.md)・
> [旧キュー削除 Runbook](operations/wolverine-queue-cleanup-runbook.md)。
>
> `how-to` は仕様ではなく作業手順の案内であり、起点 ID を持たないことがある。
> その場合はフロントマターの起点 ID を空にしてよい（他の仕様書と異なり必須としない）。

## 補助成果物の自動生成

補助成果物は生成可能なら必ず生成し、CI（`.github/workflows/`）で自動更新する。

- **CHANGELOG**（`CHANGELOG.md`）: コミット履歴から自動生成（`scripts/gen-changelog.js` / `changelog.yml`）。
- **OpenAPI**（`docs/api/openapi.yaml`）: コードからの生成コマンドがあればそれを、無ければ通信仕様書から雛形を生成（`scripts/gen-openapi-skeleton.js` / `openapi.yml`）。

## 運用ルール

1. **作業着手前に必ず作業仕様書を作成する**（`/new-spec work` → `.ai-context/specs/`）。仕様書なしで実装へ着手しない。
2. 必須の仕様書は対象が存在する限り作成・維持する。任意の仕様書は必要に応じて作成する。
3. 重要な実装判断は**実装ADR（`.ai-context/adr/`）に残す**。計画リポジトリの計画ADR とは別系統である。
4. 🔴 **`docs/` 配下の資料は、計画 ID・実装ADR・仕様書・他リポジトリの issue 番号を表示テキストへ書かない。**
   frontmatter 終端直後・最初の H1 の直前に置く **trace ブロック**（HTML コメント。1 文書 1 個）へ、非表示メタデータとして持つ
   （資料再編の計画 ADR 決定 4。機械検査は `scripts/check-trace-blocks.js`、CI の `trace-blocks` ジョブ）。
   - 書式は `ids` / `adrs` / `iadrs` / `specs` / `issues` の 5 キーをこの順ですべて 1 回ずつ持つ。
     裸の ID は本リポジトリの計画プロジェクトを指し、他プロジェクト・他リポジトリは短縮名で修飾する。
   - **可視のリンクとして張ってよいのは、同一リポジトリの `docs/` 配下だけである。**
     計画リポジトリの文書・`.ai-context/` の凍結記録・他リポジトリへはリンクを張らず、trace ブロックの
     該当キー（`adrs` / `iadrs` / `specs` / `issues`）へ入れる。
   - 表の中で ID を示す必要がある場合は、表の直後に隣接する **trace-table ブロック**へ置く（表セルには書かない）。
   - **`.ai-context/` 配下（凍結記録）には適用しない。** 計画 ID・実装ADR・issue 参照は本文にそのまま書く。
5. 計画書の誤り・不足・新たな制約は、**計画リポジトリ（`project-planning`）の GitHub issue で環流する**
   （`feedback.yml` テンプレート・`decision-needed` ラベル）。**起票前に同件の既存 issue を必ず検索する。**
   裁定の完了記録は計画リポジトリ側 `projects/ai-stock-trading/10_feedback/` に残り、本リポジトリには残さない。
6. 本リポジトリは計画リポジトリに依存しない（submodule は張らない）。計画書は GitHub 上の URL か
   隣接クローン（既定パス `../project-planning`。読み取り専用）で読む。

詳細な開発規約は `CLAUDE.md` を参照すること。
