---
title: k8s-local-deploy で変わったサービスだけを作り直せるようにする（SERVICES / --changed-since。#1094）
type: spec
status: accepted
related_ids: [NFR, IADR-0052, IADR-0295, IADR-0322, IADR-0457]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs: []
---

# k8s-local-deploy で変わったサービスだけを作り直す（#1094）

## 背景

#1094（PoC セッションの報告）: `scripts/k8s-local-deploy.sh` は手順 1 で `scripts/k8s-local-images.sh` を呼び、MAPPING の 12 本
（Worker 11 本と opend-auth-gateway）を選択の口なしに逐次ビルドする。実測で最大 30 分かかる（2026-09-29 01:24→01:54）。
場の前の配備の締め切り（21:30 JST）に間に合わず、場中（22:30〜05:00 JST）の配備も避けたいので、配備を短くしたい。

起点は非機能（配備の運用性）であり、計画の FR/UC に当たる番号は無い（NFR）。計画 ADR の新たな制約は無い。

## 前提の確認（origin/develop 78c4be56）

- タグは `:latest` 固定で、helm にタグを渡さず、`imagePullPolicy: IfNotPresent` である。**変わっていないサービスは前回の `:latest` のまま動き続けられる**（helm 側の変更は要らない）。
- `backend/Dockerfile` は `COPY global.json Directory.Build.props Directory.Packages.props ./` と `COPY backend/ ./backend/` だけをビルド入力にし、
  `dotnet restore/build/publish "${SERVICE_PROJECT}"` は **その csproj の ProjectReference の閉包だけ**を読む。ルートの他のファイル（`.editorconfig` 等）は COPY されない。
- `.dockerignore` は `**/*.md`・`**/bin/`・`**/obj/`・`**/Internal/Generated/` をコンテキストから外す。
- `ast_rollout_restart_workers`（IADR-0295）は OpenD 以外の全 Deployment を毎回 restart する。
- サービス間の ProjectReference は `TradeDecisionService.csproj → RiskManagementService.csproj` の 1 本だけ（`grep -rn ProjectReference backend/Services --include=*.csproj`）。
  他の参照先は `backend/Shared/**` と `backend/TestSupport/**`（`AiStockTrading.TestSupport.PlatformShim`）である。
- **`backend/Bff/**`・`backend/Tests/**`・`backend/backend.slnx` はどの Worker イメージの入力でもない**ことを確かめた:
  Services・Shared・TestSupport（テストプロジェクトを除く）の csproj の ProjectReference はどれもこの 2 ディレクトリへ解決されない（`grep -rn 'Bff\|backend/Tests\|\\Tests\\' --include=*.csproj`）。
  `.slnx` は `dotnet restore "${SERVICE_PROJECT}"` が読まない。
  ただし将来の参照追加で黙って入力になり得るため、**選択器は起動時に csproj を読んでこの事実を確かめ直し、参照があればその領域を全件扱いへ倒す**（ハードコードの無視にしない）。
- 各サービスの csproj は `Compile/Content/None Remove="Tests/**"` を持つ（`backend/Services/<Dir>/Tests/**` は本体の入力ではない）。

## 設計

### 1. `scripts/select-changed-services.js`（新規）

変更ファイル一覧から作り直すサービス名（MAPPING の名前）を出す。**迷ったら全件（`ALL`）へ倒す**（作り直し過ぎは遅いだけで気付けるが、作り直し漏れは古いイメージが緑のまま動き続ける）。

| 規則 | パス | 扱い |
| --- | --- | --- |
| 全件 | `global.json` / `Directory.Build.props` / `Directory.Packages.props` / `nuget.config` / `.dockerignore` / `backend/Dockerfile` | `ALL` |
| 全件 | `backend/Shared/**` / `backend/TestSupport/**` | `ALL` |
| 全件 | `backend/` の下で下記のどれにも当たらないパス（MAPPING に無い `backend/Services/<Dir>/`、`backend/Services/` 直下のファイルを含む） | `ALL` |
| 全件 | 変更 0 件・git の失敗 | `ALL` |
| サービス | `backend/Services/<Dir>/**`（`<Dir>` に csproj を持つ MAPPING の項目） | その項目 |
| 無視 | `backend/Services/<Dir>/Tests/**` | 何もしない |
| 無視 | `backend/Bff/**` / `backend/Tests/**` / `backend/backend.slnx`（Worker の参照が無いことを起動時に確かめた場合だけ。有れば `ALL`） | 何もしない |
| 無視 | `backend/**/*.md`（`.dockerignore` の `**/*.md` でコンテキストに入らない） | 何もしない |
| 無視 | `backend/` の外（docs / deploy / scripts / frontend / .ai-context / .github 等）。deploy/ の変更は helm upgrade が反映する | 何もしない |
| 推移閉包 | csproj の ProjectReference を読んだ逆グラフで、変わったサービスに依存するサービスを足す（例: risk-management → trade-decision） | 追加 |

