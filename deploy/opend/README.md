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
- 🔑 **OpenD はログイン時に SMS/デバイス認証を要求し、対話コンソール（`>>>`）で待機する**。
  → **k8s Deployment（TTY/stdin なし）では初回認証を完了できない**。これが「無人運用の成立性」の要点（ADR-0002 未決）。

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

### 3) 初回デバイス認証（対話ブートストラップ・実口座で 1 回）
Deployment は TTY を持たず初回 SMS 認証を完了できない。**PVC をマウントした対話 Pod**（`bootstrap-pod.yaml`）で
1 回だけ認証する。資格情報は手順2の Secret を参照する（コマンドに書かない）:
```bash
kubectl apply -f deploy/opend/k8s/pvc.yaml
kubectl apply -f deploy/opend/k8s/bootstrap-pod.yaml
kubectl -n ai-stock-trading attach -it opend-bootstrap
```
OpenD は起動時に自動で SMS を要求する（携帯に届く）。**`>>>` プロンプトに OpenD のコマンドを入力する**（kubectl は打たない）:
```
>>> input_phone_verify_code -code=<携帯に届いた6桁コード>
```
成功すればログイン完了・API が `:11111` で待受。**別ターミナル**で永続化パス（デバイス情報の保存先）を確認する:
```bash
kubectl -n ai-stock-trading exec opend-bootstrap -- ls -lat /opt/opend       # AppData.dat 等の更新を確認
kubectl -n ai-stock-trading exec opend-bootstrap -- ls -lat /opt/opend/persist
kubectl -n ai-stock-trading delete pod opend-bootstrap   # 確認後に片付け（quit すると OpenD/Pod が終了する）
```
> `>>>` は OpenD のコンソール。`help` でコマンド一覧。SMS コードは `input_phone_verify_code -code=...`。`quit` で終了。
> `attach` で `>>>` が見えない場合は Enter を一度押す。

### 4) 無人 Deployment（デバイス認証後・オプトイン）
```bash
kubectl apply -f deploy/opend/k8s/opend.yaml       # Secret（手順2）＋PVC（手順3）を参照
kubectl -n ai-stock-trading rollout status deploy/opend
kubectl -n ai-stock-trading logs deploy/opend      # 永続化認証で無人ログインできるか確認
```

### 5) 発注執行（#13）から利用
moomoo アダプタ（#13・未実装）は `IBrokerAdapter` 経由で `opend:11111` へ接続し、`TrdEnv.SIMULATE` で発注する。
アダプタ実装・`Broker:Provider=moomoo` の解禁は #13（ADR-0002 PoC 合格後）で行う。

## PoC で確認する未決事項（→ 結果を IADR-0053/plan-feedback へ）
- ✅ ビルド・共有ライブラリ充足・OpenD 起動（2026-07-15 確認済）
- 🔑 デバイス認証は**初回対話（SMS）必須**を確認。残: **初回認証後、PVC 永続化で再起動時の再認証を回避できるか**
  （永続化パス＝OpenD のデバイス保存ファイルの場所を実認証後に確定）
- 取引パスワードのアンロック自動化（SIMULATE では不要な範囲の切り分け）
- 海外 IP（Hetzner）からの接続可否・利用規約（ADR-0002 未決）
- OpenD の長期常駐安定性・強制アップデート頻度
