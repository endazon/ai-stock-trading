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

1. **バイナリ非同梱の Docker Image** とする。OpenD(Linux) の**バイナリはイメージに焼かず・コミットせず**
   （再配布/EULA 回避・~440MB）、**公式取得の tar.gz をビルド時にコンテキストへ取り込む**（PoC で当初の
   「ダウンロード URL 方式」から変更。配布が口座ログイン前提のため。`.gitignore`＋ビルドスクリプトで一時配置）。
   バージョンは pin する。
2. **k8s には `opend` Deployment/Service（ClusterIP :11111）としてオプトイン配備**する（AST chart に
   `opend.enabled`、**既定 false**＝fail-safe。OpenD 不在時は moomoo を選べず paper のまま）。
3. **資格情報は k8s Secret / 環境変数**で注入し、`FutuOpenD.xml` をマウントする（コミットしない。暫定 Secret、
   恒久は Vault 等）。
4. **dev は SIMULATE（ペーパー）**（`TrdEnv.SIMULATE`）に限定する。実弾は本 IADR の対象外（money-safety）。
5. ~~**デバイス認証の永続化で無人再起動**~~ → **撤回（PoC で不成立）**。下記「PoC 結果」のとおり永続化しても
   再検証が要るため、**常駐モデル**（起動時のみ対話検証・以降は再起動を避けて常駐）を採用する。

## PoC 結果（2026-07-15・初回検証。#124）

実バイナリ `moomoo_OpenD_10.8.6818`（コマンドライン版・実行ファイル `OpenD`・設定 `OpenD.xml`）で検証:

- ✅ **ビルド成功**。ベースは `mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy`（nerdctl の docker.io 認証ヘルパ
  失敗を避けるため mcr を採用。当初の「ダウンロード URL 方式」は口座ログインが要る配布のため、**参照 tar.gz を
  ビルドコンテキストへ一時配置する取り込み方式**へ変更した）。
- ✅ **共有ライブラリ充足**（ダミー資格情報でも `error while loading shared libraries` は出ず OpenD 起動。
  追加 apt は `libgomp1`/`libglib2.0-0` のみ）。
- ✅ **実口座でログイン成功**（画像 CAPTCHA `input_pic_verify_code` ＋ SMS `input_phone_verify_code`。権限取得）。
  規制アンケート（`api.moomoo.com/v2`・口座で一度きり）完了後は OpenD が**常駐継続**する。
- 🔴 **完全無人（自動再起動）は不可（確定）**。デバイス状態を **home（`/root/.com.moomoo.OpenD`）＋install
  （`/opt/opend/AppData.dat` 等）の両方**を PVC 永続化しても、**新 Pod（＝新 IP）は再び画像/SMS 検証を要求**した。
  検証は **IP/セッション依存**で、永続化では回避できない（experiment-appdata.yaml で確認）。
- ➡️ **決定を更新: 常駐モデル**。当初の「初回認証→永続化→無人」案は成立しないため撤回。OpenD を**長時間常駐**させ、
  **起動/再起動のたびに 1 回だけ対話で検証**（`kubectl attach -it deploy/opend` → `input_*_verify_code`）、
  **再起動を極力避ける**（安定ノード・rolling 不使用・単一レプリカ）。#13 は稼働中 `opend:11111` へ SIMULATE 接続。

## 未確定（Accepted の条件・残）

- 海外 IP（Hetzner）からの OpenD 接続可否と利用規約上の扱い（ADR-0002 未決）。
- 長期常駐の安定性・強制アップデート頻度・取引パスワードのアンロック（SIMULATE で不要な範囲の切り分け）。
- **ADR-0002「無人運用の成立性」への回答: 限定的成立（起動時有人・以降常駐）。→ /plan-feedback で環流する。**
- 口座条件・市況データ権限・取引パスワードのアンロック（SIMULATE で不要な範囲の切り分け）。

## トレードオフ・代替案

- **同梱方式**（バイナリをイメージに焼く）: 起動が単純だが EULA/再配布リスク。→ 不採用（ダウンロード方式）。
- **ホスト常駐（コンテナ化しない）**: 現行 desktop 運用のまま。k8s 一貫配備・再現性で劣る。→ dev では非採用。
- **既定有効**: 資格情報必須で fail-safe を壊す。→ 不採用（既定無効・オプトイン）。

## 影響

- 追加（試作・#124）: `opend` の Dockerfile（ダウンロード方式）・k8s manifest（chart オプトイン）・Secret 雛形。
- コード影響なし（本 IADR 時点）。moomoo アダプタ実装は #13。
- Accepted 時: ADR-0002 の未決（無人運用/Hetzner）を /plan-feedback で計画へ環流する。
