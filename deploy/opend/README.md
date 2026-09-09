# moomoo OpenD 常駐（Docker / k8s）— #124 試作

> 起点: IADR-0053（moomoo OpenD Docker 化・Proposed。ai-stock-trading#122 / PR #123 で追加）/
> Issue #124 / #13（moomoo アダプタ）/ ADR-0002

moomoo OpenAPI は **OpenD ゲートウェイの常駐が必須**（既定 `:11111`、SDK 要求を moomoo サーバへ中継）。
本ディレクトリは **コマンドライン版 OpenD**（`moomoo_OpenD_<ver>_Ubuntu18.04` の内側フォルダ・実行ファイル `OpenD`・
設定 `OpenD.xml`）をコンテナで常駐させる一式。実バイナリ構成（10.8.6818）に合わせてある。

## ⚠️ 重要な前提（正直な線引き）

- **バイナリは同梱しない**（EULA/再配布回避・~440MB・`.gitignore` 済）。ビルド時に **tar.gz をビルドコンテキストへ
  一時配置**して取り込む（`scripts/opend-build.sh` が参照ディレクトリから自動コピー→ビルド→import→後片付け）。
- **SIMULATE 前提・実弾は撃たない**。取引環境はクライアント（#13 アダプタ）が `TrdEnv.SIMULATE` を選ぶ。
  実弾（`TrdEnv.REAL`）は ADR-0002 の PoC 合格まで行わない（IADR-0016）。

## PoC 結果（2026-07-15・初回検証）

実バイナリ（`moomoo_OpenD_10.8.6818`）で以下を確認した:

- ✅ **ビルド成功**（ベース `mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy`。docker.io 認証を避けるため mcr を使用）。
- ✅ **共有ライブラリ充足**（ダミー資格情報で起動しても `error while loading shared libraries` は出ず、OpenD が起動）。
- 🔑 **OpenD はログイン時に検証（画像 CAPTCHA / SMS）を対話コンソール（`>>>`）で要求する**。
  → **k8s Deployment（TTY/stdin なし）では初回認証を完了できない**。これが「無人運用の成立性」の要点であった
  （ADR-0002。**2026-08-07 に ADR-0024 決定1 で決着＝条件付き成立。初回のみ有人**）。
- ✅ **実口座で認証→ログイン成功を確認**（画像 CAPTCHA `input_pic_verify_code` ＋ SMS `input_phone_verify_code`。
  権限: HK/US 株等を取得）。**コンテナ内 OpenD が moomoo に認証できることは実証済み**。
- ✅ **規制アンケート**（`https://api.moomoo.com/v2/...`・口座で一度きり）完了後は、ログイン成功後も OpenD が
  **常駐継続**することを確認。
- ⚠️ **初回 PoC の観測（後日修正）**: 初回検証では、デバイス状態を home（`/root/.com.moomoo.OpenD`）＋install
  （`/opt/opend/AppData.dat` 等）に永続化しても新 Pod で再び画像/SMS 検証を要求され、「完全無人は不可」と観測した。
  → **この結論は下記「追検証（2026-07-15）」で修正された**。安定 egress IP（単一ノード）＋デバイス信頼の永続化が
  揃えば、Pod 再作成をまたいで**無人再ログインが成立**する（初回 PoC は Pod IP と egress IP を混同していた可能性）。
- ➡️ **採用: 常駐モデル**。OpenD を**長時間常駐**させる。**初回のみ対話でデバイス認証**し、以降は
  安定 egress IP 下で無人再起動が成立する（IP 変動時は再度 `kubectl attach` で検証）。→ ADR-0002「無人運用の成立性」は
  **条件付き成立（初回有人・安定 egress IP なら以降無人）** として plan-feedback（下記 追検証・修正版）。

**帰結（無人運用の設計）**: **2 段階**とする。
1. **初回のみ対話でデバイス認証**（実口座＋実 SMS を 1 回入力）→ デバイス情報を **PVC に永続化**。
2. 以降は Deployment が**永続化済みデバイス認証を再利用して無人起動**する（安定 egress IP 前提・追検証で実証）。

