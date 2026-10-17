---
title: IADR-0559 east-west gRPC 段 5 の追補 2 —— 発注執行に gRPC の読み取り面（OrderExecutionRead）を新設し、REST だけで呼ばれていた 4 本（借株可否・日足 2・処理中の決済）を段 1〜5 と同じ切り方で並走させる
type: impl-adr
status: Accepted
related_ids: [NFR, FR-04, FR-10, FR-15, UC-06, MSP:ADR-0029, MSP:ADR-0075, ADR-0047, IADR-0284, IADR-0328, IADR-0331, IADR-0425, IADR-0427, IADR-0446, IADR-0448, IADR-0467, IADR-0489, IADR-0528, IADR-0535, IADR-0557]
author: endazon (with Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# IADR-0559: east-west gRPC 段 5 の追補 2（発注執行の読み取り面の新設）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-17
- 決定者: Claude Code（実装）／ endazon（段の切り方は #753・段 2〜5 の雛形）

## 起点・関連

- 関連する計画書 ID: **`MSP/ADR-0029`**（同期通信の使い分け基準）、**`MSP/ADR-0075`**（移行順序）。経路の起点は FR-10・UC-06（借株可否）、
  FR-04・FR-15（日足）、FR-04・FR-10（処理中の決済）。
- 関連する実装仕様書: [`.ai-context/specs/20261017_1391_grpc-order-execution-read-surface.md`](../specs/20261017_1391_grpc-order-execution-read-surface.md)
- 前提: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5 と 2026-09-27・2026-10-17 の追記（段 6 の完了は呼び出し箇所で数える）、
  [IADR-0557](IADR-0557_grpc-late-routes-held-symbols-asof-policy.md) 決定 1（発注執行の 4 本は面の新設として別段）、
  [IADR-0427](IADR-0427_risk-read-grpc-stage2.md)（原則 A・解釈の共有・チャネルの所有）、[IADR-0448](IADR-0448_grpc-owner-gate-requires-bot-azp.md)（gRPC 面の門）、
  [IADR-0489](IADR-0489_grpc-client-declaration-as-map-key-and-measurement-overlay.md)（呼び出し側の宣言と計測用 overlay）、
  [IADR-0425](IADR-0425_short-sell-context-supplier.md)（借株可否）、[IADR-0467](IADR-0467_decision-volume-from-daily-kline-via-order-execution.md)（判断の日足）、
  [IADR-0528](IADR-0528_stage0-asof-base-input-supply.md) 決定 7・8（Stage 0 の基底・バックテストの過去データ源）、[IADR-0535](IADR-0535_skip-llm-while-close-in-flight-covers-position.md)（処理中の決済）
- 関連 issue: #1391（本件）、#753（段 2〜6 の受け皿・`Refs`）、#1388（直前の追補）

## コンテキストと課題

段 6（REST の撤去）の前に、REST だけで呼ばれている east-west の読み取りが 4 本残っていた（呼び出し箇所は 5。作業仕様書の母集合）。
宛先はどれも発注執行で、発注執行は h2c リスナも helm の `grpcPort` も proto も持たない。

| 呼び出し元 | 経路 | 組み立てる箇所 |
| --- | --- | --- |
| リスク管理 | `GET /order-execution/short-permit` | `IShortSellBorrowSource` |
| 取引判断 | `GET /order-execution/daily-bars` | 日足の共有口（出来高・ATR(14)）と Stage 0 の基底（当時の価格）の 2 か所 |
| 取引判断 | `GET /order-execution/in-flight-closes` | `IInFlightCloseProvider` |
| バックテスト | `GET /order-execution/daily-bars` | 過去データ源 `order-execution` |

## 決定

### 決定 1 — 発注執行に読み取りの gRPC 面 `aistocktrading.orderexecution.v1.OrderExecutionRead` を新設する

- rpc は `GetShortPermit`・`GetDailyBars`・`GetInFlightCloses` の 3 本（REST の 3 つの照会の口と 1 対 1）。
- h2c リスナは共有の `AddAiStockTradingGrpcListener`（`Grpc:Port` が無ければ立たない）。helm は `services.order-execution.grpcPort` を
  **宣言したときだけ**第 2 ポートと env を描き、呼び先の表（`deployment.yaml` の `$grpcCallees`）へ `OrderExecution` を足す。既定描画は変わらない。
- 門は `GrpcOwnerOrService`（service のクラス属性。deny-by-default）。資格情報が無ければ `UNAUTHENTICATED`、ロールが無ければ `PERMISSION_DENIED`、
  所有者はボットの機密クライアント（`azp`）だけ。helm は他の提供側と同じく `Auth__GrpcOwnerClients__0` を同じ秘密鍵から入れる（未構成ならコード既定のボット）。
- 照会は REST と**同じ照会サービス**、入力の検証は REST の検証を関数（各 Endpoint の `IsValidQuery`）へ切り出して**共有する**
  （規則は 1 つも変えない）。欠落・値域外は `INVALID_ARGUMENT`（REST の 400）。

### 決定 2 — 線上の表現（原則 A）

- 状態の列挙は `*_UNSPECIFIED = 0` を持ち、送り手の「分からない」「取れない」は別の値（`*_UNKNOWN` / `*_UNAVAILABLE`）で運ぶ。C# の列挙は
  `Unknown = 0` / `Unavailable = 0` なので、両端で**名前で**写す。UNSPECIFIED は「送り手が状態を書かなかった」＝ REST の本文の状態の欠落と同じ扱い（契約の食い違い）にする。
- 市場・方向は `*_UNSPECIFIED = 0` を持つ列挙、金額は不変文化の 10 進文字列、日付は `yyyy-MM-dd`、スカラーは `optional`。
- 運ぶのは受け手が読む項目だけ（借株可否の観測時刻・日足の期間・処理中の決済の決済方向の建玉と判断 ID は運ばない）。
- message・列挙の名前は送り手の C# の型名と同じにしない（`GetShortPermitResponse`・`DailyBarRow`、状態は `ShortPermitState`・`DailyBarsState`・`InFlightClosesState`）。
  受け手の本番コードに送り手の型名が現れると、送り手の型による契約テストの判定（IADR-0420）が効かなくなる（実測: 当初 `InFlightClosesStatus` と名付けて、取引判断の処理中の決済の契約テストが「無い」と判定された）。

### 決定 3 — 消費側は段 1〜5 と同じ切り方（並走・既定 REST・宣言が BaseUrl より優先）

- 各サービスに `OrderExecutionGrpcTransport`（構成 `OrderExecution:Grpc`）を置き、宣言が無ければ**輸送そのものを登録しない**。宣言してあるのに使えない宛先は起動時に落とす。
- 宣言があれば `OrderExecution:BaseUrl`（バックテストは `Backtest:BarData:OrderExecution:BaseUrl`）より優先し、BaseUrl が空でも読む。
  バックテストは過去データ源の明示（`Backtest:BarData:Provider=order-execution`）が今までどおり要る（allow-list を崩さない）。
- 取引判断の日足は 2 か所が同じ選択（`DailyBarsComposition.Source`）を使う。Stage 0 の基底の「日足の口がある」は `OrderExecution:Grpc` の宣言でも満たす。
- 宣言があれば REST の資源は使わない。バックテストは宣言時に REST の名前付き HttpClient を作らず、gRPC が載せるサービストークンの供給元だけを
  REST と同じ規則で登録する（共有の `AddAiStockTradingServiceTokenProvider`。AI レビューの指摘で是正）。
- deadline の既定は各サービスの REST の `HttpClient.Timeout` と同値（リスク管理 5 秒・取引判断 8 秒・バックテスト 30 秒）。再試行の既定は 1 試行。
- 呼び出しの規則はサービスごとに複製する（リスク管理は本段が初めての呼び出し側なので `RiskManagementGrpcCalls` を足す。`*.Client` は作らない）。

### 決定 4 — 受け手の解釈と失敗の意味は REST と同じ 1 つ

- 受け手は線上の応答を REST と同じ全項目 nullable の受け皿へ写し、REST の `Interpret` を通す（借株可否・日足・処理中の決済）。
  バックテストは銘柄ごとの照会・期間の分割・欠測の作り方（`HttpOrderExecutionHistoricalBarSource.LoadAsync`）も共有する。
- 借株可否: 照会の失敗は「分からない」（許可へ倒さない）。送り手の UNKNOWN は理由つきの「分からない」、NOT_PERMITTED は「借りられない」。
- 日足（取引判断）: 照会の失敗・Available でない・欄の欠けは null（未提供。判断は止めない）。
- 処理中の決済: 照会の失敗・UNKNOWN は null（不明＝LLM を呼ぶ）。NOT_COVERED は「覆わない」。
- 🔴 バックテストの日足は**失敗を送出する側を残す**: REST の源は通信例外・タイムアウトを送出し、非 2xx だけをその銘柄の欠測にする。gRPC も、
  届かない・待ちきれない失敗（`UNAVAILABLE`・`DEADLINE_EXCEEDED`）と呼び出し元のキャンセルは送出し、送り手が答えた失敗（REST の非 2xx に当たる）は欠測にする。
  そのため本サービスの輸送は他サービスと違い null を返さず、最後の試行の `RpcException` を投げる（何へ倒すかはポートが持つ。IADR-0328 決定 5）。

### 決定 5 — 計測用 overlay は 2 本を既定で宣言し、借株可否は別窓にする

- overlay は発注執行の `grpcPort` と、取引判断・バックテストの `grpcClients.OrderExecution: true` を足す（values-local では REST が結線済みで、輸送の切り替えになる）。
- リスク管理の `OrderExecution` は **false**。`OrderExecution__BaseUrl` は values.yaml・values-local のどちらでも空（REST も未結線）で、宣言すると
  借株可否の照会が新しく始まる（空売りの審査の入力が変わる）。#6（報告書 → 監査）と同じく、所有者の同意を得て別の窓で測る（Runbook の #17）。
- helm.yml の期待表は呼び出し側 15・提供側 7 になる。

## 理由

- 段 6 は呼び出し箇所で数えるため、面の無い提供側を残すと段 6 に入れない。面を作るなら、呼び出し元 3 サービスを同じ PR で並走させる方が、
  計測の窓（overlay の 1 行）に入るまでの段数が少ない。
- 検証と解釈を REST と共有すると、並走中に片方だけが直る（ブローカーの枠を使う入力を片方だけが通す・「分からない」を片方だけが許可に読む）形を作らない。
- バックテストの失敗の分け方を他サービスに揃えて null へ畳むと、通信の失敗が「全銘柄の欠測」として評価へ流れ、REST と意味が変わる。

## 結果

- 段 6 の前に残る AST 内の REST だけの east-west の読み取りは 0 本になる（AST→MSP の Retrieval / Document は段の外）。
- 計測の経路表は呼び出し側 17 宣言（overlay の既定は #6・#17 を除く 15）・提供側 7 サービスになる。

## 関連

- Supersedes: なし
- Superseded by: なし
