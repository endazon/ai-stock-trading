# scripts — 補助スクリプト

補助成果物（CHANGELOG / OpenAPI）の生成・環境セットアップ・プロファイル適用を行う依存ゼロのスクリプト群。

**キット共通**（impl-handoff-kit の `repo-template/scripts/` 由来。文面・挙動をキットに揃える）:

| スクリプト | 役割 | 出力 |
| --- | --- | --- |
| `gen-changelog.js` | コミット履歴（`種別(起点ID): 要約`）から変更履歴を生成 | `CHANGELOG.md` |
| `gen-openapi-skeleton.js` | 通信仕様書（`docs/api/`）から OpenAPI 雛形を生成 | `docs/api/openapi.yaml` |
| `check-doc-links.js` | `docs/` 配下 Markdown の相対リンク（frontmatter の `plan_refs`/`related_specs`・本文リンク・インラインコードのパス）の実在を検査。破損があれば終了コード 1 | 標準出力（レポート） |
| `check-commit-messages.js` | コミット件名（`種別(起点ID): 要約`）の規約適合と ADR/IADR の実在性を検査。除外は `commit-allowlist.json`。**置換点**: 計画 ADR の名前空間は `PLAN_PROJECT`（既定 `ai-stock-trading`・環境変数で上書き可）が決める | 標準出力（レポート） |
| `check-ai-workflow-config.js` | Claude 系ワークフローのツール許可設定を検査。`claude_args` の記法誤り（空白分割で無効化）・ブロック内コメント・「SDK を用意して実行ツールを許可していない」不一致・**実装用とレビュー用のスタック別実行ツールのドリフト**（片方にだけ `Bash(node:*)` が無い等）を検出。不備があれば終了コード 1。`--self-test` で検証器自体も試験 | 標準出力（レポート） |
| `validate-pipeline-config.js` | 宣言的パイプライン構成のスキーマ検証（`--self-test` で検証器自体も試験） | 標準出力（判定） |
| `scripts.test.js` | 上記スクリプト群と本リポジトリ固有スクリプトの単体テスト | 標準出力（判定） |
| `setup.sh` | 開発環境セットアップ（SessionStart hook / devcontainer から実行） | — |
| `apply-profile.sh` | `AI_SETUP.md` で宣言したプロファイルに応じてキットを構成（`.example` 有効化等） | `.ai-profile` |

**本リポジトリ固有**（ai-stock-trading の実装・運用に依存する。キットには無い）:

| スクリプト | 役割 | 出力 |
| --- | --- | --- |
| `check-consumer-endpoint-names.js` | サービスを跨ぐ MassTransit エンドポイント名（＝RabbitMQ キュー名）の衝突を検査（`--self-test` あり） | 標準出力（レポート） |
| `validate-runtime-scaffold.js` | 実行環境スキャフォールド（docker-compose / appsettings / `.env.example`）の静的検査 | 標準出力（レポート） |
| `k8s-local-deploy.sh` / `k8s-local-deploy.test.sh` | ローカル k8s へのデプロイと、その `ast-secrets` 同期の Bash テスト（kubectl スタブ・実クラスタ不要） | — |
| `k8s-local-images.sh` | ローカル k8s へのイメージ投入（Rancher=nerdctl / Docker Desktop=k3d import を自動判定） | — |
| `opend-build.sh` | moomoo OpenD コンテナのビルド | — |
| `e2e-local-infra.sh` | 実コンテナ統合 E2E 用のローカル基盤起動 | — |
| `scripts.repo.test.js` | 上記の本リポ固有スクリプトのテスト。`scripts.test.js` から自動で読み込まれる（キット提供の受け口） | 標準出力（判定） |

## プロファイルの適用

利用可能な AI（`claude-code` / `api` / `copilot`）を `AI_SETUP.md` で宣言し、対応する構成を適用する。