> ℹ️ **永続化パスは確定済み**: OpenD はデバイス情報を `/root/.com.moomoo.OpenD` に書く。`opend.yaml` は同パスを
> PVC `opend-persist` にマウントしている（実口座認証で確認済み）。install 側（`AppData.dat`）の追加永続化は不要
> （home のみで無人再ログインが成立・IADR-0053 / 追検証）。

## 手順

### 1) イメージをビルド（tar.gz は参照ディレクトリから自動取り込み）
```bash
# 既定: リポジトリ隣接の ../references/moomoo_OpenD_*.tar.gz を使う。別の場所なら引数か OPEND_TARBALL_PATH で指定。
scripts/opend-build.sh
#   もしくは: scripts/opend-build.sh /path/to/moomoo_OpenD_10.8.6818_Ubuntu18.04.tar.gz
# 手動でビルドする場合は tar.gz を deploy/opend/ に置いてから:
#   docker build -t k3d-local/ai-stock-trading/opend:latest \
#     --build-arg OPEND_TARBALL="moomoo_OpenD_10.8.6818_Ubuntu18.04.tar.gz" deploy/opend
```

### 2) 資格情報 Secret（実値はコミットしない）
```bash
PWD_MD5=$(printf '%s' "<ログインパスワード>" | md5sum | cut -d' ' -f1)
kubectl create secret generic moomoo-credentials -n ai-stock-trading \
  --from-literal=login-account="<moomoo account>" \
  --from-literal=login-pwd-md5="$PWD_MD5"
```

### 2b) RSA 暗号鍵 Secret（#13・cross-network trade 必須）
moomoo は**別 Pod 間（worker→opend）の trade 接続に RSA 暗号化を要求する**（#13 で確定・実 OpenD で検証）。
OpenD とクライアント（#13 発注執行）で**同一の PKCS#1 秘密鍵**を用いる。非暗号は OpenD が 127.0.0.1
listen（同一 Pod/loopback）のときのみ。
```bash
openssl genrsa -out opend_rsa_pkcs8.pem 1024
openssl rsa -in opend_rsa_pkcs8.pem -traditional -out opend_rsa.pem   # PKCS#1（BEGIN RSA PRIVATE KEY）
kubectl create secret generic moomoo-rsa -n ai-stock-trading --from-file=opend_rsa.pem=opend_rsa.pem
```
- OpenD 側: `opend.yaml` が `/opt/opend/rsa/opend_rsa.pem` にマウントし `OPEND_RSA_KEY_FILE` で参照
  （entrypoint が `<rsa_private_key>` を OpenD.xml に追加。起動ログに `API RSA Enabled: Yes`）。
- クライアント側: `Broker:Moomoo:OpenD:RsaPrivateKeyPath` に同一鍵パスを設定（AST chart で Secret をマウント）。

（任意）**スモークテスト**（口座不要・共有ライブラリの充足だけ確認）:
```bash
nerdctl --namespace k8s.io run --rm \
  -e OPEND_LOGIN_ACCOUNT=dummy -e OPEND_LOGIN_PWD_MD5=00000000000000000000000000000000 \
  k3d-local/ai-stock-trading/opend:latest
# `error while loading shared libraries: libXXX` → その lib を Dockerfile の apt に追加。
# SMS/`>>>` まで進めば lib 充足。Ctrl+C で終了。
```

### 3) （簡易）対話検証テスト（bootstrap-pod）
検証フローだけ素早く試す用の使い捨て Pod。実運用の常駐は手順4。資格情報は手順2の Secret を参照する:
> 注: 本 Pod は**永続化しない使い捨て**なので「認証フロー確認」用途。デバイス信頼を持ち越して無人再ログインを
> 成立させる常駐は手順 4（PVC 付き）。
```bash
kubectl apply -f deploy/opend/k8s/pvc.yaml
kubectl apply -f deploy/opend/k8s/bootstrap-pod.yaml
kubectl -n ai-stock-trading attach -it opend-bootstrap
```
起動時に OpenD が**検証コードを要求する（SMS か画像 CAPTCHA。moomoo が選ぶ）**。`>>>` に **OpenD コマンド**で入力
（kubectl は打たない）:

- **SMS の場合**（`Command Tips: input_phone_verify_code ...`）: 携帯に届いた 6 桁を
  `>>> input_phone_verify_code -code=<6桁>`
- **画像 CAPTCHA の場合**（`Command Tips: input_pic_verify_code ...`・`PicVerifyCode.png` に保存）:
  **別ターミナル**で画像を取り出して見る → 4 文字を入力
  ```bash
  kubectl -n ai-stock-trading cp opend-bootstrap:/root/.com.moomoo.OpenD/F3CNN/PicVerifyCode.png ./PicVerifyCode.png
  # PicVerifyCode.png を開いて 4 文字を読む
  ```
  ```
  >>> input_pic_verify_code -code=<4文字>
  ```
> コードは数分で失効。失効/やり直しは `>>> req_phone_verify_code` で新しいコードを出す（画像は再度 cp して読む）。
> **［2026-09-09 訂正 / #722］従前ここは `relogin` と書いていたが誤りである。** `relogin` は
> `-login_pwd=` を取る**再ログイン**のコマンドで、検証コードの再送ではない。引数なしで打つと
> 意図しない挙動になり得る。再送は `req_phone_verify_code`（引数なし）である。

成功すればログイン完了・API が `:11111` で待受。**別ターミナル**で保存されたデバイス状態を確認:
```bash
kubectl -n ai-stock-trading exec opend-bootstrap -- ls -lat /root/.com.moomoo.OpenD
kubectl -n ai-stock-trading delete pod opend-bootstrap   # 確認後に片付け（>>> quit でも OpenD/Pod が終了する）
```
> `>>>` は OpenD のコンソール。`help` でコマンド一覧。`quit`/`exit` で終了。`attach` で `>>>` が見えなければ Enter を一度。

### 4) 常駐 Deployment（★実運用・常駐モデル）— 起動時に 1 回検証、以降は再起動しない
`opend.yaml` の container は stdin/tty 付き。適用後に `attach` で 1 回だけ検証する（入力は手順3と同じ）:
```bash
kubectl apply -f deploy/opend/k8s/pvc.yaml
kubectl apply -f deploy/opend/k8s/opend.yaml           # Deployment + Service（opend:11111）
kubectl -n ai-stock-trading attach -it deploy/opend    # `>>>` に input_pic_verify_code / input_phone_verify_code
kubectl -n ai-stock-trading logs deploy/opend | grep -i "Login successful"
```

#### 画面（ブラウザ）から検証コードを入れる（#722）

**手元に kubeconfig が無くても検証できる。** `entrypoint.sh` は OpenD の標準入力を
**FIFO（`/run/opend/stdin`）経由**にしてあるので、`kubectl exec` からも同じ標準入力へ届く。
`attach` が要るのは tty を掴むときだけで、コードを 1 行入れるだけなら exec で足りる。

> **［2026-09-09 追記 / #722 段 2］入力面はこの先 ai-stock-trading の画面になる。**
> 利用者裁定により、検証コードは **ai-stock-trading の SPA 画面**から入れられるようにする
> （Headlamp は入力面にしない）。そのための受け口（サイドカー `opend-auth`）は本 PR で入ったが、
> **画面と BFF のルートは裁定待ち（planning#594）で未着手**である。
> **画面ができるまでの現行手段は、以下の Headlamp / `kubectl exec` のままである。**
> サイドカーの詳細は後段「サイドカー経由で入れる（#722 段 2）」を参照。

**画面ができるまで**は、**既に配備済みの Headlamp**（`https://headlamp.localhost`。Keycloak の OIDC で入る）が
そのまま入力面になる。

1. Headlamp で `ai-stock-trading` / Pod `opend` を開き、**Logs** で `Command Tips:` を読む
   （`input_phone_verify_code` か `input_pic_verify_code` のどちらを求められているか）。
2. 同じ Pod の **Terminal** を開いて、次の 1 行を打つ。

```bash
printf 'input_phone_verify_code -code=123456\n' > /run/opend/stdin
```

