# scripts — 補助スクリプト

補助成果物（CHANGELOG / OpenAPI）の生成・環境セットアップ・プロファイル適用を行う依存ゼロのスクリプト群。

**キット共通**（impl-handoff-kit の `repo-template/scripts/` 由来。文面・挙動をキットに揃える）:

| スクリプト | 役割 | 出力 |
| --- | --- | --- |
| `gen-changelog.js` | コミット履歴（`種別(起点ID): 要約`）から変更履歴を生成 | `CHANGELOG.md` |
| `gen-openapi-skeleton.js` | 通信仕様書（`docs/api/`）から OpenAPI 雛形を生成 | `docs/api/openapi.yaml` |
| `check-doc-links.js` | 追跡下 Markdown の相対リンク（frontmatter の `plan_refs`/`related_specs`・本文リンク・インラインコードのパス）の実在を検査。破損があれば終了コード 1。**未 populate な submodule 配下は対象外にし、その件数を submodule 別に `notice` で報告する**（黙って飛ばすと「破損リンクはありません」が検査していない範囲まで含んだ断定になる。**本リポジトリは submodule を持たないため、この除外は現在発生しない**） | 標準出力（レポート） |
| `check-permission-denials.js` | claude-code-action の実行ログ（`outputs.execution_file`）を読み、**権限拒否で実行できなかったツール**を名前と件数で報告（Bash は `Bash(git show \| diff)` のようにパイプ・置換の**全セグメント**を出す。引数は出さない）。**失敗判定は段階ポリシー**: 件数が許容値（既定 4、`PERMISSION_DENIALS_TOLERANCE` で変更可）を超えるか、拒否がターン数の半分以上なら終了コード 1。それ未満は警告（アノテーション + 実行サマリ）のみで終了コード 0——「成果物は正しいのに赤」の常態化は、拒否の赤を無視する学習を生み検査の目的を壊すため。`STRICT_PERMISSION_DENIALS=1` で「1 件でも失敗」の旧挙動に戻せる（実測: レビューが 17 件の拒否で潰れ、本文を書けないまま `success` で終了した事故が起点）。実行ログを読めない場合は `warn` を出して終了コード 0（fail-open）。**内訳は `$GITHUB_STEP_SUMMARY`（PR の Checks 画面から 1 クリック）にも書く**——ジョブログにしか無いと、AI 本文の「✅ 実測」との突き合わせができないため（issue planning#155）。`--self-test` で検証器自体も試験 | 標準出力＋実行サマリ |
| `check-review-verdict.js` | **AI レビューが判定を投稿しないまま `success` で終わる形**を検出し、ジョブを落とす。**「緑だが検査されていない」を止める。** 実測では同一 PR で 3 回連続これが起き、うち 2 回は `success`、**判定が 1 つも無いまま PR がマージされた**（planning#333）。`check-permission-denials.js` では捕まらない —— あちらは「ツールを 1 つも実行できなかった」形、こちらは**ツールは動いたが最後の投稿だけが無い**形である。入力（実行ログ）の形は同じで `parseEvents` を借りるため**2 本セットで配布する**。**ただし配線先は、判定の書式を `prompt:` で強制しているレビュー用ワークフローに限る** —— 実装依頼へ応答するワークフロー（`claude-coding`）は判定を出さないため、配線すると恒常的に赤くなる（planning#355）。**検出は見出しの構造で行い、絵文字と語の両方を要求する** —— 語だけで探すと「**重大**な問題は無い」という散文で緑になる（planning#319 知見 3 で実証済みの同型のアンチパターン）。**判定 3 種がそろって初めて緑**とする。**プロンプトの「出力フォーマット」節と対であり、書式を変えるときは `VERDICTS` も同時に直すこと** —— 両者が離れると恒久的な偽陽性になり、**検査器そのものが外される**。**偽陰性より偽陽性へ倒す判断である**（落ちれば人が気付くが、緑の素通りは誰も気付かない）。なお planning#313 の「検査器にしてよいのは例外が無いと言い切れる規則だけ」とは緊張する —— **書式は例外が無いと言い切れる規則ではなく、プロンプトと検査器を同時に直す運用で支える**。実行ログを読めないときは warn ＋ exit 0（fail-open。その形は隣の検査器が捕まえる）。`ALLOW_MISSING_VERDICT=1` で警告のみにできる。`--self-test`（12 件） | 標準出力（レポート） |
| `check-ci-latency.js` | **CI の「逆転」を検知する** —— 監視対象（既定 `build-and-test`）の**中央値**が基準（既定 `claude-review`）の**最小値**を超えたら終了コード 1。**しきい値を固定値で持たない**（基準側も同じ run 群から実測する自己校正。固定値は環境とコードベースの変化で必ず腐る）。**非対称は意図的**——対象を中央値にするのはランナーの当たり外れ 1 本で鳴らさないため、基準を最小値にするのは密なループではPR が小さくレビューが下限へ寄るためである。🔴 **測るのは check-run 自身の所要ではなく、その head の check 群が動き出してからの完了オフセット** —— `needs:` で脚を束ねた集約ジョブは自身の所要が十数秒しかなく、実装リポジトリでは**実際の 1/8**（15 秒 vs 126 秒）に見えて監視が永久に鳴らなかった。🔴 **中央値が最小の 2 倍以上なら判定を skip する**（定常性の門）—— CI を作り変えた直後は中央値が「消えたはずの旧構成」を指し、**直したばかりの CI を「遅い」と起票する**（実装リポジトリで実測: サンプル 12 本中**11 本が最適化前**、中央値 456 秒 vs 現在 126 秒）。監視が最初の 1 回で狼少年になれば以後の本物も無視される。代償として、窓の途中で実際に 2 倍へ伸びた場合も検知が 1 週遅れる（速さの監視は fail-open という方針と同じ向き）。`GITHUB_REPOSITORY` / `GITHUB_TOKEN` が無い・API が引けない・サンプル不足はいずれも fail-open だが、**skip したことは必ず出力する**（黙って 0 件検査で緑を返さない）。`--report-only` で判定せず報告のみ。`--self-test`（**39 件**。上記の罠 4 つ〔集約ジョブの尺度・定常性・permissions 不一致・`sort=updated` の順序〕はすべて回帰テストとして固定してある。401/403 は設定の誤りとして赤くするが、**レート制限の 403 と、個別コミットが GC された 404 は一過性として落として続ける** —— どちらも待てば／放っておけば直る失敗であり、赤くすると誤起票になる） | 標準出力（レポート） |
| `check-action-versions.js` | ワークフローの `uses: <action>@vN` を集め、**メジャーバージョンの退行**を検出。`action-versions.json` の下限を下回る、または `--compare-with` で指定したディレクトリ（Dependabot 管理下のリポジトリ直下）より古ければ終了コード 1。Dependabot は github-actions エコシステムでは**リポジトリ直下しか走査しない**ため、配布テンプレートは自動追随しない（issue planning#148）。表に無いアクション・使われていない表エントリは `warn`。`--check-latest` で GitHub API から新しいメジャーを確認（warn のみ・fail-open）。`--self-test` で検証器自体も試験 | 標準出力（レポート） |
| `action-versions.json` | 上記の下限表（キット配布。**編集しない**）。本リポジトリ固有のアクションは `action-versions.repo.json`（companion）に書く。後述「リポジトリ固有の Actions を足す場所」 | — |
| `check-ai-workflow-config.js` | Claude 系ワークフローのツール許可設定を検査。`claude_args` の記法誤り（空白分割で無効化）・ブロック内コメント・「SDK を用意して実行ツールを許可していない」不一致・**実装用とレビュー用のスタック別実行ツールのドリフト**（片方にだけ `Bash(node:*)` が無い等）を検出。不備があれば終了コード 1。`--self-test` で検証器自体も試験 | 標準出力（レポート） |
| `check-cross-repo-refs.js` | **他リポジトリの issue / PR 番号の修飾**が規約（`.claude/rules/traceability.md`）どおりかを検査。4 つの型を見る——**長い表記の裸書き**／**列挙形の修飾漏れ**（先頭だけ修飾して後続を裸にする形。`〔〕` で添える注記も区切りとして見る）／**修飾語と番号が空白で離れる形**／**フルパス形式の owner 誤り**。**owner 誤りだけは実害の性質が違う**——他の 3 型は `.md` では表記ゆれに留まるが、**フルパス形式は `.md` でも自動リンクするため owner を誤ると死んだリンクになる**。**検査対象は表示テキストのみ**（インラインコード・コードフェンスの中は見ない＝ literal な引用は表記規約の対象外。これにより規約自身が反例を書ける）。閉じないコードフェンスは**違反として上げる**（黙ると以降のファイル全体が検査対象外になる）。**走査は追跡下の全ファイル**（`*.md` だけを見ていた頃はワークフロー YAML の中の違反を誰も見ていなかった。実測）。`EXCLUDED_DIRS` の**非 Markdown だけ**を外し（そこは検査器と自己試験フィクスチャの置き場所であり違反の文字列を書くことが仕事）、**除外件数をログに出す**（「検査していない」と「違反 0 件」を読み分けるため）。バイナリ（NUL を含むファイル）は読み飛ばす。**走査対象が 0 件なら fail させる**（fail-closed。0 件検査は「検査しているつもりで何も見ていない」状態であり、退行を止めているという記録だけが残る。`check-doc-links.js` / `check-plan-id-qualification.js` と横並びの作法）。**`git ls-files` を実行できない環境は従来どおり exit 0**（fail-open。両者は別の分岐である）。違反があれば終了コード 1。`--self-test` で検証器自体も試験。**配布時に冒頭の 置換点（`CROSS_REPOS` / `SELF_NAMES` / `EXCLUDE_PATHSPECS` / `EXCLUDED_DIRS` / `KNOWN_OWNERS` / `REPO_RELATIVE_PATHS`）を必ず書き換える**——とくに `SELF_NAMES` の書き忘れは正当な自リポ参照を大量に止め、検査そのものを外させる（実測 22 件）。**`KNOWN_OWNERS` はプレースホルダのままなら型 4 を検査しない**（検査すると規約が許す正しいフルパス形式を全件違反として上げ、同じく検査が外される）。検査していないことは実行時に notice で出す。環境変数 `CROSS_REPO_NAMES` / `CROSS_REPO_SELF_NAMES` / `CROSS_REPO_EXCLUDES` / `CROSS_REPO_EXCLUDED_DIRS` / `CROSS_REPO_OWNERS` / `CROSS_REPO_RELATIVE_PATHS` でも上書きできる。**規約に書くだけでは守られないことが実測で確かめられている**（規約の書いてある当のファイルを編集する PR が同じ違反を犯し CI を green で通過した。158 occurrence が蓄積） | 標準出力（レポート） |
| `lib/ci-annotate.js` | 検査器共通。警告を GitHub Actions のアノテーション（`::warning::` / `::notice::`）として出す。素の出力は緑ジョブのログに埋もれて読まれないため。ローカル実行時の見た目は従来どおり | — |
| `check-commit-messages.js` | コミット件名（`種別(起点ID): 要約`）の規約適合と ADR/IADR の実在性を検査。除外は `commit-allowlist.json`。**置換点**: 計画 ADR の名前空間は `PLAN_PROJECT`（既定 `ai-stock-trading`・環境変数で上書き可）が決める | 標準出力（レポート） |
| `validate-pipeline-config.js` | 宣言的パイプライン構成のスキーマ検証（`--self-test` で検証器自体も試験） | 標準出力（判定） |
| `scripts.test.js` | 上記スクリプト群と本リポジトリ固有スクリプトの単体テスト | 標準出力（判定） |
| `setup.sh` | 開発環境セットアップ（SessionStart hook / devcontainer から実行） | — |
| `apply-profile.sh` | `AI_SETUP.md` で宣言したプロファイルに応じてキットを構成（`.example` 有効化等） | `.ai-profile` |