```bash
bash scripts/apply-profile.sh claude-code          # サブスクリプション
bash scripts/apply-profile.sh api                  # Anthropic API
bash scripts/apply-profile.sh --prune copilot      # Copilot のみ（Claude 系を削除）
```

## 使い方（ローカル）

```bash
node scripts/gen-changelog.js --out CHANGELOG.md
node scripts/gen-openapi-skeleton.js --src docs/api --out docs/api/openapi.yaml
node scripts/check-doc-links.js                    # 仕様書の相対リンク切れを検査（再発防止）
node scripts/check-ai-workflow-config.js           # AI ワークフローのツール許可設定を検査
node scripts/scripts.test.js                       # 上記スクリプト群の単体テスト
```

> `check-ai-workflow-config.js` は、AI レビュー / 実装が「ジョブは成功するのに検証を実行できない」
> 状態に陥る設定不備を機械的に止める。失敗モードの一覧は `impl-handoff-kit/HOWTO.md` の
> 付録3（トラブルシューティング）を参照。
>
> **警告（`warn`）も読むこと。** 本検証器は「検査そのものが効いていない」状態を warn で報告する
> （既定名のファイルがあるのに `claude_args` を解析できない／既定名で 2 ファイルを引き当てられず
> ドリフト検査が動かない）。exit 0 のままなので CI は緑になるが、その間は記法検査もドリフト検査も
> 実行されていない。ERROR にしないのは、アクションの入力名変更で全リポジトリの CI が一斉に
> 落ちるのを避けるため（fail-open）である。

## 検査（CI）

`ci.yml` が PR ごとに以下を実行する。**`scripts.test.js` は `scripts-tests` ジョブで走る**。

| ジョブ | 実行内容 |
| --- | --- |
| `scripts-tests` | `node scripts/scripts.test.js`（本 README のスクリプト群の横断テスト。`fetch-depth: 0` が必要） |
| `commit-messages` | `check-commit-messages.js`（コミット件名の規約と ADR/IADR 実在性） |
| `doc-links` | `check-doc-links.js`（相対リンクの実在） |
| `ai-workflow-config` | `check-ai-workflow-config.js --self-test` と本検査 |
| `pipeline-config` | `validate-pipeline-config.js --self-test` ＋ 実ファイル（`PIPELINE_CONFIG`。本リポは採用する） |
| `consumer-endpoint-names` | `check-consumer-endpoint-names.js --self-test` と本検査（本リポ固有） |
| `runtime-scaffold` | `validate-runtime-scaffold.js`（本リポ固有） |
| `shell-scripts` | `k8s-local-deploy.test.sh` / `deploy/opend/entrypoint.test.sh`（本リポ固有） |

> `scripts.test.js` を CI に載せないと「誰かが手で叩いたときだけ走るテスト」になる。
> 実際に、CHANGELOG 生成が全面的に壊れる回帰が PR の CI をすべて green のまま通り抜けたことがある
> （`changelog.yml` は push でしか起動しないため、壊れるのはマージ後）。

### リポジトリ固有のテストを足す場所

`scripts.test.js` は**キットが配布する共通テスト**であり、キットの更新のたびに差し替わる。
自前スクリプトの検査を同ファイルへ直接追記すると、同期のたびに手動マージが要り、
キットが同じテストを取り込んだ際に重複も生じる（重複はテストが落ちないため気付きにくい）。

固有テストは **`scripts/scripts.repo.test.js`** に置く。`scripts.test.js` が存在すれば自動で
読み込む（無ければ何もしない）。これにより `scripts.test.js` をキットとバイト一致に保て、
同期は上書きコピー 1 回で済む。

```js
// scripts/scripts.repo.test.js
module.exports = ({ ok, assert }) => {
  ok('本リポ固有の検査', () => {
    assert.ok(true);
  });
};
```

`ok` をそのまま受け取るため、件数の集計は自動で正しくなる（カウンタが分かれない）。