3. 画像 CAPTCHA を求められたときは、Terminal で画像を base64 にして読む。

```bash
base64 -w0 "$HOME/.com.moomoo.OpenD/F3CNN/PicVerifyCode.png"
```

出力を `data:image/png;base64,<貼り付け>` としてブラウザのアドレス欄へ入れると画像が見える。
読み取った 4 文字を `printf 'input_pic_verify_code -code=ab12\n' > /run/opend/stdin` で入れる。

4. 再送は `printf 'req_phone_verify_code\n' > /run/opend/stdin`。

同じことを CLI からやるなら次のとおり。

```bash
kubectl -n ai-stock-trading exec deploy/opend -- \
  sh -c "printf 'input_phone_verify_code -code=123456\n' > /run/opend/stdin"
```

> **認可は apiserver の RBAC がそのまま効く。** 書けるのは `pods/exec` を持つ主体だけである。
> OpenD のコンソールには `show_delay_report -detail_report_path=<path>`（**root 権限で任意パスへ書ける**）や
> `relogin -login_pwd=` のような、検証コード以外の強いコマンドがある。
> 標準入力への書き込みは実口座に対する強い権限であり、**この経路（exec）を誰に許すかは RBAC で決める**。
>
> **［2026-09-09 追記 / #722 段 2］** 段 1 ではここに「だから専用の HTTP 面は作らない」と書いていた。
> サイドカー（`opend-auth`）は**その HTTP 面を作らずに済ませる代わりに、通せる行を 3 つに固定した**
> ——面が任意のコマンドを通さないなら、面そのものの権限は「検証コードを入れること」以上に広がらない。
> 経緯と判断は [IADR-0320](../../.ai-context/adr/IADR-0320_opend-auth-sidecar-command-allowlist.md)。
>
> **注意**: FIFO に書いた行は次のプロンプトで消費される。失効したコードを入れたまま再送すると、
> 古い行が再送後の 1 回を食う。**入れる前に Logs で現在のプロンプトを確かめること。**
> ⚠️ 初回の `attach` 検証以降は、**デバイス信頼の永続化（PVC）＋ egress IP の安定**が保てれば
> **無人再ログインが成立**する（追検証で実証。IADR-0053 の初回結論「再起動＝毎回再検証」は撤回済み）。
> ただし **egress IP が変わる再起動**（ノード跨ぎの再スケジュール・クラウド/別リージョン）や moomoo 側の
> セッション失効では**再び有人検証が要る**見込み（実測は #132 で未了）。**再起動は最小化**し、
> 本番ではノードを固定する（chart の `opend.nodeSelector`）。再検証が要る状態になったら再度 `attach` する。

#### サイドカー経由で入れる（#722 段 2・**画面の受け口**）

**このサイドカーは既定で立たない**（`opend.authGateway.enabled=false`）。SPA の画面と BFF のルートが
入るまで面を開けないためである（裁定待ち: planning#594）。

`opend` Pod へ `opend-auth`（`backend/Services/OpendAuthGateway/`）を同居させ、
共有 `emptyDir`（`/run/opend`）越しに OpenD の標準入力 FIFO へ書く。
呼び出すのは**クラスタ内の BFF だけ**であり、Ingress も TLS も持たない（ClusterIP のみ）。

| 口 | 役割 |
| --- | --- |
| `GET /opend-auth/state` | 整形済みのコンソール末尾・待たれているプロンプト（`phone` / `pic` / `resend`）・CAPTCHA の有無 |
| `GET /opend-auth/captcha` | 画像 CAPTCHA を `image/png` で返す（**固定パス・引数なし**。不在なら 404） |
| `POST /opend-auth/verify` | `{"kind":"phone"｜"pic"｜"resend","code":"..."}` |

🔴 **クライアントはコマンド文字列を渡さない。** 書かれ得る行は次の 3 つだけで、サーバが組み立てる。

```
input_phone_verify_code -code=<4〜8 桁の ASCII 数字>
input_pic_verify_code   -code=<4 文字の ASCII 英数>
req_phone_verify_code
```

有効化の手順（**イメージを先に用意する**）:

