---
title: IADR-0567 発注執行は新規建てをブローカーへ送る直前に kill switch・一時停止の状態をリスク管理へ最終照会し、成立中または不明なら送らない（fail-closed）。決済・損切りは照会しない。照会は REST 1 試行・待ち 3 秒
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-05, FR-14, UC-06, NFR-04, NFR-01, ADR-0003, ADR-0009, IADR-0051, IADR-0057, IADR-0211, IADR-0356, IADR-0398, IADR-0420, IADR-0428, IADR-0481, IADR-0284, IADR-0557]
author: claude (Claude Code)
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md（FR-10・受け入れ基準・NFR-04）
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md（決定 3・4）
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
related_specs:
  - ../specs/20261021_order-dispatch-final-halt-check.md
---

# IADR-0567: 発注直前の停止状態の最終照会（fail-closed）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-21
- 決定者: オーナー裁定（2026-10-21。多層防御の強化。参照 GHSA-pvmh-cvmf-pvcp の一部）。実装の形は Claude Code。

## 起点・関連

- 計画: FR-10（kill switch・日次損失ロックアウト・一時停止は**いずれも新規建てを止め、手仕舞い・損切りは止めない**。受け入れ基準「kill switch 起動後、新規発注が一切行われない」「一時停止中でも手仕舞い・損切りは止まらない」）、
  ADR-0009 決定 3（優先順位 kill switch ＞ 日次損失ロックアウト ＞ 一時停止）・決定 4（共通不変条件）、NFR-04（障害時は安全側＝新規発注停止）。
- 前提（覆さない）: 審査（リスク管理 `RiskEvaluator`）が新規建てを kill switch・一時停止で拒否する経路は**そのまま残す**（本 IADR は送信の手前にもう 1 段を足す）。
  IADR-0051（読み取りはサービス間 s2s で OwnerOrService、操作は OwnerOnly）、IADR-0057（予約 → 発注 → 確定）、IADR-0398（見送りは予約表へ Forgone として記録し再配送で送らない）、
  IADR-0356（在庫解放は「確実に未発注」の理由だけ）、IADR-0420（サービス間 HTTP の契約テスト）、IADR-0481 決定 1（送信の直前の判定の形）。
- 仕様書: `.ai-context/specs/20261021_order-dispatch-final-halt-check.md`

## コンテキスト

- 停止の状態の持ち主はリスク管理（`IKillSwitchStore` / `IPauseStore`）。発注執行は承認イベント（`OrderApproved`）を受けて送るだけで、送る時点の停止の状態を見ていなかった。
- サービスが読める状態の口は銘柄単位の `GET /risk-controls/entry-blockers`（建玉・設定・台帳の射影を毎回組む）だけで、`/kill-switch`・`/pause` は OwnerOnly（操作者・理由も返す）。
- 発注執行が新規建てをブローカーへ送る箇所は `OrderExecutionAppService.ExecuteAsync` の相 3 の 1 か所だけである（他の送信＝保護レグ・成行の手仕舞い・S1 の決済・ガードの決済はすべて決済方向）。

## 検討した選択肢

| 案 | 内容 | 採否 |
| --- | --- | --- |
| A | `GET /risk-controls/entry-blockers` を流用して kill switch・一時停止の理由だけを見る | 不採用。建玉・口座・台帳の射影を毎回組むため、停止と無関係な失敗（口座照会の不調など）で新規建てが止まる。待ちも長い |
| B | `/kill-switch`・`/pause` をサービスへ開く | 不採用。操作者・理由まで出る（読み手が要らない情報）。OwnerOnly の区画を崩す |
| **C** | **2 値だけの読み取り口 `GET /risk-controls/trading-halt`（OwnerOrService）を足し、送信の直前に読む** | **採用** |
| D | 停止の変更をイベントで発注執行へ配り、発注執行が状態を持つ | 不採用。配送の遅れ・取りこぼしの窓が残り「不明」を判定できない。持ち主が 2 つになる |

送る位置: 予約の**前**（建玉照会と同じ位置）は、予約の後の処理（ms 単位）のあいだに入った停止を見ない。予約の**後・送信の直前**（IADR-0481 の後の端と同じ位置）を採る。

## 決定

### 決定 1: 範囲は新規建てだけ。決済・損切りは照会しない

- `intent.PositionEffect != Close` のときだけ照会する（列挙の未知の値も照会する側へ倒す）。
- 決済（利用者の手仕舞い・自動縮小・判断の手仕舞い・成行の手仕舞い）・保護レグ・S1 の決済・ガードの決済は**照会しない**。照会の失敗で手仕舞い・損切りを止めないためである（ADR-0009 決定 4）。

### 決定 2: 状態の口と受け手

- リスク管理: `GET /risk-controls/trading-halt`（読み取り群・OwnerOrService）。本文は `TradingHaltView(KillSwitchEngaged, TradingPaused)` の 2 値だけ。
  値は審査と同じストア（`IKillSwitchStore` / `IPauseStore`）から読む。操作の口と操作者つきの現況は従来どおり OwnerOnly。
- 発注執行: `ITradingHaltSource`（Features）と `HttpTradingHaltSource`（"risk" HttpClient・`AddAiStockTradingServiceToken`）。受け手の行は nullable で受け、
  **非 2xx・例外・タイムアウト・本文の欠落・項目の欠落・型の不一致は不明（null）**。項目の欠落を false（停止していない）と読まない。