> **このファイルは必ずコミットする。** 追跡されていないと CI（clean checkout）に存在せず、
> 固有テストが黙って走らなくなる。`scripts.test.js` は未追跡を検出して警告するが、
> `.gitignore` を確認しておくこと。
> `.local` を名前に使わないのは、多くのプロジェクトで「コミットしない」の目印だからである
> （キット自身も `CLAUDE.local.md` をその意味で使っている）。旧名 `scripts.local.test.js` は
> 移行のあいだ読み込むが、改名を促す警告を出す。

**消失を検出したい場合**（固有テストを持つリポジトリ向け）: `ci.yml` の `scripts-tests` ジョブで
`REQUIRE_REPO_TESTS=1` を設定すると、companion が見つからないときに失敗する。未設定だと
誤削除やマージ事故でテスト件数が静かに減るだけで CI は green のままになる。
**companion があるのに未設定の場合は `notice:` で促す**（失敗はさせない）。

`scripts.test.js` が検出して知らせる状態は以下のとおり。

| 状態 | 挙動 |
| --- | --- |
| companion なし | 何もしない（キット既定） |
| companion なし ＋ `REQUIRE_REPO_TESTS=1` | **失敗**（消失の検出） |
| companion あり・登録 0 件 | **失敗**（export 忘れ・空実装・全件 skip） |
| companion あり ＋ `REQUIRE_REPO_TESTS` 未設定 | `notice:` で設定を促す（**本リポジトリは設定済み**のため出ない） |
| companion が git 未追跡 | `warning:`（CI に存在せず固有テストが走らないため） |
| 旧名 `scripts.local.test.js` のみ | 読み込む ＋ `warning:` で改名を促す |
| 新旧が**両方**ある | 新名を優先して読み込み、`warning:` で旧名の残存を知らせる（移行漏れならテストを移し、不要なら削除する） |

## 自動生成（CI）

- `.github/workflows/changelog.yml`: `main` への push で CHANGELOG を再生成しコミットする。タグ push でリリースノートも生成する。
- `.github/workflows/openapi.yml`: OpenAPI を生成する。コードからの生成コマンド（`scripts/generate-openapi.sh` または変数 `OPENAPI_GENERATE_CMD`）が設定されていればそれを実行し、無ければ通信仕様書からの雛形生成にフォールバックする（「生成可能なら必ず生成」）。

> OpenAPI をコードから生成する場合は `scripts/generate-openapi.sh` を用意する（例: `dotnet swagger tofile ...` / `npx ...`）。未整備でも雛形は通信仕様書から生成される。

## スタック・プロジェクト依存の置換点

キット雛形をそのまま使えない箇所（HOWTO Part B-5 の差し替え表に相当）。移設・改名時はここを見る。

| 置換点 | 本リポジトリの値 | 外すとどうなるか |
| --- | --- | --- |
| `check-commit-messages.js` の `PLAN_PROJECT` | `ai-stock-trading` | 計画 ADR の実在性検査が他プロジェクトの番号帯まで受理し、誤った ADR を名乗る件名を検出できなくなる |
| `openapi.yml` の `paths:` | `backend/**` | コードを変更しても OpenAPI 生成が起動しない（**失敗せず単に走らない**ため気付きにくい） |
| `ci.yml` / `claude-*.yml` / `.claude/settings.json` のビルド系コマンド | `dotnet ... backend/backend.slnx` | 3 系統のいずれかが欠けると AI が検証を実行できない（`ai-workflow-config` ジョブが検出する） |

## 上流テンプレートとの関係

これらの仕組みの単一情報源は上流テンプレート **impl-handoff-kit**（`planning/tools/impl-handoff-kit/repo-template/`）である。
「キット共通」の行を変更するときは、まずキット側へ `/plan-feedback` で環流し、キットが正となる状態を保つこと。
キット側の文面に他プロジェクト固有の Issue 番号・PR 番号・コミット SHA が含まれる場合は、本リポジトリでは
実在するファイル名で説明する（本リポジトリで解決できない番号を持ち込まない）。