```bash
# 1) サイドカーのイメージを作って import（未 import で有効化すると ImagePullBackOff → Pod ごと NotReady
#    ＝ Service opend の endpoint から外れて発注経路まで止まる）
bash scripts/k8s-local-images.sh          # opend-auth-gateway を含む

# 2) chart 経由
helm upgrade --install ast deploy/helm/ai-stock-trading \
  --set opend.enabled=true --set opend.authGateway.enabled=true

# 3) 生 manifest（dev 経路）は opend.yaml にサイドカーが入っている
kubectl apply -f deploy/opend/k8s/opend.yaml
```

クラスタ内からの動作確認（BFF が入るまでの暫定手段）:

```bash
kubectl -n ai-stock-trading run curl --rm -it --image=curlimages/curl --restart=Never -- \
  curl -s http://opend:8080/opend-auth/state
```

**OpenD 本体が担う複写**（サイドカーは PVC を見ない）:

- コンソールの複製は `script -q -e -f -a` で `/run/opend/console.log` へ落とす
  （`OPEND_CONSOLE_MAX_BYTES` 既定 1MiB を超えたら切り詰める）。
  **`kubectl logs` と `attach` は従来どおり動く**（`tee` を使うと C stdio が全バッファへ落ちて沈黙する）。
- 画像 CAPTCHA は `$HOME/.com.moomoo.OpenD/F3CNN/PicVerifyCode.png` を `/run/opend/captcha.png` へ複写する
  （`$HOME` は chart の `opend.home` から。非 root 化すると `/home/opend`）。

> 🔴 **サイドカーは `opend-persist`（PVC）を絶対にマウントしない。** PVC にはデバイス信頼の実体
> （`Device.dat`）と `OpenD.xml`（ログイン資格情報の MD5）が同居する。
> 判断の全体は [IADR-0320](../../.ai-context/adr/IADR-0320_opend-auth-sidecar-command-allowlist.md)。

### 5) 発注執行（#13）から利用
moomoo アダプタ（#13・未実装）は `IBrokerAdapter` 経由で稼働中の `opend:11111` へ接続し、`TrdEnv.SIMULATE` で発注する。
アダプタ実装・`Broker:Provider=moomoo` の解禁は #13（ADR-0002 PoC 合格後）で行う。

## 追検証（2026-07-15・#13 結合時）

- ✅ **暗号化で in-cluster trade 成立**: RSA（PKCS#1）を OpenD＋クライアント双方に設定し、実 OpenD の SIMULATE
  口座で **発注→状態追跡→取消**の一巡を確認（#13・PR #130）。非暗号では OpenD が
  `cross-network trade connections must be encrypted` を返す。
- ➕ **自動再ログインの緩和例（要注意・限定条件）**: 本セッションで Deployment を再作成した際、
  永続化済みデバイス状態（PVC `/root/.com.moomoo.OpenD`）で**対話検証なしに再ログインが成立**した。
  単一ノード（Rancher Desktop）で egress IP が安定なためと考えられる。IADR-0053 の「再起動＝毎回再検証」は
  **撤回された**（2026-08-07・ADR-0024 決定2。**誤りは Pod IP と egress IP の混同**）。
  **egress IP 変更時に再検証が要るかは未検証である**（ADR-0024 決定5-1）。**安全側に有人検証を想定する**
  （マルチノード/クラウドでは従来どおり）が、これは**実測ではなく安全側の仮定**である。

## PoC 結論（→ ADR-0002 へ plan-feedback）
- ✅ ビルド・共有ライブラリ充足・OpenD 起動・**実口座ログイン成功**（2026-07-15）。
- ✅ 規制アンケート完了後は OpenD が常駐継続。
- 🟡 **無人再起動は条件付き成立（初回 PoC 結論を追検証で修正）**: 初回検証では新 Pod で再検証を要求されたが、
  追検証（下記「追検証」節）で、**デバイス信頼の永続化＋安定 egress IP（単一ノード）が揃えば Pod 再作成をまたいで
  無人再ログインが成立**することを実証（3 リビジョンで対話検証なし）。egress IP 変動時（マルチノード/クラウド）は有人検証が要る。
