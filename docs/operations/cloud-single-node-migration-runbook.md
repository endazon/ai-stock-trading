---
title: 運用 Runbook — 単一ノードのクラウドへの移行（自宅の PC から Hetzner の k3s へ）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-07
---
<!-- trace:
ids: [FR-04, FR-08, NFR, NFR-03, NFR-05, NFR-06, FR-10]
adrs: [ADR-0006, ADR-0024, ADR-0073, ADR-0076, ADR-0078, ADR-0080]
iadrs: [IADR-0631, IADR-0630, IADR-0629, IADR-0621, IADR-0608, IADR-0053, IADR-0598, IADR-0341, IADR-0623, IADR-0613, IADR-0615, IADR-0627]
specs: [20261107_cloud-openbao-tls-store, 20261107_cloud-db-per-service-users, 20261107_cloud-keycloak-tls, 20261106_1560_cloud-openbao-eso-and-stage2, 20261106_1535_platform-infra-tls, 20261105_1525_cloud-single-node-values-and-runbook]
issues: [#1560, #1535, #1525, #24, planning#868, MSP#2401, MSP#2402, MSP#2471]
-->

# 運用 Runbook: 単一ノードのクラウドへの移行

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。**
>
> 🔴 **本書の手順はすべてオーナーが行う。** サーバーの契約・ノードへのログイン・秘密の投入・OpenD の認証・配備の実行は、
> AI（コーディングエージェント）は行わない。

## 1. 移行の条件（この順番を必ず守る）

**次の 5 段をすべて満たすまで、サーバーに本物の秘密を入れない。** 本物の秘密は次の 3 つである。

- 外から持ち込む資格情報（moomoo のログイン情報・Discord のトークン・情報源の API キー・所有者の TOTP の秘密など）。
- クラスタの中で乱数で作る資格情報（DB・ブローカの利用者・機密クライアントの secret・注文の承認の印の鍵・OpenD の RSA の鍵など）。
- TLS のサーバ証明書の秘密鍵（基盤の側）。

どれも **OpenBao から供給する**（§5）。k8s の Secret を手で作ってから移す形は採らない。

1. 両リポジトリの非公開のセキュリティの指摘が、すべて直りマージされている。指摘ごとに直したことを、PoC の担当が確かめる。
2. このクラウドの構成がマージされている。
   - AST の values-cloud（秘密は OpenBao から ESO で写す・守りは段 2）と本書。
   - 基盤の最小構成（OpenBao・ESO・unseal の手順を含む）と、バックアップと警報。
3. マージされた構成と修正を、書き手と別の新しい文脈のエージェントが監査し、critical と high が 0 件である。指摘の確かめは PoC の担当が行う。
4. サーバーを、秘密を入れない状態で立てる。外から次を確かめる。
   - 開いている口が SSH だけであること。
   - k3s の起動フラグ。
   - データの暗号化。
   - **到達の確認（秘密は要らない）**: ノードから `scripts/measure-region-latency.sh` を**既定の回数（5 回）以上**で流す。`--count` で 5 より少なくしない。
     - 必須の情報源（Finnhub・SEC EDGAR・FRED）は既定のホストで測る。
     - ニュース系は Finnhub（既定）と Google News RSS（`--host news.google.com:443` で足す）の **2 つのうち 1 つ以上**が届けばよい。
     - 日銀（`--host www.stat-search.boj.or.jp:443`）と EDINET（既定）は記録だけにする。
     - OpenD の接続先（moomoo のサーバー）は `--tcp-only <ホスト>:<ポート>` で足す。ホストとポートは、オーナーが moomoo の公式資料で確かめてここへ書く: `<未確認>`。
       公式資料に接続先が無く確かめられないときは、確かめた日付と参照先（資料の URL や文書名）を段 5 の報告に残す（残さなければこの段を満たさない）。OpenD の到達は初日（§8）に確かめる。
     - **「届かない」は、必須の接続先のどれかが 1 回も成功しないこと**（ニュース系は 2 つとも 0 回）。届かなければ、秘密を入れる前に作り直し（シンガポール）を裁定にかける。段 5 へ進まない。
     - 遅延は記録するが、判断の基準に含めない。
5. 1〜4 の結果をオーナーに報告し、オーナーが承認してから、**オーナー自身が**秘密を入れる（§5）。
   - 入れる順: §5.1 の値 → §5.4 の配備 → **§5.3 の TOTP の登録**（登録する前は、取引を再開・緩める 7 操作が 403 になる）→ §5.5 の突き合わせ。
   - 入れた後、**突き合わせ**を行う（§5 の最後）。描画が参照する Secret のうち、OpenBao から写されていないもの（`ast-operator-hold` を除く）が 1 つでもあれば、取引を始めない。

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
  - 値は標準入力でだけ渡す。ファイルに書くときは `umask 077` で作り、使い終わったら消す。
  - 秘密の作り直しは、オーナーが手順のスクリプトを実行して行う。
- **秘密は OpenBao に置き、ESO が k8s の Secret へ写す。** k8s の Secret を手で作らない（例外は下の `ast-operator-hold` だけ）。
  - OpenBao・ESO の本体、unseal、オーナー用の資格、基盤と共有の値の作成は、基盤の手順書（`deploy/cloud/README.md` と OpenBao の初期化・unseal の手順書）で先に済ませる。
  - 取引ユニットの名前空間のストア（`SecretStore openbao`・ServiceAccount `ast-eso-reader`）と ExternalSecret は、`values-cloud.yaml` を当てると chart が作る。
  - 置き場（KV v2 のマウント `cloud/`）: 取引ユニットだけの値は `cloud/ast/<Secret の名前>`、基盤と共有の値は `cloud/shared/<名前>`。property の名前は Secret のキーの名前と同じ。
- 外から持ち込む値は、基盤の `put-external-secret.sh` で入れる（基盤の手順書の確定した呼び方）。
  - 形: `<値> | bash <基盤>/deploy/cloud/vault/put-external-secret.sh --live --operator-file <…>.age --identity <…> [--overwrite] cloud/ast/<名前> <キー>`
  - 値は標準入力の中身を、末尾の改行も含めてそのまま 1 つのキーへ書く。空なら書かない。
  - キーが既に在れば何も変えずに 5 で止まる。上書きは `--overwrite` のときだけ（同じパスのほかのキーは残る）。`cloud/shared` へは書けない。

### 5.1 入れるもの（取引ユニットの分）

| Secret（キー） | OpenBao のパス（property） | 入れ方 |
| --- | --- | --- |
| `ast-db-credentials`（`username`・`password`） | `cloud/shared/ast-db` | 基盤の `seed-secrets.sh` が OpenBao の中で作る（オーナーは何も入れない） |
| `ast-db-<サービス>-credentials`（`username`・`password`。DB を持つ 7 つのサービスごと） | `cloud/shared/ast-db-<サービス>` | 同上。各サービスは自分の Secret だけを読む。上の共有の 1 組は、基盤が共有の利用者を消すまでの移行の間だけ残る（§5.4） |
| `ast-rabbitmq-users`（11 の利用者名） | `cloud/shared/ast-rabbitmq-users` | 同上 |
| `ast-secrets` の 3 つの client secret | `cloud/shared/ast-client-secrets`（キーは clientId） | 同上。client ID は chart が字面で足す |
| CA の写し `platform-infra-ca`（`ca.crt`） | `cloud/shared/platform-infra-ca` | 基盤の `configure.sh` が置く（公開してよい証明書） |
| `ast-secrets` の `order-approval-signing-key` | `cloud/ast/ast-secrets` | 基盤の `seed-secrets.sh … gen cloud/ast/ast-secrets order-approval-signing-key 32`（OpenBao の中で乱数。在れば上書きしない） |
| `ast-message-origin-keys`（`backtest`・`information-collection`・`market-monitor`・`order-execution`・`report`・`risk-management`・`trade-decision`） | `cloud/ast/ast-message-origin-keys` | 基盤の `seed-secrets.sh … gen cloud/ast/ast-message-origin-keys <キー> 32` を 7 つのキーごとに流す（下。OpenBao の中で乱数。在れば上書きしない） |
| `ast-secrets` の外の値 | `cloud/ast/ast-secrets` | オーナーが `put-external-secret.sh cloud/ast/ast-secrets <キー>` で 1 つずつ入れる（下） |
| `moomoo-credentials`（`login-account`・`login-pwd-md5`） | `cloud/ast/moomoo-credentials` | オーナーが入れる。パスワードは平文で置かず、MD5 の小文字 16 進を入れる |
| `moomoo-rsa`（`opend_rsa.pem`） | `cloud/ast/moomoo-rsa` | オーナーが手元で 1024 ビットの鍵を作り、ディスクに書かずに流す（下） |
| `ast-owner-totp`（`secret`） | `cloud/ast/ast-owner-totp` | `scripts/ast-owner-totp-enroll.sh --live --openbao`（§5.3） |

`ast-secrets` の外の値（キー）: `discord-bot-token`・`discord-webhook-url`・`discord-bot-killswitch-phrase`・`finnhub-api-key`・
`marketdata-finnhub-api-key`・`fred-api-key`・`edinet-subscription-key`・`sec-edgar-user-agent`。
Discord の環境の ID（`discord-bot-guild-id`・`discord-bot-channel-id`・`discord-bot-allowed-user-ids`・`discord-bot-user-mapping`）も、
ESO を使う構成では `ast-secrets` から読む（秘密ではないが、`--set-string discord.bot.*` とは併用できない）。

例（値は標準入力から。画面に出さない）:

```bash
# 呼び出しの前置き（秘密は含まない。ファイルのパスだけ）。
PUT=(bash <基盤>/deploy/cloud/vault/put-external-secret.sh --live --operator-file <…>.age --identity <…>)
# 外の値: 打った文字が見えない read で受け、パイプでだけ渡す（履歴・ディスクに残さない。末尾の改行を付けない）。
read -rs v && printf '%s' "$v" | "${PUT[@]}" cloud/ast/ast-secrets discord-bot-token; unset v
# moomoo のログインパスワードは MD5 にしてから入れる（末尾の改行を落とす）。
read -rs v && printf '%s' "$v" | md5sum | cut -d' ' -f1 | tr -d '\n' | "${PUT[@]}" cloud/ast/moomoo-credentials login-pwd-md5; unset v
# OpenD の RSA の鍵（1024 ビット・PKCS#1）。鍵はディスクに書かない。
openssl genrsa -traditional 1024 2>/dev/null | "${PUT[@]}" cloud/ast/moomoo-rsa opend_rsa.pem
```

メッセージの発行元の鍵（7 つ。乱数で足りるので、オーナーが値を入れるのではなく OpenBao の中で作る）:

```bash
# 前置き（秘密は含まない。ファイルのパスだけ）。キーが既に在れば 5 で止まる（作り直さない）。
SEED=(bash <基盤>/deploy/cloud/vault/seed-secrets.sh --live --operator-file <…>.age --identity <…>)
for k in backtest information-collection market-monitor order-execution report risk-management trade-decision; do
  "${SEED[@]}" gen cloud/ast/ast-message-origin-keys "$k" 32
done
```

- 7 つが揃うまで配備しない（段 2 では、鍵を読むサービスの Pod が起動しない）。

- RSA の鍵は 1024 ビットにする。手元の PoC がこの長さで動いており、2048 ビットで OpenD が動いた記録は無い（基盤の `gen-rsa` は 2048 未満を作れない）。
- DB のパスワードと利用者名は接続文字列（`Username=…;Password=…;`）へ埋め込まれる。基盤の生成器は英数字と `_` だけを作るので、`;`・`=`・引用符・空白は入らない。

### 5.2 OpenBao を経ないもの

- `ast-operator-hold`（`enabled`・`reason`）: **秘密ではない。SSH から入れる停止の切り替え**である（[Discord を経ない停止の Runbook](out-of-band-trading-stop-runbook.md)）。
  - OpenBao が封印のあいだや ESO が止まったときにも効く必要があるので、従来どおり `kubectl` で作る・消す。
- 取引ユニットの名前空間の CA の ConfigMap は要らない。CA は ESO が Secret `platform-infra-ca` に写し、chart がそれを mount する。

### 5.3 所有者の TOTP

1. 基盤の `put-external-secret.sh` を呼ぶ前置き（スクリプトのパスと `--live --operator-file <…>.age --identity <…>`。秘密は含まない）を `AST_PUT_EXTERNAL_SECRET` に入れる。パスと引数に空白を含めない。
2. `bash scripts/ast-owner-totp-enroll.sh --live --openbao` を流す。シークレットは標準入力でだけ渡り、QR だけが端末に出る（Enter で消す）。
   - 既に登録されていれば 5 で止まる（何も変えない）。作り直すときだけ `--rotate` を付ける（基盤の口へ `--overwrite` を渡す。旧い登録は使えなくなる）。
3. スクリプトが ESO に同期を求め、写し終えてから risk-management と report を作り直させる。同期を確かめられなければ止まるので、
   `kubectl -n ai-stock-trading get externalsecret ast-owner-totp` で `SecretSynced` を確かめてから、2 つの Pod を作り直す。

### 5.4 配備

- 配備: `helm upgrade --install ast deploy/helm/ai-stock-trading -f deploy/helm/ai-stock-trading/values-cloud.yaml -f <§4 のファイル>`
- 守りは段 2 で始まる（注文の承認の印・メッセージの発行元の印・所有者のトークンの aud / azp・ボットの許可表・危険な操作の第 2 要素・kill switch の前提条件）。
  - 注文の承認の鍵が無いと、risk-management と order-execution の Pod は起動しない。
  - メッセージの発行元の鍵（`ast-message-origin-keys`）が無いと、鍵を読む 8 サービスの Pod は起動しない。
    受け手が拒んだメッセージは処理されず、ログ（EventId 16260）とメトリクス `ast_messaging_origin_failures_total{outcome="rejected"}`・警報 `AstMessageOriginRejected` に残る。
  - TOTP が無いと、取引を再開・緩める 7 操作は拒まれる。
- 値を替えたら（作り直し・更新）、ESO が写した後に読む側を作り直す（`kubectl -n ai-stock-trading rollout restart deploy/<名前>`）。クラウドに Reloader は無い。
- DB の資格はサービスごとに分かれている（audit・configuration・cost-control・market-monitor・order-execution・report・risk-management が、それぞれ
  `ast-db-<サービス>-credentials` を読む）。稼働中の構成を共有の 1 組から移すときは、基盤の手順書の 3 段の順を守る:
  基盤の段（利用者を作り OWNER を移す）→ この chart を当てる → 基盤の段（共有の利用者を消す）。基盤の段の前にこの chart を当てると、
  7 つの Secret が同期されず、新しい Pod は起動しない（旧の Pod が残る）。共有の利用者を消した後に、共有の 1 組を読む版へ chart を戻さない。
- 秘密のストア（`SecretStore openbao`）は、基盤の OpenBao の TLS の口（`https://openbao.openbao.svc:8443`）へ、同じ名前空間の ConfigMap `openbao-ca` の CA で検証して話す。
  - 🔴 **稼働中の構成を平文の口から移すときは、基盤の手順の後にこの chart を当てる**: 基盤の受け口の証明書（`tls-cert.sh issue`）→ 基盤の併存の段
    （平文と TLS の両方を受ける）→ 基盤のストア → **この chart** → 基盤が平文を閉じる。先に当てると、ストアが平文の受け口へ https で話して届かない
    （写した Secret は残るが、更新が止まる）。手順は基盤の「秘匿管理の受け口の TLS」の手順書（`docs/operations/cloud-openbao-tls-runbook.md`）の段 0〜段 4。
  - 名前空間を新しく作るとき（この chart を初めて入れるとき）は、入れた後に基盤の `bash deploy/cloud/vault/tls-cert.sh --live publish-ca --namespace ai-stock-trading`
    で ConfigMap を置く。置くまでストアは Ready にならない（ESO が繰り返し試す）。
  - 確かめ: `kubectl -n ai-stock-trading get secretstore openbao` が Ready。ExternalSecret の 1 つを force-sync して SecretSynced。
- 基盤が CA を作り直したら、`cloud/shared/platform-infra-ca` の更新を ESO が写した後に、取引ユニットのサービスを作り直す。
  切り替えの確かめ方と戻し方は [基盤の TLS 切り替え Runbook](platform-infra-tls-cutover-runbook.md)。
- 認可サーバー（Keycloak）へは `https://keycloak:8443` で繋ぎ、同じ基盤の CA で検証する（`values-cloud.yaml` の `global.authTls`）。
  稼働中の構成を平文から移す段（移す間だけ旧の発行元を足し、後で外す）と、Keycloak を呼ぶサービスを足すときの基盤への依頼は
  [認可サーバーの TLS Runbook](cloud-keycloak-tls-runbook.md)。

### 5.5 突き合わせ（秘密を入れた後・取引を始める前）

1. `kubectl -n ai-stock-trading get externalsecret` で、すべてが `SecretSynced` であること。
2. 描画が参照する Secret（`helm template … -f values-cloud.yaml -f <§4 のファイル>` の `secretKeyRef` と `secretName`）の一覧と、
   ExternalSecret の同期先の一覧を照らす。参照されているのに同期先に無いもの（`ast-operator-hold` を除く）が 1 つでもあれば、取引を始めない。
   - CI（helm.yml の「Assert values-cloud secrets from OpenBao and stage 2」）が同じ突き合わせを描画で行っている。ここではノードの上の実物で確かめる。
3. 名前空間に、ESO が所有していない Secret が無いこと（`kubectl -n ai-stock-trading get secret -o jsonpath='{range .items[*]}{.metadata.name}{" "}{.metadata.ownerReferences[0].kind}{"\n"}{end}'`。
   `ast-operator-hold` と ServiceAccount・Helm の記録の Secret は除く）。

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
- [ ] 情報源への到達は §1 の段 4 で確かめ済み（秘密を入れる前）。ここでは API キーを要する取得が成功することを、各サービスのログで確かめる。
- [ ] 遅延: `scripts/measure-region-latency.sh --count 10 --tcp-only <OpenD の接続先のホスト>:<ポート>` の中央値を記録する。
- [ ] digest の参照で全 Pod が起動している（§4 の 5）。
- [ ] 基盤への接続が TLS で通っている: DB・ブローカの接続の失敗と、LLM ゲートウェイの証明書の検証の失敗がログに無い。
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
