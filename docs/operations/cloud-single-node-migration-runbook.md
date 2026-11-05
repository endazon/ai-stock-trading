---
title: 運用 Runbook — 単一ノードのクラウドへの移行（自宅の PC から Hetzner の k3s へ）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
---
<!-- trace:
ids: [FR-04, FR-08, NFR, NFR-03]
adrs: [ADR-0006, ADR-0024, ADR-0073, ADR-0076]
iadrs: [IADR-0608, IADR-0053, IADR-0598, IADR-0341]
specs: [20261105_1525_cloud-single-node-values-and-runbook]
issues: [#1525, #24, planning#868, MSP#2401, MSP#2402]
-->

# 運用 Runbook: 単一ノードのクラウドへの移行

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。**
>
> 🔴 **本書の手順はすべてオーナーが行う。** サーバーの契約・ノードへのログイン・秘密の投入・OpenD の認証・配備の実行は、
> AI（コーディングエージェント）は行わない。

## 1. 移行の条件（この順番を必ず守る）

**次の 5 段をすべて満たすまで、サーバーに本物の秘密（moomoo のログイン情報・LLM の API キー・Discord のトークンなど）を入れない。**

1. 両リポジトリの非公開のセキュリティの指摘が、すべて直りマージされている。指摘ごとに直したことを、PoC の担当が確かめる。
2. このクラウドの構成（AST の values-cloud と本書・基盤の最小構成・バックアップと警報）がマージされている。
3. マージされた構成と修正を、書き手と別の新しい文脈のエージェントが監査し、critical と high が 0 件である。指摘の確かめは PoC の担当が行う。
4. サーバーを、秘密を入れない状態で立てる。外から次を確かめる。
   - 開いている口が SSH だけであること。
   - k3s の起動フラグ。
   - データの暗号化。
5. 1〜4 の結果をオーナーに報告し、オーナーが承認してから、**オーナー自身が**秘密を入れる。

## 2. 前提と、使わないもの

- ノード: Hetzner の欧州の 8 GB 帯（例: CX33）の単一ノード k3s。
  - 初日の確認（§8）で接続と遅延が足りなければ、シンガポールで作り直す。
  - 再認証の多さも作り直しの基準に入れる（§9）。
- 基盤（MSP）の最小構成（Postgres・RabbitMQ・Keycloak・LlmGateway）は、基盤の手順書で先に立てる。
  - 名前空間とホスト名は経路 B と同じとしている（`platform-infra` の postgres・rabbitmq・keycloak・otel-collector、`microservices-platform` の LlmGateway）。
  - 基盤の手順書で名前が変わったら、`values-cloud.yaml` を合わせてから配備する。
- **持ち込まないもの**:
  - ニュースの RAG（基盤の knowledge ユニット）。
  - Web の画面。
  - Stage 0 の記録。
  - 取引判断のプロンプトには「この環境では検索を使っていない」の 1 行が出る（判断を止めない）。
- 🔴 **ArgoCD は使わない。** `deploy/argocd/application.yaml` は `targetRevision: main` を指しており、`main` はまだ空のブランチである。
  - 最初の稼働では、helm をノードで**手で**実行する（§5）。
  - Application をクラスタへ適用しない。自動同期が空の構成で上書きするため。
  - `values-cloud.yaml` を ArgoCD の valueFiles に入れない（CI が検査する）。
- 操作は Discord だけで行う。OpenD の検証コードの画面（認証サイドカー）は使わない。

## 3. OpenD のイメージを作る（ノードの上で）

1. 配布物（`moomoo_OpenD_<版>_Ubuntu18.04.tar.gz`）を、moomoo の公式の配布元から取得する。
2. **ビルドの前に sha256 を記録する。** `sha256sum moomoo_OpenD_*.tar.gz` の出力を、版・取得日と一緒に運用の記録へ残す。
   - 次に作り直すときは、同じ版なら同じ値になることを確かめてから使う。
3. ノードで `scripts/opend-build.sh <tar.gz のパス>` を実行してイメージを作る（手順の詳細は [deploy/opend の README](../../deploy/opend/README.md) の 1)）。
4. 作ったイメージをノードの containerd へ取り込む（`ctr -n k8s.io images import` ほか）。
   - 取り込んだ後の digest を §4 のファイルへ書く。