- 照会先 `RiskManagement:BaseUrl` が未設定・不正なら `UnconfiguredTradingHaltSource`（常に不明）。**照会を省く構成の口は持たない。**
- 本番の Program.cs は照会元を必ず `OrderExecutionAppService` へ渡す（引数は省略可能なので、渡し忘れは T-10-2971 が Program.cs そのもので赤にする）。

### 決定 3: 判定と見送りの記録

- 不明 → `TradingHaltStateUnknown`、kill switch 成立 → `KillSwitchActive`、一時停止だけ成立 → `TradingPaused`（両方なら kill switch。ADR-0009 決定 3）。
- 送らないときは、予約を Forgone へ移し（IADR-0398）、S1 の行と保護の文脈（AwaitingEntry）を閉じ、`OrderDispatchForgone` を発行する（IADR-0481 決定 1 の後の端と同じ形）。
  ログは不明なら Error、成立なら Warning（DecisionId・銘柄・数量・理由）。監査台帳・通知・メトリクス（`reason` タグ）は既存の見送りの経路にそのまま載る。
- 見送り理由 3 値は共有契約 `OrderDispatchForgoneReason` の**末尾**（序数 9・10・11）。リスク管理の在庫解放の allowlist では「確実に未発注」（送信前に return）。
- 照会元が例外を投げても不明として見送る（予約を Reserved のまま残さない）。

### 決定 4: 待ちの上限と発注の遅れ

- **1 回の照会の待ちの上限は 3 秒（"risk" HttpClient.Timeout）、1 試行（再試行しない）。** 呼び出し元の取り消しでも投げずに不明を返す（予約の後に呼ぶため）。
- 遅れへの影響: 新規建て 1 件ごとにクラスタ内の往復 1 回（リスク管理は単一行の読み取り 2 回）が送信の前に加わる。平常は数 ms〜数十 ms、**最悪は 3 秒待って見送り**。
  NFR-01（価格変動の検知から発注完了まで 5 分以内）の予算に対して最悪でも 1% 程度であり、指値の価格は承認時に決まっているので値の陳腐化は 3 秒分に限られる。
  決済・損切りの遅れは 0（照会しない）。3 秒より長くすると停止の不明な時間に予約が押さえられる時間が延び、短くすると平常の揺らぎで新規建てを取りこぼす。
- 再試行しない理由: 停止の照会の失敗は見送りに倒れ、再発注は次の取引判断から行われる（IADR-0211 決定 3）。送信の手前で待ちを重ねない。

### 決定 5: 輸送は REST（gRPC の追補は後続）

- 既定の配備は REST であり、gRPC の面（`RiskControlsRead`）はまだ既定で開いていない。本件は REST の 1 本だけを足す。
  gRPC の rpc を足す追補は IADR-0557 と同型で後から足せる（受け手の解釈 `HttpTradingHaltSource.Interpret` を共有する）。

## 試験と変異

- T-10-2965〜T-10-2969: `OrderExecutionServiceTradingHaltTests`（停止中・一時停止中・両方・不明・照会元の例外・正常・S1・決済は照会しない〔指値・成行〕）。
- T-10-2967・T-10-2970: `HttpTradingHaltSourceTests`（送り手の本物の型の契約・非 2xx・欠落・型の不一致・例外・タイムアウト・取り消し・未構成）、
  `TradingHaltEndpointTests`（OwnerOrService・本文は応答型の web 既定・項目は 2 つだけ・`/kill-switch`・`/pause` はサービスに閉じたまま）。
- T-10-2971: `TradingHaltWiringTests`（本番の Program.cs: 未構成なら送らない／構成済みなら HTTP で照会して状態に従う）。
- T-10-2972: `StopLossMethodContractTests`（序数）・`OrderDispatchForgoneLifecycleTests`（要素数 12・allowlist）。
- 変異（1 本ずつ当てて戻した。いずれも赤）: M1 判定を外す（11 件赤）・M2 決済も照会する（5）・M3 不明を通す（3）・M4 kill switch と一時停止の順を入れ替える（1）・
  M5 項目の欠落を false と読む（6）・M6 Program.cs が照会元を渡さない（4）・M7 見送りで S1 の行・保護の文脈を閉じない（4）。
  契約の検査器（`CrossServiceReadContractTests`）は、受け手の契約テストを外すと本経路を名指しで赤にすることを確かめた。

## 結果

- 良い影響: 審査の承認の後・送信の前に入った停止でも新規建てが送られない。状態が分からないときも送らない。決済・損切りは影響を受けない。
- 悪い影響・トレードオフ: 照会先と s2s の資格情報を発注執行へ与えるまで、新規建ては全件見送られる（fail-closed）。新規建て 1 件ごとに照会 1 回の遅れが加わる。

## 残る制約

- **配備の前提**: 発注執行に `RiskManagement:BaseUrl` と `ServiceAuth:*`（`trading-service` ロールを持つクライアント）を与え、発注執行からリスク管理の HTTP ポートへの通信を許すこと。
  与えない間は新規建てが止まる（決済・損切りは動く）。リスク管理（口を足した版）を先に配備する（逆順だと 404 → 不明 → 見送り）。
- 照会の後・送信までの数 ms のあいだに入った停止は見ない（審査と本照会の 2 段で窓を狭めたが 0 にはしない）。
- 日次損失ロックアウトは本照会の対象外（審査が止める。オーナー裁定の範囲は利用者が手で入れる kill switch と一時停止の 2 つ）。
