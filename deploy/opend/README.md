# moomoo OpenD 常駐（Docker / k8s）— #124 試作

> 起点: IADR-0053（moomoo OpenD Docker 化・Proposed。ai-stock-trading#122 / PR #123 で追加）/
> Issue #124 / #13（moomoo アダプタ）/ ADR-0002

moomoo OpenAPI は **OpenD ゲートウェイの常駐が必須**（既定 `:11111`、SDK 要求を moomoo サーバへ中継）。
本ディレクトリは **コマンドライン版 OpenD**（`moomoo_OpenD_<ver>_Ubuntu18.04` の内側フォルダ・実行ファイル `OpenD`・
設定 `OpenD.xml`）をコンテナで常駐させる一式。実バイナリ構成（10.8.6818）に合わせてある。

## ⚠️ 重要な前提（正直な線引き）

- **バイナリは同梱しない**（EULA/再配布回避・~440MB・`.gitignore` 済）。ビルド時に **tar.gz をビルドコンテキストへ
  一時配置**して取り込む（`scripts/opend-build.sh` が参照ディレクトリから自動コピー→ビルド→import→後片付け）。
- **実起動検証は未実施**（moomoo 口座・Futu 通信が無いため）。ubuntu:20.04 上で Ubuntu18.04 バイナリを動かすため、
  不足共有ライブラリがあり得る（`ldd /opt/opend/OpenD` で確認し `Dockerfile` の apt に追加）。デバイス保存パス
  （PVC マウント先）は起動ログで確認して調整する。
- **SIMULATE 前提・実弾は撃たない**。取引環境はクライアント（#13 アダプタ）が `TrdEnv.SIMULATE` を選ぶ。
  実弾（`TrdEnv.REAL`）は ADR-0002 の PoC 合格まで行わない（IADR-0016）。
- **無人運用の成立性は本 PoC の検証項目**（デバイス認証/2FA・取引パスワードのアンロック自動化）。

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

### 3) 適用（オプトイン。既定では立てない）
```bash
kubectl apply -f deploy/opend/k8s/pvc.yaml
kubectl apply -f deploy/opend/k8s/opend.yaml
kubectl -n ai-stock-trading rollout status deploy/opend
kubectl -n ai-stock-trading logs deploy/opend       # ログイン/デバイス認証の要否を確認
```

### 4) 発注執行（#13）から利用
moomoo アダプタ（#13・未実装）は `IBrokerAdapter` 経由で `opend:11111` へ接続し、`TrdEnv.SIMULATE` で発注する。
アダプタ実装・`Broker:Provider=moomoo` の解禁は #13（ADR-0002 PoC 合格後）で行う。

## PoC で確認する未決事項（→ 結果を IADR-0053/plan-feedback へ）
- デバイス認証/2FA の無人化（PVC 永続化で再起動時の再認証を回避できるか）
- 取引パスワードのアンロック自動化（SIMULATE では不要な範囲の切り分け）
- 海外 IP（Hetzner）からの接続可否・利用規約（ADR-0002 未決）
- OpenD の長期常駐安定性・強制アップデート頻度