- **MAPPING の単一情報源は `scripts/k8s-local-images.sh`**（配列を正規表現で読む。0 件なら例外＝黙って空にしない）。
- ProjectReference は csproj から XML コメントを剥がしてから `<ProjectReference Include="...">` を読む（OpendAuthGateway.csproj はコメント中に語 `ProjectReference` を持つ）。
- CLI: `--since <ref>` は `git diff --name-only --no-renames <ref>`（作業ツリーと ref の差＝コミット済み・未コミットの両方。改名は旧名と新名の両方）と
  `git ls-files --others --exclude-standard`（未追跡）を合わせる。出力は 1 行 1 名、または `ALL`。変更に当たるサービスが 0 件なら何も出さない。
  `-` で始まる ref は拒む（exit 2。git へのオプション注入を防ぐ）。`--files <path>` は一覧ファイルを読む。`--self-test` と `module.exports`。

### 2. `scripts/k8s-local-images.sh`

- `SERVICES=a,b`（MAPPING の名前。空白は許す）。**未知の名前はランタイム判定より前に exit 2**（何も作らない）。未設定・空＝全件（後方互換）。
  `SERVICES=none`＝選択なし（下の「無ければ作る」だけが働く）。`k8s-local-deploy.sh --changed-since` が変更 0 件のときに使う。
- **選ばれなくても、ランタイムに `:latest` が無いイメージは作る**（初回・作り直したクラスタへの安全策）。
  - rancher: `nerdctl --namespace k8s.io image inspect <ref>`（ビルド先がそのまま k3s の containerd なので、これが Pod から見える実体）。
  - k3d: **ビルドの要否は `docker image inspect`、import の要否はノードの `crictl inspecti`**（`docker exec k3d-<cluster>-server-0 crictl inspecti <ref>`）で分けて判定する。
    docker に有ってもクラスタを作り直すとノードからは消えるため、docker だけを見ると「作らない・import もしない」で Pod が `ErrImagePull` になる。
    逆にノードだけを見ると、docker に残っているイメージまで作り直して遅くなる。ノードの確認が失敗したとき（ノード名の違い等）は **import する側へ倒す**
    （import は数十秒で、作り直しより桁で安い）。
- `K8S_BUILT_FILE` が設定されていれば、ランタイムへ新たに供給した（作った・import した）名前を 1 行 1 名で書く（開始時に空にする）。
- `AST_IMAGES_LIB=1` で source すると関数定義だけを読む（`AST_DEPLOY_LIB` と同じ idiom）。

### 3. `scripts/k8s-local-deploy.sh`

- `--changed-since <ref>`（`--changed-since=<ref>` も可。値が無ければ exit 2）を足し、`SERVICES` も受ける。両方の指定は exit 2（どちらを信じるか曖昧）。
  他のフラグの解釈は変えない（未知の `-*` は exit 2、それ以外はクラスタ名）。
- `resolve_ast_image_selection`: `--changed-since` なら選択器を呼び、`ALL`（または node が無い・選択器の失敗）→ 絞り込みなし（`SERVICES` を unset）、
  0 件 → `SERVICES=none`、それ以外 → `SERVICES=a,b`。選択があれば `AST_IMAGE_SELECTION=1`。
- 手順 1 で `K8S_BUILT_FILE` を一時ファイルへ向けて images を呼ぶ。手順 5 は `AST_IMAGE_SELECTION=1` のときだけ
  `ast_rollout_restart_workers <built-file>` とし、**作り直した名前と一致する Deployment だけ**を restart する（OpenD は従来どおり除外）。
  opend-auth-gateway を作り直したときは、OpenD を再起動しない旨と手動の反映手順を stderr に出す。選択が無ければ従来どおり全件 restart。

## 採らなかった案

- **イメージタグを git の SHA にして helm へ渡す**: 変わらないサービスも毎回 Pod テンプレートが変わる（または SHA ごとの対応表が要る）。
  helm 側の変更と values-local の追随が要り、`imagePullPolicy` の前提も変わる。#1094 の範囲（作り直しを減らす）を超える。
