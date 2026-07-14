# moomoo OpenD 常駐（Docker / k8s）— #124 試作

> 起点: IADR-0053（moomoo OpenD Docker 化・Proposed。ai-stock-trading#122 / PR #123 で追加）/
> Issue #124 / #13（moomoo アダプタ）/ ADR-0002

moomoo OpenAPI は **OpenD ゲートウェイの常駐が必須**（既定 `:11111`、SDK 要求を moomoo サーバへ中継）。
本ディレクトリは OpenD をコンテナで常駐させる**ダウンロード方式**の試作一式。

## ⚠️ 重要な前提（正直な線引き）

- **バイナリは同梱しない**（EULA/再配布回避）。ビルド時に**あなたの Futu アカウント**のダウンロードセンターで
  取得した Linux 版 OpenD の tar.gz URL を `--build-arg OPEND_URL` で渡す。
- **実起動検証は未実施**（moomoo 口座・Futu への通信が無いため）。版により実行ファイル名/フラグ/依存ライブラリ/
  デバイス保存パスが異なる可能性があり、`Dockerfile`・`entrypoint.sh`・PVC マウント先は**あなたの環境で要調整**。
- **SIMULATE 前提・実弾は撃たない**。取引環境はクライアント（#13 アダプタ）が `TrdEnv.SIMULATE` を選ぶ。
  実弾（`TrdEnv.REAL`）は ADR-0002 の PoC 合格まで行わない（IADR-0016）。
- **無人運用の成立性は本 PoC の検証項目**（デバイス認証/2FA・取引パスワードのアンロック自動化）。

## 手順

### 1) イメージをビルド（OpenD URL はあなたが用意）
```bash
docker build -t k3d-local/ai-stock-trading/opend:latest \
  --build-arg OPEND_URL="https://.../FutuOpenD_<ver>_Ubuntu.tar.gz" \
  deploy/opend
# Rancher Desktop(containerd) の場合:
#   nerdctl --namespace k8s.io build -t k3d-local/ai-stock-trading/opend:latest \
#     --build-arg OPEND_URL="..." deploy/opend
# Docker Desktop + k3d の場合はビルド後 `k3d image import k3d-local/ai-stock-trading/opend:latest -c msp-ast-dev`
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
