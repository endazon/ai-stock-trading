# scripts — 補助スクリプト

補助成果物（CHANGELOG / OpenAPI）の生成・環境セットアップ・プロファイル適用を行う依存ゼロのスクリプト群。

**キット共通**（impl-handoff-kit の `repo-template/scripts/` 由来。文面・挙動をキットに揃える）:

| スクリプト | 役割 | 出力 |
| --- | --- | --- |
| `gen-changelog.js` | コミット履歴（`種別(起点ID): 要約`）から変更履歴を生成 | `CHANGELOG.md` |
| `gen-openapi-skeleton.js` | 通信仕様書（`docs/api/`）から OpenAPI 雛形を生成 | `docs/api/openapi.yaml` |
| `check-doc-links.js` | `docs/` 配下 Markdown の相対リンク（frontmatter の `plan_refs`/`related_specs`・本文リンク・インラインコードのパス）の実在を検査。破損があれば終了コード 1。**未 populate な submodule 配下は対象外にし、その件数を submodule 別に `notice` で報告する**（黙って飛ばすと「破損リンクはありません」が検査していない範囲まで含んだ断定になる） | 標準出力（レポート） |
| `check-permission-denials.js` | claude-code-action の実行ログ（`outputs.execution_file`）を読み、**権限拒否で実行できなかったツール**を名前と件数で報告（Bash は `Bash(git show \| diff)` のようにパイプ・置換の**全セグメント**を出す。引数は出さない）。**失敗判定は段階ポリシー**: 件数が許容値（既定 4、`PERMISSION_DENIALS_TOLERANCE` で変更可）を超えるか、拒否がターン数の半分以上なら終了コード 1。それ未満は警告（アノテーション + 実行サマリ）のみで終了コード 0——「成果物は正しいのに赤」の常態化は、拒否の赤を無視する学習を生み検査の目的を壊すため。`STRICT_PERMISSION_DENIALS=1` で「1 件でも失敗」の旧挙動に戻せる（実測: レビューが 17 件の拒否で潰れ、本文を書けないまま `success` で終了した事故が起点）。実行ログを読めない場合は `warn` を出して終了コード 0（fail-open）。**内訳は `$GITHUB_STEP_SUMMARY`（PR の Checks 画面から 1 クリック）にも書く**——ジョブログにしか無いと、AI 本文の「✅ 実測」との突き合わせができないため（issue #155）。`--self-test` で検証器自体も試験 | 標準出力＋実行サマリ |
| `check-action-versions.js` | ワークフローの `uses: <action>@vN` を集め、**メジャーバージョンの退行**を検出。`action-versions.json` の下限を下回る、または `--compare-with` で指定したディレクトリ（Dependabot 管理下のリポジトリ直下）より古ければ終了コード 1。Dependabot は github-actions エコシステムでは**リポジトリ直下しか走査しない**ため、配布テンプレートは自動追随しない（issue #148）。表に無いアクション・使われていない表エントリは `warn`。`--check-latest` で GitHub API から新しいメジャーを確認（warn のみ・fail-open）。`--self-test` で検証器自体も試験 | 標準出力（レポート） |
| `action-versions.json` | 上記の下限表（キット配布。**編集しない**）。本リポジトリ固有のアクションは `action-versions.repo.json`（companion）に書く。後述「リポジトリ固有の Actions を足す場所」 | — |
| `check-ai-workflow-config.js` | Claude 系ワークフローのツール許可設定を検査。`claude_args` の記法誤り（空白分割で無効化）・ブロック内コメント・「SDK を用意して実行ツールを許可していない」不一致・**実装用とレビュー用のスタック別実行ツールのドリフト**（片方にだけ `Bash(node:*)` が無い等）を検出。不備があれば終了コード 1。`--self-test` で検証器自体も試験 | 標準出力（レポート） |
| `lib/ci-annotate.js` | 検査器共通。警告を GitHub Actions のアノテーション（`::warning::` / `::notice::`）として出す。素の出力は緑ジョブのログに埋もれて読まれないため。ローカル実行時の見た目は従来どおり | — |
| `check-commit-messages.js` | コミット件名（`種別(起点ID): 要約`）の規約適合と ADR/IADR の実在性を検査。除外は `commit-allowlist.json`。**置換点**: 計画 ADR の名前空間は `PLAN_PROJECT`（既定 `ai-stock-trading`・環境変数で上書き可）が決める | 標準出力（レポート） |
| `validate-pipeline-config.js` | 宣言的パイプライン構成のスキーマ検証（`--self-test` で検証器自体も試験） | 標準出力（判定） |
| `scripts.test.js` | 上記スクリプト群と本リポジトリ固有スクリプトの単体テスト | 標準出力（判定） |
| `setup.sh` | 開発環境セットアップ（SessionStart hook / devcontainer から実行） | — |
| `apply-profile.sh` | `AI_SETUP.md` で宣言したプロファイルに応じてキットを構成（`.example` 有効化等） | `.ai-profile` |

**本リポジトリ固有**（ai-stock-trading の実装・運用に依存する。キットには無い）:

| スクリプト | 役割 | 出力 |
| --- | --- | --- |
| `check-consumer-endpoint-names.js` | サービスを跨ぐ MassTransit エンドポイント名（＝RabbitMQ キュー名）の衝突を検査（`--self-test` あり） | 標準出力（レポート） |
| `validate-runtime-scaffold.js` | 実行環境スキャフォールド（docker-compose / appsettings / `.env.example`）の静的検査 | 標準出力（レポート） |
| `check-banned-settled-cash-sources.js` | **決済済み資金（settled cash）の代替に使ってはならないブローカー値**（`MaxTrdQtys.MaxCashBuy` / `Funds.AvlWithdrawalCash` / `Funds.MaxWithdrawal`）の**コードとしての参照**を検出。コメント・XML ドキュメント中の言及は誤検出しない（禁止の理由を書けなくしないため）。とりわけ現金買付余力は現金口座では**未決済の売却代金を含む**のが通例であり、**分母に据えると GFV 回避ガードが GFV を許可する**（#425 / ADR-0025 / IADR-0165） | 標準出力（レポート） |
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
node scripts/check-action-versions.js              # Actions のバージョン退行を検査
node scripts/check-action-versions.js --compare-with-ref origin/develop  # 同期による巻き戻りを検査
node scripts/check-action-versions.js --check-latest  # 新しいメジャーが出ていないか確認
node scripts/check-permission-denials.js <log>     # 実行ログの権限拒否を検査（CI では自動実行）
node scripts/check-banned-settled-cash-sources.js  # 決済済み資金の代替値のコード参照を検査（#425）
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
>
> GitHub Actions 上では警告は **アノテーション**（`::warning::`）として出るため、ジョブログを
> 開かなくても PR の Checks 画面と実行サマリで気付ける。ファイル名・構成が固まったリポジトリは
> `STRICT_AI_WORKFLOW_CONFIG=1` で警告を失敗として扱える（既定はオフ。**本リポジトリは有効化済み**）。
>
> **`check-doc-links.js` の「対象外」表示に注意する。** PR CI は submodule を populate しないため、
> `planning/` 配下などへのリンクは**検査されない**。出力の `（未 populate の submodule 配下 N 件は
> 対象外 …）` はその範囲を示す。実際に ai-stock-trading では PR CI が planning 配下 753 件を毎回
> 飛ばし、その隙間に破損 20 件が蓄積した。PR 段階で検査したい場合は checkout に submodules と
> トークンを付けるか、定期ジョブ（`doc-links-planning`）の結果を確認すること。

## 検査（CI）

`ci.yml` が PR ごとに以下を実行する。**`scripts.test.js` は `scripts-tests` ジョブで走る**。

| ジョブ | 実行内容 |
| --- | --- |
| `scripts-tests` | `node scripts/scripts.test.js`（本 README のスクリプト群の横断テスト。`fetch-depth: 0` が必要） |
| `commit-messages` | `check-commit-messages.js`（コミット件名の規約と ADR/IADR 実在性） |
| `doc-links` | `check-doc-links.js`（相対リンクの実在） |
| `ai-workflow-config` | `check-ai-workflow-config.js --self-test` と本検査、および `check-action-versions.js`（Actions のバージョン退行。`fetch-depth: 0` が必要。**置換点**: `--compare-with-ref` は本リポの統合ブランチ `origin/develop`） |
| `pipeline-config` | `validate-pipeline-config.js --self-test` ＋ 実ファイル（`PIPELINE_CONFIG`。本リポは採用する） |
| `consumer-endpoint-names` | `check-consumer-endpoint-names.js --self-test` と本検査（本リポ固有） |
| `runtime-scaffold` | `validate-runtime-scaffold.js`（本リポ固有） |
| `banned-settled-cash-sources` | `check-banned-settled-cash-sources.js`（本リポ固有・#425） |
| `shell-scripts` | `k8s-local-deploy.test.sh` / `deploy/opend/entrypoint.test.sh`（本リポ固有） |

> `scripts.test.js` を CI に載せないと「誰かが手で叩いたときだけ走るテスト」になる。
> 実際に、CHANGELOG 生成が全面的に壊れる回帰が PR の CI をすべて green のまま通り抜けたことがある
> （`changelog.yml` は push でしか起動しないため、壊れるのはマージ後）。

### リポジトリ固有の Actions を足す場所

`action-versions.json` は**キットが配布する下限表**であり、キットの更新のたびに差し替わる。
実装リポジトリだけが使うアクション（デプロイ系・クラウド系など）を同ファイルへ直接追記すると、
`scripts.test.js` と同じく**バイト一致が崩れ、以後の同期で毎回手動マージが要る**。

固有の下限は **`scripts/action-versions.repo.json`** に置く。存在すれば `expected` / `$exempt`
をマージして読む（無ければ何もしない）。

```json
{
  "$comment": "本リポジトリ固有のアクション。キットの action-versions.json は編集しない。",
  "expected": { "azure/setup-helm": 5 },
  "$exempt": { "some/action": "タグ形式がメジャーを持たないため" }
}
```

追記しないと `… は action-versions.json に無いため下限を検査していない` の警告が
**アノテーションとして毎回出続ける**。常時出る警告は「読まなくてよいもの」として学習され、
`ci-annotate` を入れた目的（緑ジョブに埋もれる警告の可視化）そのものを損なう。

| 状態 | 挙動 |
| --- | --- |
| companion なし | 何もしない（キット既定） |
| `expected` / `$exempt` が両方とも空 | `warning:`（書き忘れの検出） |
| JSON として壊れている | **失敗**（黙って無視すると「置いたのに効かない」状態になる） |
| キットの下限を**下げて**いる | `warning:`（退行を検出できなくなる方向の変更のため） |
| git 未追跡 | `warning:`（CI に存在せず、追記した下限が効かない） |

> **このファイルも必ずコミットする。** 理由は `scripts.repo.test.js` と同じである。
>
> 本リポジトリは `azure/setup-helm`（`helm.yml`）を companion に登録済みである。

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