5. 🔴 **取引パスワードはクラスタに置かない。**
   - Secret `moomoo-credentials` が持つのは、ログインの資格（アカウントとログインパスワードの MD5）だけである。
   - SIMULATE の発注に取引の解錠は要らない。実弾の解錠は別の判断であり、本書の範囲外である。

## 4. AST のイメージを取り込み、digest で固定する

1. ノードで 11 サービスのイメージをビルドし、containerd へ取り込む（イメージ名は `k3d-local/ai-stock-trading/<サービス>-service:latest`）。
2. 取り込んだイメージの digest を一覧する（例: `crictl images --digests`）。
3. 次の形のファイルを**ノードの上だけに**作る（コミットしない・ノードの外へ出さない）。
   - 形は `deploy/helm/ai-stock-trading/ci/values-cloud-digests.fixture.yaml` と同じにする。
   - そのファイルの値は検査専用で、実在しない。

   ```yaml
   services:
     audit:
       digest: sha256:<64 桁>
     # … 11 サービスすべて
   opend:
     image:
       digest: sha256:<64 桁>
   ```
4. `values-cloud.yaml` は digest が欠けると描画で止まる。止まったら、欠けたサービスを書き足す。
5. 初日に、digest の参照（`…:latest@sha256:…`）で kubelet がイメージを解決できることを確かめる。
   - Pod が `ErrImageNeverPull` にならないこと。

## 5. 秘密と配備

- 🔴 **秘密の値を画面に表示しない・コマンドの引数に書かない・リポジトリに置かない。**
  - 値は標準入力かファイル（`umask 077` で作り、使い終わったら消す）で渡す。
  - 秘密の作り直しは、オーナーが手順のスクリプトを実行して行う。
- 供給の方式は `externalSecrets.enabled` で切り替える。
  - 既定は `false` で、Secret を手で作る。
  - 基盤の最小構成が ESO を使うと決めたら、`true` にする（基盤の手順書に合わせる）。
- 手で作る Secret（名前空間 `ai-stock-trading`）:
  - `ast-db-credentials`（キー `password`）: 基盤の最小構成が作った AST 用の DB のパスワードを、**値を表示せずに**写す。
    - 例: 基盤の Secret を `kubectl get secret … -o jsonpath=…` で読み、`base64 -d` してそのまま `kubectl create secret generic ast-db-credentials --from-file=password=/dev/stdin` へ流す。
    - リポジトリの既定のパスワードは使わない（`values-cloud.yaml` は既定値を描かない）。
    - 🔴 パスワードは接続文字列（`Password=…;`）へ埋め込まれる。`;`・`=`・引用符・空白を含まない値（16 進か英数字）に限る。基盤の最小構成の生成器は乱数の 16 進を作るので、この条件を満たす。手で作り直すときも 16 進か英数字にする。
  - `ast-rabbitmq-users`・`ast-secrets`・`moomoo-credentials`・`moomoo-rsa`: 経路 B と同じキー名で作る（[Vault 秘匿値 Runbook](vault-secrets-runbook.md) のキー一覧）。
- 配備: `helm upgrade --install ast deploy/helm/ai-stock-trading -f deploy/helm/ai-stock-trading/values-cloud.yaml -f <§4 のファイル>`
  - Discord の環境固有 ID は `--set-string discord.bot.*` で与える。これらは秘密ではない。

## 6. 切り替えの順番（場の時間外に行う）

🔴 **同じ SIMULATE の口座に二重にログインしない。** 自宅の OpenD とクラウドの OpenD を同時に動かさない。

1. **場の時間外**に始める（米国の通常取引の時間を避ける）。
2. 自宅の PoC で、新規発注の一時停止（pause）を入れる。
3. **自宅の OpenD を止める**（`kubectl -n ai-stock-trading scale deploy/opend --replicas=0`）。止まったことを確かめる。
4. クラウドで配備し（§5）、OpenD の初回の SMS 認証を行う（§7）。
5. クラウドの取引ユニットが立ち上がったことを確かめ、一時停止を解く。
6. 自宅の取引ユニットは止めたままにする（戻すとき以外に動かさない）。

