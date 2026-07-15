---
title: moomoo OpenD の無人運用は限定的成立（常駐モデル）— ADR-0002 未決事項への PoC 回答
type: plan-feedback
status: open
category: 新たな制約(ADR要)
related_ids:
  - ADR-0002
source_repo: ai-stock-trading
source_ref: "feat/124-opend-docker / IADR-0053 / docs/opend PoC（2026-07-15）"
author: claude
created: 2026-07-15
---

# フィードバック: moomoo OpenD の無人運用は限定的成立（常駐モデル）

## 種別

新たな制約(ADR要)。ADR-0002 の未決事項「**OpenD 無人運用の成立性**」に、実バイナリ PoC で得た回答を環流する。

## 起点となる計画書

- 機能要求（FR）: FR-05（発注執行）
- ユースケース（UC）: UC-01/02
- 画面（SC）: —
- 関連 ADR: **ADR-0002**（証券会社連携は moomoo OpenAPI を第一候補・Proposed）
- 計画書リンク: `projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md` /
  `projects/ai-stock-trading/06_technical/03_moomoo-integration.md`

## 現状（計画書の記述 / As-Is）

ADR-0002 は Accepted の条件に「デモ取引（SIMULATE）での PoC」を挙げ、未決事項として
「**OpenD 無人運用の成立性**（長期常駐の安定性・取引パスワードのアンロック自動化・強制アップデート頻度）」
「海外 IP（Hetzner）からの接続・利用規約」を掲げている。03_moomoo-integration も
「無人運用での安全なアンロック手順は PoC で検証する」と記す。

## 問題点 / あるべき姿（To-Be）

**PoC の結果、OpenD は「完全無人（自動再起動）」では運用できないことが判明した。** 計画側の
「無人運用の成立性」の前提を、**「限定的成立（起動時のみ有人・以降常駐）」**へ明確化すべき。

## 実装で判明した経緯

実バイナリ `moomoo_OpenD_10.8.6818`（コマンドライン版）を Docker/k8s 化（#124・IADR-0053）し、実口座で検証:

1. ✅ コンテナで OpenD は起動・moomoo にログイン・API 稼働（権限取得）まで到達。
2. 🔴 **ログイン時に対話デバイス検証（画像 CAPTCHA / SMS）が毎回必要**。デバイス状態を
   home（`/root/.com.moomoo.OpenD`）＋install（`AppData.dat` 等）の**両方を永続化しても、
   新コンテナ（＝新 IP）は再び検証を要求**した。検証は **IP/セッション依存**で永続化では回避不可。
3. 追加制約: 初回に **API 利用規制アンケート**（`api.moomoo.com/v2`）の完了が口座単位で必須。

→ k8s で Pod を再起動するたびに人手の検証が要るため、**自動再起動・自動スケールを前提にした無人運用は不可**。

## 提案（計画への反映案）

- 反映先候補: **ADR-0002 の更新**（未決事項→確定事項へ）／必要なら 03_moomoo-integration の追記
- 提案内容:
  - ADR-0002 の「無人運用の成立性」を **「限定的成立：OpenD は常駐モデルで運用する。起動/再起動のたびに
    対話デバイス検証（画像/SMS）が必要で、永続化では回避できない（IP/セッション依存）。よって再起動を最小化し
    （安定ノード・単一インスタンス・ローリング更新を避ける）、起動時のみ有人で認証する運用とする」** と明文化。
  - 前提条件に「**初回に API 規制アンケートの完了が口座単位で必須**」を追加。
  - Hetzner（海外 IP）接続可否・ToS、取引パスワードアンロックの自動化可否は引き続き未検証（要 PoC 継続）。
  - 可用性設計への含意: OpenD は SPOF になり得る（再起動＝有人）。冗長化は `IBrokerAdapter` 差し替え
    （立花証券 e支店 等）を将来の代替として ADR-0002 の既述どおり保持。

## 影響範囲

- FR-05（発注執行・#13）: moomoo アダプタは**稼働中の常駐 OpenD（`opend:11111`）へ SIMULATE 接続**する前提で実装する。
- 運用（NFR 可用性）: OpenD 再起動時の一時的な発注不可を許容/監視する設計が要る。
- インフラ（#24・Hetzner）: 自動デプロイ/自動復旧の対象から OpenD 認証を除外（有人手順として分離）。
