---
title: IADR-0053 moomoo OpenD はダウンロード方式の Docker Image で常駐させ、k8s に opend サービスとしてオプトイン配備する（Proposed）
type: impl-adr
status: Proposed
related_ids:
  - ADR-0002 # 証券会社連携=moomoo OpenAPI（OpenD 常駐が必要）
  - IADR-0016 # 実弾防止ゲート（既定 paper）
  - IADR-0052 # AST k8s Helm chart
author: claude
created: 2026-07-14
updated: 2026-07-14
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md"
  - "../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md"
---

# IADR-0053: moomoo OpenD の Docker Image 化（Proposed）

- 状態: **Proposed**（検討・試作段階。Accepted は無人運用/Hetzner 接続の一次確認後）
- 日付: 2026-07-14
- 決定者: claude（実装・起案）

## 起点・関連

- 関連計画 ID: ADR-0002（moomoo OpenAPI 第一候補。**OpenD ゲートウェイの常駐が必要**、未決=「OpenD 無人運用の
  成立性」「海外 IP(Hetzner) 接続・ToS」）／IADR-0016（実弾防止ゲート・既定 paper）
- Issue: #124（OpenD Docker 化の検討・試作）／ #13（moomoo アダプタ実装）
- 前提環境: MSP #266 / AST #122（連結ローカル k8s dev）

## コンテキストと課題

moomoo 発注（#13）は OpenD（FutuOpenD）ゲートウェイの常駐が前提（既定 :11111）。現状 `BrokerFactory` は
`Broker:Provider=moomoo` を選ぶと起動停止する（実弾防止・IADR-0016）。連結 k8s dev で OpenD をコンテナ常駐
できれば、moomoo アダプタは `opend:11111` へ接続する構成に落とせる。課題は **無人ログイン（デバイス認証/2FA）**、
**バイナリ再配布(EULA)**、**資格情報の秘匿**、**海外 IP 接続の ToS**。

## 決定（方向性・Proposed）

1. **ダウンロード方式**の Docker Image とする。FutuOpenD(Linux) の**バイナリはイメージに同梱せず**、ビルド時
   もしくは初回起動時に公式配布から取得する（再配布/EULA 回避）。バージョンは pin する。
2. **k8s には `opend` Deployment/Service（ClusterIP :11111）としてオプトイン配備**する（AST chart に
   `opend.enabled`、**既定 false**＝fail-safe。OpenD 不在時は moomoo を選べず paper のまま）。
3. **資格情報は k8s Secret / 環境変数**で注入し、`FutuOpenD.xml` をマウントする（コミットしない。暫定 Secret、
   恒久は Vault 等）。
4. **dev は SIMULATE（ペーパー）**（`TrdEnv.SIMULATE`）に限定する。実弾は本 IADR の対象外（money-safety）。
5. **デバイス認証の永続化**: 初回のみ対話ログインでデバイス承認 → デバイストークン/設定を PVC に永続化し、
   以降は無人再起動で再ログインを回避する方式を試作で検証する（成立性が Accepted の条件）。

## 未確定（Accepted の条件・#124 で消化）

- 無人運用（デバイス認証/2FA を通した再起動耐性）の成立性。
- 海外 IP（Hetzner）からの OpenD 接続可否と利用規約上の扱い（ADR-0002 未決）。
- 口座条件・市況データ権限。

## トレードオフ・代替案

- **同梱方式**（バイナリをイメージに焼く）: 起動が単純だが EULA/再配布リスク。→ 不採用（ダウンロード方式）。
- **ホスト常駐（コンテナ化しない）**: 現行 desktop 運用のまま。k8s 一貫配備・再現性で劣る。→ dev では非採用。
- **既定有効**: 資格情報必須で fail-safe を壊す。→ 不採用（既定無効・オプトイン）。

## 影響

- 追加（試作・#124）: `opend` の Dockerfile（ダウンロード方式）・k8s manifest（chart オプトイン）・Secret 雛形。
- コード影響なし（本 IADR 時点）。moomoo アダプタ実装は #13。
- Accepted 時: ADR-0002 の未決（無人運用/Hetzner）を /plan-feedback で計画へ環流する。