**週末を挟む場合の期限:**

- **日曜（米国東部）の暦日のうちに、クラウドで口座照会が 1 回以上成功していること。**
  - 成功していないと、月曜の新規建てが一日中拒否される。
  - 規則は [基準資金の供給が無いときの Runbook](capital-baseline-seed-runbook.md) にある。
- 遅くとも日曜の米国東部の夕方までにクラウドを稼働させ、巡回を 1 周以上回してから終える。

## 7. OpenD の初回の SMS 認証（利用者が行う）

1. `kubectl -n ai-stock-trading attach -it deploy/opend -c opend` で OpenD のコンソール（`>>>`）につなぐ。
2. SMS で届いた 6 桁を、`input_phone_verify_code -code=<6桁>` で入れる。画像の検証が出たら `input_pic_verify_code` で入れる。
   - 端末はエコー無しなので、打った文字は見えない。
3. 抜けるときに Ctrl+C を押さない（OpenD が落ちて SMS からやり直しになる）。ウィンドウごと閉じる。
4. 詳細は [deploy/opend の README](../../deploy/opend/README.md) の 4)。デバイスの信頼は PVC に残り、egress の IP が変わらなければ再認証は要らない。

## 8. 初日の確認

結果は #24 に記録する（リージョンの実測）。

- [ ] moomoo への接続: OpenD がログインし、発注執行の口座照会が成功する。
- [ ] 情報源への到達: Finnhub・SEC EDGAR・FRED・日銀（BOJ）へ、ノードから HTTPS で届く。
  - 例: `scripts/measure-region-latency.sh`。日銀は `--host www.stat-search.boj.or.jp:443` を足す。
- [ ] 遅延: `scripts/measure-region-latency.sh --count 10` の中央値を記録する。OpenD の接続先は `--host <ホスト>:<ポート> --tcp-only` で足す。
- [ ] digest の参照で全 Pod が起動している（§4 の 5）。
- [ ] 取引判断のログ（判断の記録）の `ragContext` が `not-configured` で、判断が失敗していない。

## 9. 初週の確認

- [ ] OpenD の再認証（SMS・画像）を求められた**回数と時刻**を記録する（Discord の通知と OpenD のログ）。
  - 欧州からのログインで、不正アクセスの検知（再認証）が増えるおそれがある（推論）。
- [ ] **場中に再認証が繰り返されたら、シンガポールで作り直す。** 無人の運用が成り立たないため。
  - 繰り返されたかの判断は、記録した回数と時刻でオーナーが行う。
- [ ] 月曜の寄り付きで新規建てが拒否されていない（日曜の口座照会が成功している）。
- [ ] Pod のセキュリティの段を上げる: 段 2（`podSecurity.stage: 2`・PSA の enforce は baseline）で 1 日以上、起動・判断・発注・日報が通ったら、段 3（非 root）へ上げる。
  - 上げるときは `values-cloud.yaml` の `podSecurity.stage: 3` と `podSecurity.admission.enforce: restricted` を一緒に替える。
  - OpenD は実機で確かめてから `opend.podSecurity.stage: 3` にする（chart の README「必須の前提」）。
  - 戻すときは段を下げて helm を再実行する。

## 10. 戻し方（クラウドから自宅へ）

🔴 **戻すときも二重ログインをしない。クラウドの OpenD を先に止める。**

1. 場の時間外に行う。クラウドで新規発注の一時停止（pause）を入れる。
2. **クラウドの OpenD を止める**（`kubectl -n ai-stock-trading scale deploy/opend --replicas=0`）。止まったことを確かめる。
3. 自宅の PoC の OpenD を起こし、必要なら再認証する。
4. 自宅の取引ユニットを起こし、一時停止を解く。
5. クラウドの取引ユニットは止めたままにする。サーバーを消すときは、秘密を消してから消す。