- **イメージの内容ハッシュ（ビルド入力の digest）で要否を決める**: 入力の列挙が Dockerfile の写しになり、黙ってずれる。git の差分は「人が何を変えたか」を直接表す。
- **`backend/Bff/**` 等を固定で無視する**: 参照が足されたときに黙って作り直し漏れになる。起動時に csproj から確かめ直す形にした。
- **k3d でも docker の有無だけを見る**: クラスタを作り直した直後に import 漏れで Pod が起動しない（上記）。
- **選択があっても全件 restart する**: 変わらないサービスまで Pod を入れ替え、場の前の配備時間と、稼働中の判断サイクルの中断を増やす。
  `:latest` の IfNotPresent なので、作り直していないサービスは restart しても同じイメージのままである（restart は効果が無い）。
- **Dockerfile で csproj を先に COPY して restore のレイヤを分ける**: 個々のビルドを短くするが別の変更である（#1094 の補足。別 issue の候補）。

## 母集合（規則 9・10）

`grep -rn 'k8s-local-images\|k8s-local-deploy\|ast_rollout_restart_workers' --exclude-dir=.git .` を走査した（`.ai-context/` の凍結記録と `CHANGELOG.md` は point-in-time の記録なので対象外）。live な文書・設定のうち、呼び出し・再起動・配備コマンドを記述する箇所と扱い:

| 箇所 | 記述 | 扱い |
| --- | --- | --- |
| `scripts/k8s-local-deploy.sh` 冒頭の使い方・手順 1・手順 5 | images の呼び出し・全件 restart | **変更**（オプション・選択の中継・restart の絞り込み） |
| `scripts/k8s-local-images.sh` 冒頭 | 使い方 | **変更**（`SERVICES` / `K8S_BUILT_FILE` / 無ければ作る） |
| `scripts/k8s-local-deploy.test.sh` | restart の固定（T-673-05/06） | **追加**（絞り込み・中継・既定不変）。既存は無改修で緑 |
| `scripts/README.md` の表 2 行と `shell-scripts` の列挙 | images / deploy の説明 | **変更**・新規 2 行（選択器・images のテスト） |
| `deploy/helm/ai-stock-trading/README.md` §デプロイ・§rollout restart の注記 | 配備コマンド・全件 restart | **変更**（使い方と注意: 共有の変更は全件・初回は全件・OpenD は再起動しない） |
| `docs/operations/operations.md` の「手順（dev）」行 | 「images → deploy」の 2 段 | **変更**（deploy が images を呼ぶこと・絞り込みの口）。 trace ブロックは既存のまま（表示テキストに ID を書かない） |
| `.github/workflows/ci.yml` の `static-checks`（シェルテスト）・常時の自己試験 | テストの配線 | **追加**（`bash scripts/k8s-local-images.test.sh`・`node scripts/select-changed-services.js --self-test`） |
| `.github/workflows/claude-coding.yml` / `claude-code-review.yml` の `--allowedTools`・レビューの案内 | CI が実走するシェルテストの許可（`check-ai-workflow-config.js` の `shellTestDrift` が ci.yml と突合する） | **追加**（`Bash(bash scripts/k8s-local-images.test.sh:*)`。案内の「3 本」を 4 本へ） |
| `.claude/settings.json` の許可 | ローカルの Claude の許可 | **変えない**（利用者の権限設定であり、エージェントが変えない。検査器の突合対象でもない）。PR で利用者に追加を提案する |
| `deploy/opend/README.md:269` / `deploy/opend/k8s/opend.yaml:85` / `values.yaml:180` / `values-local.yaml:44` | 「opend-auth-gateway のイメージを先に用意する」 | 変えない（引数なしの `k8s-local-images.sh` は従来どおり全件を作る＝記述は正しいまま） |
| `docs/tech/tech-requirements.md` / `docs/integration/*` / `docs/migration/*` / `docs/operations/{vault-secrets,wolverine-queue-cleanup}-runbook.md` / `docs/blocked-tasks.md` / `infra/README.md` | build args の同一性・secret の挙動・手動デプロイである事実 | 変えない（本変更で誤りにならない） |
| `.github/workflows/helm.yml` / `scripts/check-ai-workflow-config.js` の fixture | AST_ESO・DISCORD 等の文脈、自己試験の入力 | 変えない |

**この変更で新たに誤りになる自分の記述（規則 10）**: `k8s-local-deploy.sh` 冒頭の「末尾で OpenD を除く Deployment へ rollout restart を打つ」と、
chart README の「OpenD を除く全 Deployment へ」は、絞り込み時には誤りになるため「絞り込みが無ければ」の条件を付けた。
`scripts/README.md` の deploy 行の「OpenD を除く Deployment への rollout restart」も同様に条件を付けた。レビューの案内の「同じ 3 本だけ」は 4 本へ直した。

## 受け入れ基準（→ テスト）