- ➡️ **常駐モデルを採用**: 初回のみ有人で対話検証（`attach`）、以降は安定 egress IP 下で無人再起動が成立。#13 は稼働中 `opend:11111` へ。
- 残（未検証）: egress IP 変更時の再検証発生の切り分け、海外 IP（Hetzner）接続・ToS、長期常駐安定性・強制アップデート、取引 PW アンロック（SIMULATE 範囲）。

## 本番化（#132 / IADR-0060）

本ディレクトリは **dev 経路**（生 manifest）である。**本番配備は chart の `opend.enabled=true`** を使う
（[chart README](../helm/ai-stock-trading/README.md)）。切替の前提条件・手順は
**[運用仕様書の本番切替チェックリスト](../../docs/operations/operations.md#opend-の本番切替チェックリスト132)**
に集約してある（egress-IP 実測・Vault 化・非 root の実動作確認などは**未充足**）。

> **PVC `opend-persist` は `resource-policy: keep` で保護されている**（#626 /
> [IADR-0283](../../.ai-context/adr/IADR-0283_deploy-value-preservation-and-kb-realm-fix.md)。
> `deploy/opend/k8s/pvc.yaml` と chart 版 `templates/opend.yaml` の双方）。`opend.enabled` の
> 切り替えや `helm uninstall` では PVC が消えない＝デバイス信頼状態（本ページの「デバイス信頼の
> 永続化」）は保たれる。デバイス信頼をリセットしたいときだけ手動で
> `kubectl delete pvc opend-persist -n ai-stock-trading` を実行する。

## 既知のリスク・制約（dev 割り切り）

- **root 実行**: コンテナは既定 root で動く（OpenD は `$HOME/.com.moomoo.OpenD` を使うため）。
  #132 でイメージに **uid/gid 10001 と `/home/opend` を用意済み**で、chart の `opend.home=/home/opend` ＋
  `opend.securityContext` で非 root へ切り替えられる（`USER` は切り替えていない＝既定は現行維持）。
  **実 OpenD では未検証**（HOME 変更でデバイス信頼を失う恐れ）。恒久の秘匿は Vault/External Secrets
  （chart の `externalSecrets.enabled`＝**受け口のみ**。ストアは #24 で未整備）。
- **資格情報の露出面**: `entrypoint.sh` は env の資格情報から `OpenD.xml` を生成する（コマンドライン引数には載せない
  ＝`ps` 露出は回避）。#132 で `umask 077` ＋ `chmod 600` を掛けた（RSA 鍵は Secret の `defaultMode: 0400`）が、
  **`OpenD.xml` はコンテナ内に平文（MD5）で存在する**ことに変わりはない。
- **livenessProbe を意図的に付けない**: ~~OpenD は**再起動＝対話再検証**（常駐モデル）。liveness による自動再起動は
  再検証待ちで停止する状態を招くため付けない~~ **【⚠️ 理由を差し替え 2026-08-07・ADR-0024】**
  旧理由（再起動＝対話再検証）は **ADR-0024 決定2 で否定された**。**付けない結論は変えないが、根拠は次の 2 点である。**
  (a) **再起動の最小化は維持する**（決定3。本 ADR は「再起動しても復旧できる」ことを認めるものであって
  「再起動してよい」と言うものではない）。(b) **SPOF であること自体は変わらない**（決定4。単一インスタンスであり、
  復旧までの発注不可時間が生じる）——liveness による自動再起動は、その不可時間を**無人で繰り返し発生させ得る**。
  ハング検知は監視＋有人対応とする。readiness(TCP) も「検証前から listen」する点に注意（probe 通過≠ログイン完了）。

## 実験（否定結果・参考）
`k8s/experiment-appdata.yaml` は install 側（AppData.dat）永続化でも再検証が要ることを確認した検証用（採用しない）。
**この否定結果自体が 2026-08-07 に撤回された**（ADR-0024 決定2。Pod IP と egress IP の混同）。経緯の記録として残す。
不要になったら `kubectl -n ai-stock-trading delete pod opend-appdata; kubectl -n ai-stock-trading delete pvc opend-state`。