**本リポジトリ固有**（ai-stock-trading の実装・運用に依存する。キットには無い）:

| スクリプト | 役割 | 出力 |
| --- | --- | --- |
| `check-trace-blocks.js` | `docs/**/*.md`（`.ai-context/` は対象外）に置く trace ブロック（`<!-- trace: -->`）・trace-table ブロック（`<!-- trace-table: -->`）を検査する（project-planning ADR-0029 決定4「trace ブロック規約」の検査の実現手段）。**文法**（frontmatter 直後・最初の H1 前に 1 文書 1 ブロック、`ids/adrs/iadrs/specs/issues` をこの順ですべて 1 回ずつ持つ）・**許可キー**（未知キーは error）・**trace-table の行番号連番**（`row1` から）・**値域**（`ids` の FR/UC/SC は `.claude/rules/traceability.repo.md` 宣言レンジ、`adrs` の計画 ADR は同宣言レンジ〔`scripts/lib/plan-ranges.js` 経由〕、`iadrs` は `.ai-context/adr/` のファイル実在、`NFR` は無採番許容）・**可視本文への残存**（HTML コメント外・コードフェンス外に計画 ID・IADR・修飾付き issue 参照が残っていれば error。裸の `#NNN` は対象外）を見る。他プロジェクト／他リポジトリの修飾子（`MSP:` 等）は個別名をハードコードせず「英字+英数字の短縮名 + `:`」の形だけで external と判定する（利用者裁定・ADR-0029 決定9）。trace ブロックを持たない文書は許容する。文法・分類ロジックは `scripts/lib/trace-blocks.js` が単一情報源（`gen-knowledge-graph.js` と共有）。`--self-test` あり | 標準出力（レポート） |
| `gen-knowledge-graph.js` | `docs/` の trace / trace-table ブロックと `.ai-context/{adr,specs}/` の frontmatter（`related_ids`/`related_specs`/`plan_refs`）からナレッジグラフ（ノード／エッジ）を組み立てる。`--json` で JSON、`--mermaid [--scope <dir>]` で Mermaid `graph LR`（scope 指定でそのディレクトリ配下のノードとそれに繋がる参照へ絞り込む）、`--check` で in-repo 実在検査（計画 ID・計画 ADR はレンジ、IADR はファイル実在。`planning:`/`MSP:` 等の修飾付き参照は external として数のみ数える。specs / related_specs の基準名が解決できないものは計画リポジトリ側の文書等を指し得るため失敗にしない）を行う。生成物はコミットしない（都度標準出力へ書く）。`--self-test` あり | 標準出力（JSON / Mermaid / レポート） |
| `check-consumer-endpoint-names.js` | サービスを跨ぐ MassTransit エンドポイント名（＝RabbitMQ キュー名）の衝突を検査（`--self-test` あり） | 標準出力（レポート） |
| `validate-runtime-scaffold.js` | 実行環境スキャフォールド（docker-compose / appsettings / `.env.example`）の静的検査 | 標準出力（レポート） |
| `check-banned-settled-cash-sources.js` | **決済済み資金（settled cash）の代替に使ってはならないブローカー値**（`MaxTrdQtys.MaxCashBuy` / `Funds.AvlWithdrawalCash` / `Funds.MaxWithdrawal`）の**コードとしての参照**を検出。コメント・XML ドキュメント中の言及は誤検出しない（禁止の理由を書けなくしないため）。とりわけ現金買付余力は現金口座では**未決済の売却代金を含む**のが通例であり、**分母に据えると GFV 回避ガードが GFV を許可する**（#425 / ADR-0025 / IADR-0165） | 標準出力（レポート） |
| `k8s-local-deploy.sh` / `k8s-local-deploy.test.sh` | ローカル k8s へのデプロイと、その `ast-secrets` 同期の Bash テスト（kubectl スタブ・実クラスタ不要） | — |
| `k8s-local-images.sh` | ローカル k8s へのイメージ投入（Rancher=nerdctl / Docker Desktop=k3d import を自動判定） | — |
| `opend-build.sh` | moomoo OpenD コンテナのビルド | — |
| `e2e-local-infra.sh` | 実コンテナ統合 E2E 用のローカル基盤起動 | — |
| `lib/trace-blocks.js` | `check-trace-blocks.js` / `gen-knowledge-graph.js` 共有。trace / trace-table ブロックのパーサと ID トークン分類（修飾子の汎用規則を含む）の単一情報源 | — |
| `lib/plan-ranges.js` | `check-trace-blocks.js` / `gen-knowledge-graph.js` 共有。計画 ADR の実在レンジを `.claude/rules/traceability.repo.md` から読む（`check-test-traceability.js` の `readPlanIds()`/`planRangeSection()` を拡張点として再利用。同ファイル自体は変更しない） | — |
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
node scripts/check-cross-repo-refs.js              # 他リポジトリ issue / PR 番号の修飾を検査
node scripts/check-cross-repo-refs.js --self-test   # 検査ロジック自体の自己試験
node scripts/check-banned-settled-cash-sources.js  # 決済済み資金の代替値のコード参照を検査（#425）
node scripts/check-trace-blocks.js                 # docs/ の trace ブロック規約を検査（ADR-0029 決定4）
node scripts/gen-knowledge-graph.js --json          # ナレッジグラフを JSON で出力
node scripts/gen-knowledge-graph.js --mermaid --scope docs/functional  # 一部スコープを Mermaid で出力
node scripts/gen-knowledge-graph.js --check         # in-repo 実在検査
node scripts/scripts.test.js                       # 上記スクリプト群の単体テスト
```

> **［2026-08-21 変更］計画リポジトリを参照する検査器は `check-test-traceability.js` だけになった。**
> 旧は `check-kit-sync.js` / `check-feedback-status-sync.js` / `check-planning-pin-freshness.js` も
> 計画リポジトリを見ており、**参照できないとき既定で skip する**ため CI では `--require-planning` を
> 必ず付ける運用だった（付け忘れると「配線したのに一度も検査していない」まま緑になる。planning#343）。
> 資料再編（計画 ADR 決定 2・5・6）で 3 本とも退役したため、**残る `--require-planning` は
> `check-test-traceability.js` の 1 本**である。**ローカルでは付けない**（隣接クローンが無い環境で
> 落ちるだけである）。

### 検査器を書くときの規約（fail-open の閉じ方。裁定 planning#343）

**外部の存在（隣接クローン・実行ログ。かつては submodule も）に依存して skip する検査器は、
`--require-planning` に相当する「fail-closed へ倒すフラグ」を必ず持たせる。**
持たせないと、各リポジトリが個別に気付いて CI ジョブ側で塞ぐしかなくなる。

- **未知の引数は黙って無視せず、設定誤りとして落とす。** これが本規約の要である ——
  フラグを持たない版が**フラグを黙って無視した**ために、「CI は渡し続けているのに効いていない」
  状態が生まれた。**`run:` 行に文字列が在ることしか見ていない回帰テストは、配線が効いている
  ことを保証しない。**
- **フラグの効きは実走で固定する**（合成した「参照できない環境」で `exit 1` になること）。
  配線を見るテストでは捕まらない。
- **例外**: fail-open が事故ではなく**決定**である検査器には求めない（旧 `check-planning-pin-freshness.js`
  が「pin を進める判断は人が行う」ため常に exit 0 だった例。同スクリプトは pin ごと退役済み）。
  **その旨をヘッダへ書くこと。**

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
> **［2026-08-21 変更］`check-doc-links.js` の「対象外」表示は、いまは出ない。** 旧は planning を
> submodule で取り込んでおり、PR CI がそれを populate しないため `planning/` 配下へのリンク（実測
> 753 件）を毎回飛ばし、**その隙間に破損 20 件が蓄積した**。資料再編（計画 ADR 決定 2）で submodule を
> 撤去したため、新は**追跡下の全 Markdown が毎回検査される** —— 計画リポジトリへのリンクはそもそも
> 張らず、trace ブロックの `adrs` / `issues` へ入れる（`docs/traceability-appendix.md` §trace ブロック）。

## 検査（CI）

`ci.yml` が PR ごとに以下を実行する。**`scripts.test.js` は `scripts-tests` ジョブで走る**。

| ジョブ | 実行内容 |
| --- | --- |
| `scripts-tests` | `node scripts/scripts.test.js`（本 README のスクリプト群の横断テスト。`fetch-depth: 0` が必要） |
| `commit-messages` | `check-commit-messages.js`（コミット件名の規約、`ADR` / `IADR` と `FR` / `UC` / `SC` の実在性、および**件名・本文・PR タイトルの 3 面**での他リポジトリ番号の修飾） |
| `pr-title` | `check-commit-messages.js --title`（PR タイトル＝スカッシュ後件名）。**`PR_NUMBER` を渡すこと** —— 渡すと末尾の `(#NNN)` が **PR 自身の番号**かまで見る。**渡さないと形状しか見られず、起点 issue の番号を書いた PR が素通りする**（末尾の番号は GitHub がスカッシュ時に自動付加するものであり、書くと `… (#123) (#456)` と二重に付く）。**未設定なら従来どおり形状のみ**（コミット件名モードには PR 番号が無く、必須にすると履歴コミットが全滅する）。数値でない値は `::notice::` を出して skip（fail-open） |
| `doc-links` | `check-doc-links.js`（相対リンクの実在） |
| `adr-index-sync` | `check-adr-index-sync.js`（IADR 本文と索引行の同時変更） |
| `plan-id-qualification` | `check-plan-id-qualification.js`（他プロジェクトの計画 ID の `<PROJ>/<ID>` 修飾。`PLAN_ID_PREFIXES` を明示） |
| `reading-budget` | `check-reading-budget.js --self-test` と本検査（必読規約の総量予算。エージェントごとに判定・合算しない。#524） |
| `test-traceability` | `check-test-traceability.js --require-planning`（必須範囲の機能要求のテスト・仕様書の存在。本リポ固有。**計画リポジトリを参照する唯一の検査器**） |
| `banned-libraries` | `check-banned-libraries.js`（不採用ライブラリの再混入。本リポ固有） |
| `tracked-session-timeout` | `check-tracked-session-timeout.js`（本リポ固有） |
| `trace-blocks` | `check-trace-blocks.js --self-test` と本検査（docs/ の trace ブロック規約。ADR-0029 決定4・本リポ固有） |
| `knowledge-graph` | `gen-knowledge-graph.js --self-test` と `gen-knowledge-graph.js --check`（in-repo 実在検査。本リポ固有） |
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

「キット共通」と記した仕組みは、上流テンプレート **impl-handoff-kit**（計画リポジトリの
`tools/impl-handoff-kit/repo-template/`）に由来する。

**［2026-08-21 変更］キットは bootstrap（新規実装リポジトリの立ち上げ）専用であり、本リポジトリに
追随義務は無い**（資料再編の計画 ADR 決定 6）。旧はキットを単一情報源とし、共通行を直すときは
まずキット側へ環流してバイト一致を保つ運用だった（`check-kit-sync.js` が突合していた）。
**新はキットとの乖離を受容として記録する** —— 直す場所は本リポジトリであり、キット側へ戻すかどうかは
任意である。有用な是正はキットへの改善提案として計画リポジトリへ issue を起票してよい（`/plan-feedback`）。

キット由来の文面に他プロジェクト固有の Issue 番号・PR 番号・コミット SHA が残っている場合は、
本リポジトリで実在するファイル名・内容で説明し直す（本リポジトリで解決できない番号を持ち込まない）。
