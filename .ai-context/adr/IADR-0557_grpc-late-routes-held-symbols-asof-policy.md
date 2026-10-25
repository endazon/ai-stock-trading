---
title: IADR-0557 east-west gRPC 段 5 の追補 —— 段 2〜5 の後に REST だけで足された読み取りのうち、提供側が面を持つ 2 本を同じ切り方で並走させ、発注執行の 4 本は面の新設として別段に分ける
type: impl-adr
status: Accepted
related_ids: [NFR, FR-01, FR-02, FR-04, FR-07, FR-15, MSP:ADR-0029, MSP:ADR-0075, IADR-0284, IADR-0328, IADR-0331, IADR-0427, IADR-0446, IADR-0489, IADR-0528, IADR-0547]
author: endazon (with Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# IADR-0557: east-west gRPC 段 5 の追補（情報収集の保有銘柄・Stage 0 の当時の日報の方針）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-17
- 決定者: Claude Code（実装）／ endazon（段の切り方は #753・段 2〜5 の雛形）

## 起点・関連

- 関連する計画書 ID: **`MSP/ADR-0029`**（同期通信の使い分け基準）、**`MSP/ADR-0075`**（移行順序）。経路の起点は FR-01・FR-02（情報収集の対象銘柄）、
  FR-04・FR-07・FR-15（Stage 0 の記録の当時の方針）。
- 関連する実装仕様書: [`.ai-context/specs/20261017_1388_grpc-late-routes-held-symbols-asof-policy.md`](../specs/20261017_1388_grpc-late-routes-held-symbols-asof-policy.md)
- 前提: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5 と 2026-09-27 追記（段 6 の完了は呼び出し箇所で数える）、
  [IADR-0427](IADR-0427_risk-read-grpc-stage2.md)（原則 A・解釈の共有・チャネルの所有）、
  [IADR-0446](IADR-0446_report-monitor-cost-read-grpc-stage4.md)（未確定は「無い」応答・呼び出しの規則は呼び出し元ごとに 1 つ）、
  [IADR-0489](IADR-0489_grpc-client-declaration-as-map-key-and-measurement-overlay.md)（計測用 overlay）、
  [IADR-0528](IADR-0528_stage0-asof-base-input-supply.md) 決定 3・7（当時の方針・基底の組み立て）、[IADR-0547](IADR-0547_held-symbols-into-collection-targets-and-sec-cik-from-symbols.md) 決定 1（情報収集の保有銘柄）
- 関連 issue: #1388（本件）、#753（段 2〜6 の受け皿・`Refs`）、#1363・#1308（経路の起点）

## コンテキストと課題

段 6 の完了条件は「east-west の呼び出し箇所がすべて gRPC へ移り、呼び出し元の `Http*` アダプタが退役した」ことである。
段 2〜5 を閉じた後（2026-09-28 以降）に、REST だけの east-west の読み取りが 6 本足された（作業仕様書の母集合）。

| 経路 | 提供側の gRPC 面 |
| --- | --- |
| 情報収集 → リスク管理 `open-positions`（#1363） | rpc まである（`RiskControlsRead/GetOpenPositions`） |
| 取引判断（Stage 0）→ 報告書 `daily-policy/as-of`（#1308） | 面はある（`DailyPolicyRead`）。rpc が無い |
| リスク管理 → 発注執行 `short-permit`・取引判断 → 発注執行 `daily-bars`／`in-flight-closes`・バックテスト → 発注執行 `daily-bars` | 面が無い（発注執行は h2c リスナも `grpcPort` も持たない） |

## 決定

### 決定 1 — 本段は「提供側が面を持つ 2 本」。発注執行の 4 本は別段にする

発注執行の 4 本は、提供側の h2c リスナ・helm の `grpcPort`・門（`GrpcOwnerOrService`）・proto の新設が要る。段 1〜4 で
新しい提供側を足したときの大きさであり、既存の面へ rpc を 1 本足す本段とは変更の性質が違う。混ぜるとレビューの単位が崩れる。
本段は 2 本だけを持ち、発注執行は #753 の残りとして数える。

### 決定 2 — 切り方は段 1〜5 と同じ（並走・既定 REST・宣言が BaseUrl より優先）

- 提供側と消費側を同じ PR に入れる。REST は残す。既定は REST で、`RiskManagement:Grpc`・`Reports:Grpc` を宣言したときだけ gRPC。
- 宣言があれば同じ提供側の `*:BaseUrl` より優先する。情報収集の保有銘柄は `RiskManagement:BaseUrl` が空でも、宣言があれば読む。
  Stage 0 の基底は「報告書（`Reports:Grpc` か `Reports:BaseUrl`）と発注執行の接続先がそろう」で組む。
- 呼び出しの規則（試行ごとの deadline・再試行）は、呼び出し元サービスの既存の規則（`InformationCollectionGrpcCalls`・`TradeDecisionGrpcCalls`）を共有する。
  情報収集の新しい輸送の deadline の既定は REST の "risk" と同じ 5 秒である。

### 決定 3 — 当時の方針の rpc は REST と同じサービス・同じ門・同じ入力の検証を通る

- `DailyPolicyRead/GetConfirmedDailyPolicyAsOf` を足す（非破壊。proto の baseline を更新する）。
- 要求の時刻は `string at`（オフセット必須。`WatchlistRead/GetWatchlistAsOf` と同じ）。提供側は REST の `TryParseInstant` を共有し、
  欠落・オフセット欠落・書式違いは `INVALID_ARGUMENT`（REST の 400）。
- 無かったときは `policy` の無い応答（REST の 404。IADR-0446 決定 3 と同じ。`NOT_FOUND` にしない）。
- 応答は `DailyPolicyRecord`（日付・要約・期間キー・版）に、確定の時刻 `optional string confirmed_at`（往復書式・オフセットごと）を並べる。
  受け手が「照会の時刻より後の確定」を拒むための値であり、`GetConfirmedDailyPolicy` の応答には載せない（`DailyPolicyRecord` へ足さない）。

### 決定 4 — 受け手の解釈は REST と同じ 1 つ

- 保有銘柄: 線上の行を REST と同じ nullable の行へ写し、`HttpRiskManagementHeldSymbolsProvider.Interpret` を通す（市場・方向は名前で写し、
  未指定・未知は不明）。照会の失敗は不明（`null`）。
- 当時の方針: REST と同じ撃ち直し（`AsOfRetry`）に載せる。照会の失敗・日付や要約や確定の時刻の欠けた応答は「取れなかった」（撃ち直す）、
  `policy` の無い応答は「無かった」（`null`）。照会の時刻より後の確定は送り手の契約違反として撃ち直さずに記録を止める。

## 理由

- 段 6 は呼び出し箇所で数えるため、後から足された経路も段 2〜5 と同じ形で並走させておかないと、実測（段 6 の着手条件）の対象にも入らない。
- 既存の面を使える 2 本を先に移すと、計測用 overlay の宣言を 1 行足すだけで実測の窓に入る。発注執行は面の新設を伴い、別の判断を要する。

## 結果

- 計測の経路表の呼び出し側の宣言は 13 本から 14 本になる（overlay が既定で入れるのは #6 を除く 12 本から 13 本）。提供側は 6 のまま。
- 段 6 の前に残る AST 内の REST だけの east-west は、発注執行の 4 本である。

## 関連

- Supersedes: なし
- Superseded by: なし
