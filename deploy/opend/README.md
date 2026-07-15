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
  → **k8s Deployment（TTY/stdin なし）では初回認証を完了できない**。これが「無人運用の成立性」の要点（ADR-0002 未決）。
- ✅ **実口座で認証→ログイン成功を確認**（画像 CAPTCHA `input_pic_verify_code` ＋ SMS `input_phone_verify_code`。
  権限: HK/US 株等を取得）。**コンテナ内 OpenD が moomoo に認証できることは実証済み**。
- ✅ **規制アンケート**（`https://api.moomoo.com/v2/...`・口座で一度きり）完了後は、ログイン成功後も OpenD が
  **常駐継続**することを確認。
- 🔴 **結論: 完全無人（自動再起動）は不可**。デバイス状態を **home（`/root/.com.moomoo.OpenD`）＋install
  （`/opt/opend/AppData.dat` 等）の両方**を PVC 永続化しても、**新 Pod（＝新 IP）は再び画像/SMS 検証を要求**した。
  検証は **IP/セッション依存**で、永続化では回避できない。
- ➡️ **採用: 常駐モデル**。OpenD を**長時間常駐**させ、**起動/再起動のたびに 1 回だけ対話で検証**する
  （`kubectl attach` → `input_pic_verify_code` / `input_phone_verify_code`）。**再起動を極力避ける**（安定ノード・
  rolling 不使用）。→ ADR-0002「無人運用の成立性」は **限定的成立（起動時有人・以降常駐）** として plan-feedback。

**帰結（無人運用の設計）**: **2 段階**とする。
1. **初回のみ対話でデバイス認証**（実口座＋実 SMS を 1 回入力）→ デバイス情報を **PVC に永続化**。
2. 以降は Deployment が**永続化済みデバイス認証を再利用して無人起動**する。

> ⚠️ **永続化パスは実口座での本認証後に確定する**。OpenD がデバイス情報を書くファイル（`AppData.dat` 等）の場所を
> 確認し、PVC マウント先（現状 `/opt/opend/persist`）をそこへ合わせる。→ 結果を IADR-0053 / plan-feedback（ADR-0002）へ。

## 手順

### 1) イメージをビルド（tar.gz は参照ディレクトリから自動取り込み）
```bash
# 既定: /c/10_SourceCode/references/moomoo_OpenD_*.tar.gz を使う。別の場所なら引数か OPEND_TARBALL_PATH で指定。
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
> 注: OpenD はデバイス情報を `/root/.com.moomoo.OpenD` に書くが、**永続化しても再起動時の再検証は回避できない**
> （PoC 確認済・IADR-0053）。よって本 Pod は「認証フロー確認」用途。
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
> コードは数分で失効。失効/やり直しは `>>> relogin` で新コードを出す（画像は再度 cp して読む）。

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
> ⚠️ **再起動（ノード再起動・eviction・rolling）のたびに再検証が必要**（デバイス永続化では回避不可・IADR-0053）。
> **再起動を避けて常駐**させる。再起動したら再度 `attach` で検証する。

### 5) 発注執行（#13）から利用
moomoo アダプタ（#13・未実装）は `IBrokerAdapter` 経由で稼働中の `opend:11111` へ接続し、`TrdEnv.SIMULATE` で発注する。
アダプタ実装・`Broker:Provider=moomoo` の解禁は #13（ADR-0002 PoC 合格後）で行う。

## PoC 結論（→ ADR-0002 へ plan-feedback）
- ✅ ビルド・共有ライブラリ充足・OpenD 起動・**実口座ログイン成功**（2026-07-15）。
- ✅ 規制アンケート完了後は OpenD が常駐継続。
- 🔴 **完全無人（自動再起動）は不可**: home＋install（AppData.dat）を永続化しても新 Pod（新 IP）は再検証を要求。
  検証は IP/セッション依存で回避不可。
- ➡️ **常駐モデルを採用**: 起動時のみ有人で対話検証（`attach`）、以降は再起動を避けて常駐。#13 は稼働中 `opend:11111` へ。
- 残（未検証）: 海外 IP（Hetzner）からの接続可否・ToS、長期常駐安定性・強制アップデート、取引 PW アンロック（SIMULATE 範囲）。

## 既知のリスク・制約（dev 割り切り）

- **root 実行**: コンテナは root で動く（OpenD は `/root/.com.moomoo.OpenD` を HOME 前提に使うため）。取引口座資格情報を
  扱うプロセスとして本番では非 root 化＋`securityContext`（`runAsNonRoot` 等）が望ましい（要 HOME/永続化パスの再調整）。
  恒久は Vault/External Secrets（暫定は k8s Secret）。
- **資格情報の露出面**: `entrypoint.sh` は env の資格情報から `OpenD.xml` を生成する（コマンドライン引数には載せない
  ＝`ps` 露出は回避）。ただし `OpenD.xml` はコンテナ内に平文で存在する。dev 割り切り。
- **livenessProbe を意図的に付けない**: OpenD は**再起動＝対話再検証**（常駐モデル）。liveness による自動再起動は
  再検証待ちで停止する状態を招くため付けない（ハング検知は監視＋有人対応とする）。readiness(TCP) も
  「検証前から listen」する点に注意（probe 通過≠ログイン完了）。

## 実験（否定結果・参考）
`k8s/experiment-appdata.yaml` は install 側（AppData.dat）永続化でも再検証が要ることを確認した検証用（採用しない）。
不要になったら `kubectl -n ai-stock-trading delete pod opend-appdata; kubectl -n ai-stock-trading delete pvc opend-state`。
