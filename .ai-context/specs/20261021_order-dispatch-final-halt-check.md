---
title: 発注執行が新規建てをブローカーへ送る直前に kill switch と一時停止の状態を最終照会し、状態が不明なら送らない（多層防御）
type: spec
status: done
related_ids: [FR-10, FR-05, FR-14, UC-06, NFR-04, ADR-0003, ADR-0009, IADR-0567, IADR-0051, IADR-0420, IADR-0211, IADR-0398, IADR-0356, IADR-0481, IADR-0284]
author: claude (Claude Code)
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md（FR-10 本文「kill switch・日次損失ロックアウト・一時停止は…いずれも手仕舞い（Close）と損切りは止めない」・受け入れ基準「kill switch 起動後、新規発注が一切行われない」「一時停止中でも手仕舞い・損切りは止まらない」・NFR-04「障害時は安全側に倒す（新規発注停止）」）
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md（決定 3・4）
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 発注直前の停止状態の最終照会（fail-closed）

> 本仕様書は実装着手前に作成した（着手 2026-10-21。AST `origin/develop` `40de5a61`・計画 `origin/main` `55e464a`）。
> 多層防御の強化。参照は GHSA-pvmh-cvmf-pvcp（一部）。オーナー裁定 2026-10-21。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-10**（kill switch・一時停止）、FR-05（発注執行）、FR-14（操作の窓口）。UC-06。NFR-04（障害時は新規発注を止める）。
- 関連 ADR: **ADR-0009 決定 3・4**（優先順位 kill switch ＞ 日次損失ロックアウト ＞ 一時停止。**3 統制とも新規建てのみを止め、手仕舞い・損切りは止めない**）、ADR-0003（統制は決定的なコードで強制）。
- 新規 IADR: **IADR-0567**

## 現状（調査の結果）

| 項目 | 現状 |
| --- | --- |
| 状態の持ち主 | リスク管理（`IKillSwitchStore` / `IPauseStore`。EF の単一行）。審査（`RiskEvaluator`）は `EntryStateBlockers.KillSwitch` / `.Paused` で新規建て（`isEntry`）だけを拒否する |
| 状態を読む口 | REST `GET /risk-controls/kill-switch`・`/pause` は **OwnerOnly**（操作者・理由も返す）。サービスが読めるのは銘柄単位の `GET /risk-controls/entry-blockers`（OwnerOrService）と gRPC `RiskControlsRead/GetEntryBlockers` だけ（建玉・設定・台帳の射影を毎回組む） |
| 既存の照会の例 | 取引判断の `HttpEntryBlockersProvider` / `GrpcEntryBlockersProvider`（"risk" HttpClient 5 秒・s2s トークン・nullable の DTO・失敗は null＝不明） |
| 発注の経路 | `OrderApprovedHandler.Handle` → `OrderExecutionAppService.ExecuteAsync`（相 1 完了の照合 → 見送り済みの照合 → 決済の建玉照合 → 手法の解決・新規建ての前提の判定 → 相 2 予約 → 保護の文脈の記録 → 送信の直前の手法の併存の判定 → **相 3 送信**）。新規建てを送るのはこの 1 か所だけ（他の送信は保護レグ・成行の手仕舞い・S1 の決済＝すべて決済方向） |
| 発注執行 → リスク管理の同期照会 | **無い**（発注執行は今まで承認イベントだけを受けていた） |

## 方針（IADR-0567 の要約）

1. **新規建て（`PositionEffect != Close`）だけ**を、予約を取った後・**ブローカーへ送る直前**に照会する。決済・損切り（保護レグ・成行手仕舞い・S1 の決済）は照会しない＝止めない（ADR-0009 決定 4）。
2. リスク管理に OwnerOrService の読み取り口 `GET /risk-controls/trading-halt` を足す（`{ killSwitchEngaged, tradingPaused }` の 2 値だけ。操作者・理由は返さない＝最小限の露出）。値は審査と同じストアから読む。
3. 発注執行の受け手 `HttpTradingHaltSource`（"risk" HttpClient・s2s トークン・**待ち 3 秒・1 試行**）。非 2xx・例外・タイムアウト・本文の欠落・項目の欠落は**不明**。照会先が構成されていなければ**不明**（`UnconfiguredTradingHaltSource`）。
4. 判定: 不明 → `TradingHaltStateUnknown`、kill switch → `KillSwitchActive`、一時停止 → `TradingPaused`（両方なら kill switch を採る＝ADR-0009 の優先順位）。いずれも予約を Forgone へ移し（S1 の行・保護の文脈も閉じる）、`OrderDispatchForgone` を発行して**送らない**（送信の直前の手法の併存の判定と同じ形）。
5. 見送りの理由 3 値を共有契約の末尾へ足す（序数 9・10・11）。リスク管理の在庫解放の allowlist へ「確実に未発注」として足す（送信前に return する）。通知の文言を足す。
6. gRPC の口は今回は足さない（既定の配備は REST。gRPC の追補は同型の後続〔IADR-0557 の形〕で足せる）。

## 母集合（規則 9・10）

