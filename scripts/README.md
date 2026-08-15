# scripts — 補助スクリプト

補助成果物（CHANGELOG / OpenAPI）の生成・環境セットアップ・プロファイル適用を行う依存ゼロのスクリプト群。

**キット共通**（impl-handoff-kit の `repo-template/scripts/` 由来。文面・挙動をキットに揃える）:

| スクリプト | 役割 | 出力 |
| --- | --- | --- |
| `gen-changelog.js` | コミット履歴（`種別(起点ID): 要約`）から変更履歴を生成 | `CHANGELOG.md` |
| `gen-openapi-skeleton.js` | 通信仕様書（`docs/api/`）から OpenAPI 雛形を生成 | `docs/api/openapi.yaml` |
| `check-doc-links.js` | `docs/` 配下 Markdown の相対リンク（frontmatter の `plan_refs`/`related_specs`・本文リンク・インラインコードのパス）の実在を検査。破損があれば終了コード 1。**未 populate な submodule 配下は対象外にし、その件数を submodule 別に `notice` で報告する**（黙って飛ばすと「破損リンクはありません」が検査していない範囲まで含んだ断定になる） | 標準出力（レポート） |
| `check-permission-denials.js` | claude-code-action の実行ログ（`outputs.execution_file`）を読み、**権限拒否で実行できなかったツール**を名前と件数で報告（Bash は `Bash(git show \| diff)` のようにパイプ・置換の**全セグメント**を出す。引数は出さない）。**失敗判定は段階ポリシー**: 件数が許容値（既定 4、`PERMISSION_DENIALS_TOLERANCE` で変更可）を超えるか、拒否がターン数の半分以上なら終了コード 1。それ未満は警告（アノテーション + 実行サマリ）のみで終了コード 0——「成果物は正しいのに赤」の常態化は、拒否の赤を無視する学習を生み検査の目的を壊すため。`STRICT_PERMISSION_DENIALS=1` で「1 件でも失敗」の旧挙動に戻せる（実測: レビューが 17 件の拒否で潰れ、本文を書けないまま `success` で終了した事故が起点）。実行ログを読めない場合は `warn` を出して終了コード 0（fail-open）。**内訳は `$GITHUB_STEP_SUMMARY`（PR の Checks 画面から 1 クリック）にも書く**——ジョブログにしか無いと、AI 本文の「✅ 実測」との突き合わせができないため（issue #155）。`--self-test` で検証器自体も試験 | 標準出力＋実行サマリ |
| `check-review-verdict.js` | **AI レビューが判定を投稿しないまま `success` で終わる形**を検出し、ジョブを落とす。**「緑だが検査されていない」を止める。** 実測では同一 PR で 3 回連続これが起き、うち 2 回は `success`、**判定が 1 つも無いまま PR がマージされた**（planning#333）。`check-permission-denials.js` では捕まらない —— あちらは「ツールを 1 つも実行できなかった」形、こちらは**ツールは動いたが最後の投稿だけが無い**形である。入力（実行ログ）の形は同じで `parseEvents` を借りるため**2 本セットで配布する**。**ただし配線先は、判定の書式を `prompt:` で強制しているレビュー用ワークフローに限る** —— 実装依頼へ応答するワークフロー（`claude-coding`）は判定を出さないため、配線すると恒常的に赤くなる（planning#355）。**検出は見出しの構造で行い、絵文字と語の両方を要求する** —— 語だけで探すと「**重大**な問題は無い」という散文で緑になる（planning#319 知見 3 で実証済みの同型のアンチパターン）。**判定 3 種がそろって初めて緑**とする。**プロンプトの「出力フォーマット」節と対であり、書式を変えるときは `VERDICTS` も同時に直すこと** —— 両者が離れると恒久的な偽陽性になり、**検査器そのものが外される**。**偽陰性より偽陽性へ倒す判断である**（落ちれば人が気付くが、緑の素通りは誰も気付かない）。なお #313 の「検査器にしてよいのは例外が無いと言い切れる規則だけ」とは緊張する —— **書式は例外が無いと言い切れる規則ではなく、プロンプトと検査器を同時に直す運用で支える**。実行ログを読めないときは warn ＋ exit 0（fail-open。その形は隣の検査器が捕まえる）。`ALLOW_MISSING_VERDICT=1` で警告のみにできる。`--self-test`（12 件） | 標準出力（レポート） |
| `check-action-versions.js` | ワークフローの `uses: <action>@vN` を集め、**メジャーバージョンの退行**を検出。`action-versions.json` の下限を下回る、または `--compare-with` で指定したディレクトリ（Dependabot 管理下のリポジトリ直下）より古ければ終了コード 1。Dependabot は github-actions エコシステムでは**リポジトリ直下しか走査しない**ため、配布テンプレートは自動追随しない（issue #148）。表に無いアクション・使われていない表エントリは `warn`。`--check-latest` で GitHub API から新しいメジャーを確認（warn のみ・fail-open）。`--self-test` で検証器自体も試験 | 標準出力（レポート） |
| `action-versions.json` | 上記の下限表（キット配布。**編集しない**）。本リポジトリ固有のアクションは `action-versions.repo.json`（companion）に書く。後述「リポジトリ固有の Actions を足す場所」 | — |
| `check-ai-workflow-config.js` | Claude 系ワークフローのツール許可設定を検査。`claude_args` の記法誤り（空白分割で無効化）・ブロック内コメント・「SDK を用意して実行ツールを許可していない」不一致・**実装用とレビュー用のスタック別実行ツールのドリフト**（片方にだけ `Bash(node:*)` が無い等）を検出。不備があれば終了コード 1。`--self-test` で検証器自体も試験 | 標準出力（レポート） |
| `check-feedback-dispatched.js` | `feedback/` の環流記録のうち、**計画リポジトリへ未送付のまま滞留しているもの**を検出。警告は 3 条件——①`status: open` で計画リポジトリの issue / PR への参照が無い、②`dispatched: false` で**かつ他に証拠も無い**、③`dispatched:` の値が `true` / `false` のどちらでもない（**YAML 1.1 の `no` / `off` で黙って緑にしない**）。`TEMPLATE.md` / `README.md` は対象外。**伝達済みと見なす証拠は 3 つで、いずれも構造化された記述である**——フロントマターの `planning_issue:` / `dispatched: true`、**ファイル全体のどこかにある計画リポジトリの** issue / PR URL（`source_ref:` も対象。宛先は 置換点 `PLANNING_REPO` で限る。**配布時に必ず書き換えること**——**書き換え忘れると自組織の計画リポの URL が証拠と認められず、恒久的な偽陽性が出る**〔実測: コーパス 58 件で警告 17 → 37 件〕。**空にすると**「自リポ以外なら何でも証拠」へ倒れ、逆に**無関係な第三者リポの URL 1 行で沈黙する**。`owner/repo` の形でない値は警告のうえ旧挙動へ倒す）。**本文の「起票済み」は証拠にしない**（素の部分一致で文意と無関係に当たるため。撤廃した「未送付」と同じアンチパターン）。**鍵が「見えない」形は検出できない**——鍵名の誤記・全角コロン・字下げされた鍵（いずれも鍵が無いのと区別がつかない）。**`planning_issue: #319` は値として読む**（YAML 仕様からの意図的な逸脱。人が issue 番号をこの形で書くため）。**警告のみで終了コード 0**——起票は人手の判断を伴うため、ブロックにすると回避策（記録を作らない）を誘発するからである。`STRICT_FEEDBACK_DISPATCH=1` で失敗として扱える。**自リポジトリの issue / PR URL は伝達の証拠にしない**（計画への伝達と取り違えるため）。**PR を証拠に含めるのは、README が定めるもう一方の経路（記録を計画リポジトリの `draft/feedback/` へコピー）が issue を作らないためである**（planning#319）。**自己申告は本文の語ではなくフロントマターの鍵で読む** —— 以前は本文の「未送付」の素の部分一致で判定しており、**この検査器を論じた記録が語を含むだけで自己発火した**（同上）。`--self-test` で検証器自体も試験（planning#217。記録は作られたが起票されず 6 件が最長 1 か月近く滞留した事故が起点） | 標準出力（レポート） |
| `check-cross-repo-refs.js` | **他リポジトリの issue / PR 番号の修飾**が規約（`.claude/rules/traceability.md`）どおりかを検査。3 つの型を見る——**長い表記の裸書き**／**列挙形の修飾漏れ**（先頭だけ修飾して後続を裸にする形）／**修飾語と番号が空白で離れる形**。**検査対象は表示テキストのみ**（インラインコード・コードフェンスの中は見ない＝ literal な引用は表記規約の対象外。これにより規約自身が反例を書ける）。閉じないコードフェンスは**違反として上げる**（黙ると以降のファイル全体が検査対象外になる）。違反があれば終了コード 1。`--self-test` で検証器自体も試験（69 件）。**配布時に冒頭の 置換点（`CROSS_REPOS` / `SELF_NAMES` / `EXCLUDE_PATHSPECS`）を必ず書き換える**——とくに `SELF_NAMES` の書き忘れは正当な自リポ参照を大量に止め、検査そのものを外させる（実測 22 件）。環境変数 `CROSS_REPO_NAMES` / `CROSS_REPO_SELF_NAMES` / `CROSS_REPO_EXCLUDES` でも上書きできる。**規約に書くだけでは守られないことが実測で確かめられている**（規約の書いてある当のファイルを編集する PR が同じ違反を犯し CI を green で通過した。158 occurrence が蓄積） | 標準出力（レポート） |
| `check-feedback-status-sync.js` | 環流記録の **`status` が計画側の裁定に追随しているか**を突合する。`status` は**計画側の裁定がどこまで進んだかを表す**（伝達したかは `dispatched:` が担う。裁定 planning#323）が、**値の正は計画側にあるのに人手でしか追随しない**。`feedback/README.md` 自身が「この語彙を検査する機械は無い。値の誤りは沈黙する」と明記しており、**その沈黙が 2 回起きた**（実装側 18 件 / 10 件。planning#337）。見るのは**計画側に同名の写しを持つ記録の `status` 一致**の 1 点だけ。**写しを持たない記録は対象外**——記録ファイル経路と Issue 経路は等価であり（planning#319）、**写しの不在は伝達漏れを意味しない**（数えると恒久的な偽陽性になる）。**値そのものの妥当性は見ない**（両側が同じ値なら語彙外でも素通りする）。計画リポジトリを参照できなければ skip（fail-open）だが、**`--require-planning` を付けると fail へ倒せる —— CI では必ず付ける**（付けないと「配線したのに一度も検査していない」状態が緑で固定される。planning#343）。**参照できているのに記録 0 件・突合 0 件でも fail**。**未知の引数は黙って無視せず設定誤りとして落とす**（無視すると、CI が渡し続けているフラグが効いていないことに誰も気付けない）。**突合を fixture で駆動する**ので、**計画リポが未 populate な CI でも実効する**（実データが全件同期している間は比較演算子を取り違えても緑になるため）。`--self-test`（16 件） | 標準出力（レポート） |
| `check-planning-pin-freshness.js` | **計画 pin が古いことをセッション開始時に知らせる**（`setup.sh` から呼ぶ）。計画側で裁定が反映されても pin を進めるまで実装側には伝わらず、**待ち時間の実体は「回答待ち」ではなく「回答に気づいていない時間」だった**（planning#337）。**常に exit 0（fail-open）** —— pin を進める判断は人が行うため、赤にすると pin が古い間ずっと CI が止まる。**「検査していない」と「古くない」を読み分けられるようにする**（参照できないときはその旨を出す）。既定はオフライン完結（pin されたコミットの日付だけを見る）、`--fetch` を付けたときだけ既定ブランチとの差も取る。しきい値は 置換点 `DEFAULT_MAX_AGE_DAYS`（既定 14 日）/ `PLANNING_PIN_MAX_AGE_DAYS`。**0 にはしない**（毎回鳴ると読まれなくなる）。**その差分が着手可否に効くかは見ない** —— それはリポジトリごとの規約であり、要るなら本スクリプトを土台に分類規則を足して固有デルタとして記録する。`--self-test`（7 件） | 標準出力（警告） |
| `check-kit-sync.js` | **キット `repo-template` への追随を機械が見る。** キットは「分類 A はバイト一致を機械判定できる」ことを利点に挙げるが、**どのファイルがどの分類かの表が無ければ判定を回す対象が決まらない**。表を持たずに運用したリポジトリで、**キット由来の文書から節が丸ごと欠落**していた事故と、**pin だけ進めて追随を伴わなかった**事故が実測されている（planning#336）。**pin がずれていなくても乖離は起きる** —— 差が出るのは「追随を機械が見ているか」である。見るのは 3 点（①分類 A がバイト一致 ②**キットの全ファイルが表に載っている**＝増えたファイルを黙って見逃さない ③表に在るのに実在しないファイルが無い）。**見ないもの**は正直に書く —— 分類 B のデルタの妥当性（形式しか見ない）・**キットに入った新しい規範**（バイト一致であって規範の移動ではない）・分類 C の中身。**分類表 `kit-sync-classification.json` は各リポジトリが作る**（キットが配るのは検査器と雛形 `kit-sync-classification.example.json` である）。キットを参照できなければ skip（fail-open）だが、**`--require-planning` を付けると fail へ倒せる —— CI では必ず付ける**（planning#343）。**参照できているのに走査 0 件・分類 A が 0 件でも fail**（0 件走査を緑にしない門）。**未知の引数は設定誤りとして落とす。**探索順は 置換点 `KIT_CANDIDATES`（submodule / 隣接クローンの両方を既定で見る）、`KIT_DIR` で上書き可。`--self-test`（13 件） | 標準出力（レポート） |
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
node scripts/check-feedback-dispatched.js          # 計画へ未送付の環流記録を検査（警告のみ）
node scripts/check-action-versions.js --compare-with-ref origin/develop  # 同期による巻き戻りを検査
node scripts/check-action-versions.js --check-latest  # 新しいメジャーが出ていないか確認
node scripts/check-permission-denials.js <log>     # 実行ログの権限拒否を検査（CI では自動実行）
node scripts/check-cross-repo-refs.js              # 他リポジトリ issue / PR 番号の修飾を検査
node scripts/check-cross-repo-refs.js --self-test   # 検査ロジック自体の自己試験（69 件）
node scripts/check-kit-sync.js --require-planning  # キットへの追随（CI では必ずフラグを付ける）
node scripts/check-feedback-status-sync.js --require-planning  # 環流記録 status の追随
node scripts/check-planning-pin-freshness.js       # 計画 pin の鮮度（常に exit 0）
node scripts/check-banned-settled-cash-sources.js  # 決済済み資金の代替値のコード参照を検査（#425）
node scripts/scripts.test.js                       # 上記スクリプト群の単体テスト
```

> **計画リポジトリを参照する検査器（`check-doc-links.js` / `check-kit-sync.js` /
> `check-feedback-status-sync.js`）は、参照できないとき既定で skip する。CI では必ず
> `--require-planning` を付けること** —— 付け忘れると、取得に失敗したジョブが
> **「配線したのに一度も検査していない」まま緑になる**（planning#343）。
> **ローカルでは付けない**（隣接クローンが無い環境で落ちるだけである）。

### 検査器を書くときの規約（fail-open の閉じ方。裁定 planning#343）

**外部の存在（submodule・隣接クローン・実行ログ）に依存して skip する検査器は、
`--require-planning` に相当する「fail-closed へ倒すフラグ」を必ず持たせる。**
持たせないと、各リポジトリが個別に気付いて CI ジョブ側で塞ぐしかなくなる。

- **未知の引数は黙って無視せず、設定誤りとして落とす。** これが本規約の要である ——
  フラグを持たない版が**フラグを黙って無視した**ために、「CI は渡し続けているのに効いていない」
  状態が生まれた。**`run:` 行に文字列が在ることしか見ていない回帰テストは、配線が効いている
  ことを保証しない。**
- **フラグの効きは実走で固定する**（合成した「参照できない環境」で `exit 1` になること）。
  配線を見るテストでは捕まらない。
- **例外**: fail-open が事故ではなく**決定**である検査器（`check-planning-pin-freshness.js` は
  「pin を進める判断は人が行う」ため常に exit 0）には求めない。**その旨をヘッダへ書くこと。**

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
> `STRICT_AI_WORKFLOW_CONFIG=1` で警告を失敗として扱える（既定はオフ）。
>
> **`check-doc-links.js` の「対象外」表示に注意する。** PR CI は submodule を populate しないため、
> `planning/` 配下などへのリンクは**検査されない**。出力の `（未 populate の submodule 配下 N 件は
> 対象外 …）` はその範囲を示す。実際に ai-stock-trading では PR CI が planning 配下 753 件を毎回
> 飛ばし、その隙間に破損 20 件が蓄積した。PR 段階で検査したい場合は checkout に submodules と
> トークンを付けるか、定期ジョブ（`doc-links-planning`）の結果を確認すること。



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
| `feedback-dispatched` | `check-feedback-dispatched.js`（計画へ未送付の環流記録。**警告のみ**） |
| `feedback-status-sync` | `check-feedback-status-sync.js --require-planning`（環流記録 `status` の追随。submodule を取得する） |
| `kit-sync` | `check-kit-sync.js --require-planning`（キットへの追随。**計画リポジトリを取得するジョブであり、取得できなければ fail させる**） |
| `adr-index-sync` | `check-adr-index-sync.js`（IADR 本文と索引行の同時変更） |
| `plan-id-qualification` | `check-plan-id-qualification.js`（他プロジェクトの計画 ID の `<PROJ>/<ID>` 修飾。`PLAN_ID_PREFIXES` を明示） |
| `test-traceability` | `check-test-traceability.js --require-planning`（必須範囲 FR のテスト・仕様書の存在。本リポ固有） |
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