`node scripts/select-changed-services.js --self-test`:

1. 全件扱いの各パス（ルートの props・global.json・nuget.config・.dockerignore・backend/Dockerfile・Shared・TestSupport・分類できない backend/ 下・MAPPING に無いサービス）→ `ALL`。
2. 変更 0 件・空白行だけ・git の失敗（`null`）→ `ALL`。
3. `backend/Services/<Dir>/**` → その名前。`Tests/**`・`*.md`・Bff・backend/Tests・slnx・backend/ の外 → 何も選ばない。
4. 推移閉包: risk-management の変更で trade-decision も選ぶ。trade-decision の変更は risk-management を選ばない。多段の閉包。
5. Bff 等へ Worker の参照があれば、その領域の変更は `ALL`。
6. MAPPING を実ファイルから 12 件読み、csproj の ProjectReference を実ファイルから読む（TradeDecision → RiskManagement が見える）。コメント中の語に反応しない。
7. `-` 始まりの ref を拒み、git の無い場所では `null`（→ `ALL`）へ倒れる。

`bash scripts/k8s-local-images.test.sh`（nerdctl / docker / k3d スタブ）:

1. 未知の `SERVICES` → exit 2・何も作らない（ランタイムが無い環境でも 2）。
2. 未設定 → 12 本すべて作る（`K8S_BUILT_FILE` も 12 行）。
3. 選択 → 選んだものだけ作る。空白入りの指定も受ける。`none` → 何も作らない。
4. 選ばれていなくても、ランタイムに無いイメージは作る。
5. k3d: docker に無ければ作って import、docker に有ってノードに無ければ作らずに import、ノードの確認が失敗すれば import する。
6. `AST_IMAGES_LIB=1` の source は何も作らない。

`bash scripts/k8s-local-deploy.test.sh`（追加分）:

1. 引数なしの `ast_rollout_restart_workers` は従来どおり OpenD 以外を全件 restart する（T-673-05 を無改修で維持）。
2. 作り直した一覧を渡すと、その Deployment だけを restart する（OpenD と opend-auth-gateway は Deployment として restart しない。案内を出す）。空の一覧なら 0 件。
3. `resolve_ast_image_selection`: `SERVICES` の中継、`--changed-since` の結果（名前・0 件→`none`・`ALL`→絞り込みなし・選択器の失敗→絞り込みなし）、両方の指定は exit 2。
4. 引数の解釈: `--changed-since` の値欠落は exit 2、未知のオプションは従来どおり exit 2。

## 検証

- `node scripts/select-changed-services.js --self-test`: 44 件 OK（模擬の文脈で規則・閉包、実ファイルで MAPPING 12 件・csproj の参照・Bff 等の非入力を確認）。
  実走: `--since HEAD~3` → `information-collection-service` / `order-execution-service`（2 件）、`--since HEAD~30` → `ALL`（`backend/Shared/**` の変更）。
- `bash scripts/k8s-local-images.test.sh`: 40 passed, 0 failed。
- `bash scripts/k8s-local-deploy.test.sh`: 161 passed, 0 failed（既存 128＋追加 33）。**変更前のテストファイル（`git show HEAD:`）を新しいスクリプトへ当てても 128 passed**（既定の挙動が変わっていない）。
- 変異の確認: images の「ランタイムに無ければ作る」を殺すと 7 件、deploy の restart の絞り込みを殺すと 4 件が落ちる。
- `node scripts/scripts.test.js`: 487 passed（`scripts.repo.test.js` を含む）。
- 文書・設定の検査（`git add` 後）: `check-trace-blocks` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-workflow-job-refs` /
  `gen-knowledge-graph --check` / `check-reading-budget` / `check-ai-workflow-config`（シェルテストの許可の突合を含む）/ `check-adr-index-sync` / `check-adr-index-addendum-loss` / `check-test-traceability` がすべて exit 0。
- 実クラスタ・実ランタイム（kubectl / helm / nerdctl / docker / k3d）では走らせていない（作業の制約。スタブのみ）。

## 残余

- 実クラスタでの計測（何分に縮むか）は未実施（本作業は実クラスタへ触れない制約で行った）。利用者の次回配備で確かめる。
- `COPY backend/` が全サービスのレイヤキャッシュを無効にする問題（個々のビルドの時間）は別 issue の候補（#1094 の補足）。
- ルートの `.editorconfig` 等、Dockerfile が COPY しないファイルは無視する。Dockerfile の COPY を増やしたら選択器の全件扱いの表も追随が要る（写しの関係。Dockerfile の変更自体は `ALL` になるので、その回は取りこぼさない）。