走査語: `OrderDispatchForgoneReason`（列挙を switch・数える箇所）・`StopLossMethodConflict`（直近に足された見送り理由の追随先）・`new OrderExecutionAppService(`（構築箇所）・`PositionEffect.Open`（新規建ての送信箇所）・`PlaceOrderAsync` / `PlaceMarketOrderAsync`（送信箇所）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `Shared.Contracts/Events/OrderDispatchForgone.cs` | 3 値を末尾へ足す |
| 2 | `RiskManagementService/Domain/OrderDispatchForgoneLifecycle.cs` | 3 値を `true`（送信前に return） |
| 3 | `OrderDispatchForgoneLifecycleTests`（要素数 9 → 12・肯定形）・`StopLossMethodContractTests`（序数・要素数）・`PortfolioLedgerConsumersTests.現行の見送り理由はいずれも処理中から外れる` | 追随 |
| 4 | `NotificationFormatter.ReasonLabel` | 文言を足す（既定 `ToString()` に落とさない） |
| 5 | `OrderExecutionAppService` | 照会と判定を送信の直前へ足す（任意依存。Program.cs は必ず渡す） |
| 6 | `OrderExecutionService/Program.cs` | "risk" HttpClient と `ITradingHaltSource` の登録、app service へ渡す |
| 7 | `RiskControlEndpoints`（read 群）・`Program.cs`（サービス登録） | 口を足す |
| 8 | `PlaceOrderAsync` / `PlaceMarketOrderAsync` の他の呼び出し（保護レグ・成行手仕舞い・S1 の決済・ガード） | **触らない**（すべて決済方向＝止めない） |
| 9 | `OrderExecutionPipelineE2ETests`（Integration・既定 CI 除外）・`new OrderExecutionAppService(` の試験 45 箇所 | 任意依存のため既存の試験は不変。E2E は照会元を差し替える |

除外: 監査・報告書は見送りの理由を整数・名前で汎用に扱う（switch していない）ため追随不要。`AuditEntryFactoryTests` 等は既存値の固定のみ。

## 受け入れ基準と試験の写像

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-10-2965 | kill switch が成立中 → 新規建てを送らない（`KillSwitchActive` で見送り・予約は Forgone・S1 の行／保護の文脈は閉じる） | `OrderExecutionServiceTradingHaltTests` |
| T-10-2966 | 一時停止中 → 新規建ては送らない（`TradingPaused`）。**決済は照会せずに送る**（一時停止中・kill switch 中・照会失敗中のいずれでも） | 同上 |
| T-10-2967 | 照会の失敗・タイムアウト・不正な本文・項目の欠落・照会先の未構成 → 送らない（`TradingHaltStateUnknown`）。理由をログに残す | `HttpTradingHaltSourceTests`・`OrderExecutionServiceTradingHaltTests` |
| T-10-2968 | 正常（どちらも成立していない）→ 送る | `OrderExecutionServiceTradingHaltTests` |
| T-10-2969 | 両方成立 → kill switch を理由にする（優先順位） | 同上 |
| T-10-2970 | 受け手が送り手の本物の型（`TradingHaltView`）を web 既定で直列化した本文を読める（契約）。送り手の本物の Program.cs が同じ本文を出す（wire format）。口は OwnerOrService | `HttpTradingHaltSourceTests`・`TradingHaltEndpointTests`（送り手の wire format は本物の Program.cs で `JsonNode.DeepEquals`） |
| T-10-2971 | 本番の組み立て（Program.cs）で、照会先が未構成なら新規建てを送らない／構成済みなら照会元が Http になる | `TradingHaltWiringTests` |
| T-10-2972 | 見送り理由の序数 9・10・11 と、在庫解放の allowlist（確実に未発注） | `StopLossMethodContractTests`・`OrderDispatchForgoneLifecycleTests` |

変異: (M1) 判定を外す・(M2) 決済も照会する・(M3) 不明を「通す」へ倒す・(M4) kill switch と一時停止の順を入れ替える・(M5) 項目の欠落を false と読む・(M6) Program.cs で照会元を渡さない。各 1 本ずつ当てて赤を確かめる（結果は IADR-0567 に記録）。

## 配備への影響

- 照会先（`RiskManagement:BaseUrl`）と s2s の資格情報（`ServiceAuth:*`）を発注執行へ与えるまで、**新規建ては全件見送られる**（fail-closed）。決済・損切りは影響を受けない。
- 手順（helm の値・ネットワークの許可）は PR 本文に書く（本 PR は `backend/` と記録だけを変える）。

［2026-10-22 追記］配備の値を同じ PR に足した（オーナー・調整役の指示）: helm `values.yaml` の発注執行の `extraEnv` に `RiskManagement__BaseUrl`（`http://risk-management-service:8080`）と
`ServiceAuth__ClientId` / `ServiceAuth__ClientSecret`（`ast-secrets` の `service-auth-client-id` / `-secret`）、`docker-compose.yml` に同じ値と token エンドポイント。
`values-local.yaml` は発注執行の `extraEnv` を上書きしていないため既定を継ぐ（写すと Helm のリスト置換で既定の env を落とす危険が増えるだけ）。描画は T-10-2973（`helm.yml`）で固定した。
試験の写像の訂正: T-10-2970 は `TradingHaltEndpointTests` / `HttpTradingHaltSourceTests`（`ReadContractWireFormatTests` は変更していない）。
