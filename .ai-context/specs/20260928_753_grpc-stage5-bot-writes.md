---
title: east-west gRPC 段 5（後半）—— Discord ボットの書き込みを gRPC でも呼べるようにし、構成で切り替える（#753）
type: spec
status: done
related_ids: [NFR, NFR-06, FR-14, FR-07, FR-09, FR-10, FR-11, FR-13, FR-19, FR-20, ADR-0003, ADR-0028, ADR-0041, ADR-0047, IADR-0062, IADR-0098, IADR-0284, IADR-0427, IADR-0448, IADR-0449, IADR-0450]
author: endazon (with Claude Code)
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md (planning#690)
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md（決定 4）
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 仕様書: east-west gRPC 段 5（後半）—— ボットの書き込み（#753）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR-0047 決定 1〜4・フォローアップ 2（ボットの 20 本を gRPC へ移し、ボット自身のトークンをメタデータに載せ、`OnBehalfOf` は今どおり本文で運ぶ）。
  隣接クローンは shallow（`git rev-parse --is-shallow-repository` ＝ `true`）のため、`git archive origin/main` を作業領域へ展開して読んだ（読み取り専用）。
- 計画 ADR-0041 決定 4（乖離の取り込みの窓口は REST API と Discord Bot の両方）→ 段 6 で `POST /risk-controls/position-drift/adopt` の REST 端点は残す（`IADR-0284` 2026-09-27 追記 #753）。**ボットの呼び出しは east-west なので本件で gRPC へ移す**（端点を残すことと、ボットの呼び出しを移すことは別）。
- `MSP/ADR-0029`（east-west は gRPC・一括対応・作業の分割は妨げない）・`MSP/ADR-0075` 決定 3（一括移行の義務・例外 ADR を起こさない）。
- 基盤の手引き（MSP `docs/api/east-west-grpc.md`）: deadline は「REST 実装の定数をそのまま引く」、冪等とは限らない実行は「リトライ 持たない」の先例がある（MCP ツールの実行の面）。
- 関連要求: FR-14（Discord の対話）・NFR-06。経路ごとの起点は FR-10/FR-11（kill switch・pause・乖離の取り込み）・FR-19（GFV 解除）・FR-20（段階遷移・撤退評価）・FR-07/FR-09（報告書の確定・差し戻し・方針の改訂）・FR-13（監視銘柄の入れ替え案の適用と記録）。
- 親 issue: #753。段 5 の前半は PR #1069（`IADR-0449`）。**本 PR で段 5 は完了し、#753 には段 6 が残る**（`Refs`）。
- 実装判断: `IADR-0450`（新設。develop の最大 0449 の次）。`IADR-0284` への追記は行わない（段 5 の行の改訂は 2026-09-27 追記で済んでおり、本件はその範囲を満たすだけ）。

## 目的・背景

段 5 の前半（#1069）で読み取り 6 本を移した。本件は残りの**書き込み**を移す。書き込みは状態を変えるため、読み取りと違い
「時間切れ＝結果不明」「再試行で二重に適用しうる」「409 / 422 の意味の写し」を決める必要がある。
あわせて PR #1069 の監査の非ブロッキング 3 点を直す。

## 母集合（規則 9: 誤りの側の文字列で全ファイルを走査してから挙げる）

走査（`origin/develop` `b564cb8`）:

1. `git grep -n -E "(GetAsync|PostAsync|PostAsJsonAsync|PutAsync|PutAsJsonAsync|DeleteAsync|PatchAsync|SendAsync)\(" -- backend/Services/NotificationService ':!**/Tests/**'`
   → REST の呼び出しは 8 つの `Http*` アダプタにある（`SendAsync` の残りは通知の送信口＝ Discord Webhook・内部の抽象で射程外）。
2. `git grep -n -E "AddHttpClient|AddDiscordOwnerToken" -- backend/Services/NotificationService ':!**/Tests/**'` → 名前付きクライアント 8 つ（すべて `AddDiscordOwnerToken`）＋ Discord Webhook ＋ owner トークンの取得先（射程外）。
3. 提供側の実装: `git grep -n -E 'Map(Post|Get)\("/(kill-switch|pause|resume|good-faith|stage-gate|position-drift|\{periodKey\}/(confirm|request-changes)|policy-revisions|watchlist/proposal-apply)'`。

| # | 呼び出し箇所（クラス・メソッド） | ルート | 提供側の認可 | REST の上限 | 繰り返したときの提供側の振る舞い | 本 PR |
| --- | --- | --- | --- | --- | --- | --- |
| W1 | `HttpKillSwitchController.EngageAsync` | `POST /risk-controls/kill-switch/engage` | OwnerOnly | 5 秒 | 状態は同じ（起動のまま）。操作の記録は重なり得る | **移す** |
| W2 | `HttpKillSwitchController.DisengageAsync` | `POST /risk-controls/kill-switch/disengage` | OwnerOnly | 5 秒 | 同上 | **移す** |
| W3 | `HttpPauseController.PauseAsync` | `POST /risk-controls/pause` | OwnerOnly | 5 秒 | 冪等（停止中の再 pause は現状態を返す） | **移す** |
| W4 | `HttpPauseController.ResumeAsync` | `POST /risk-controls/resume` | OwnerOnly | 5 秒 | 冪等 | **移す** |
| W5 | `HttpGoodFaithViolationController.ClearAsync` | `POST /risk-controls/good-faith-violations/clear` | OwnerOnly | 5 秒 | 2 回目は 422（解除対象なし）＝**成功したのに失敗に見える** | **移す** |
| W6 | `HttpStageGateController.RequestTransitionAsync` | `POST /risk-controls/stage-gate/transition` | OwnerOnly | 5 秒 | 2 回目は 422（現段階の指定）＝同上 | **移す** |
| W7 | `HttpStageGateController.EvaluateWithdrawalAsync` | `POST /risk-controls/stage-gate/withdrawal/evaluate` | OwnerOnly | 5 秒 | 評価は繰り返せるが、成立時は kill switch を自動起動する（書き込み） | **移す** |
| W8 | `HttpPositionDriftAdoptionController.AdoptAsync` | `POST /risk-controls/position-drift/adopt` | OwnerOnly | 5 秒 | 2 回目は 422（乖離なし）＝二重には取り込まないが失敗に見える | **移す**（REST 端点は段 6 で残す） |
| W9 | `HttpReportReviewController.ConfirmAsync` | `POST /reports/{periodKey}/confirm` | OwnerOnly | 5 秒 | 版番号付きで冪等（200・`transitioned=false`） | **移す** |
| W10 | `HttpReportReviewController.RequestChangesAsync` | `POST /reports/{periodKey}/request-changes` | OwnerOnly | 5 秒 | 2 回目は 409（不正遷移）＝失敗に見える | **移す** |
| W11 | `HttpPolicyRevisionController.ReviseAsync` | `POST /reports/policy-revisions` | OwnerOnly | 90 秒 | 🔴 **冪等でない**（LLM を呼び、新しい版を作り、1 日の回数を消費する） | **移す** |
| W12 | `HttpPolicyRevisionController.RecordWatchlistApplyAsync` | `POST /reports/policy-revisions/{attemptId}/watchlist-apply-result` | OwnerOnly | 90 秒（W11 とクライアント共用） | 1 回だけ記録（2 回目は 409） | **移す** |
| W13 | `HttpMarketMonitorWatchlistController.ApplyProposalAsync` | `POST /monitor/watchlist/proposal-apply` | OwnerOnly | 10 秒 | 2 回目は楽観排他で 409（Stale）＝適用済みなのに「適用していない」に見える | **移す** |

- **数え（規則 10: 他人の数えを転記しない）**: 書き込みの呼び出し箇所は **13**。前半の仕様書・`IADR-0449`・依頼文の「書き込み 14」とは 1 本食い違う。
  前半の表も #7〜#19 の 13 行（kill switch・pause・確定/差し戻しの 3 行が 2 本ずつ）で、「20 本」は読み取り 6 ＋書き込み 13 ＋会話キーの一覧の退避先 `GET /reports`（#4 の中の分岐）で 20 になる数え方である。
  **射程は変わらない**（ボットの REST 呼び出しはすべて本 PR で移る）。記録の是正は `IADR-0450` の数えで行い、凍結記録（前半の仕様書・`IADR-0449`）の本文は書き換えない。
- 名前付きクライアント 8 つのうち、書き込みを持つのは 8 つすべて（`risk-kill-switch`・`risk-pause`・`risk-stage-gate`・`risk-good-faith-violations`・`risk-position-drift`・`report-review`・`report-policy-revision`・`market-monitor-watchlist`）。

### 除外とその理由

- **読み取り 6 本**は前半で移した（本件は触れない。監査の非ブロッキング 3 点だけ直す）。
- **会話キーの一覧の退避先 `GET /reports`** は前半の判断のとおり gRPC に持たない。
- **段階遷移の `approval`（空売り実弾解禁の verdict）**: REST の要求は `approval` を取れるが、ボットは送らない（呼び出し箇所 0）。gRPC の要求に項目を置かない（呼び出し元の無い入力を面に足さない。必要になったらフィールド追加＝非破壊）。
- **REST の端点・REST の `Http*` アダプタは消さない**（段 6。既定は REST のまま）。
- Discord Webhook・owner トークンの取得（第三者 API・IdP）は射程外（`IADR-0284` 決定 2）。

## 規則 10: この変更で新たに誤りになる自分の記述

- `Grpc*` 実装の冒頭コメント「書き込みは REST の実装へ委ねる（段 5 の後半で移す）」（5 クラス）→ 是正する。
- `NotificationGrpcCalls` の冒頭「1 回の照会の規則」「再試行するのは `Unavailable` / `DeadlineExceeded` だけ」→ 書き込みは再試行しない旨を足す。
- `NotificationGrpcTransports.cs` の「読み取りの east-west gRPC の輸送」・`AddNotificationReadGrpc`・`Program.cs` の「書き込みは REST の実装へ委ねる」→ 是正する。
- `docs/api/east-west-grpc.md` §9（「書き込みは REST のまま」）・`deploy/helm/ai-stock-trading/values.yaml` の通知のコメント → 是正する。
- `IADR-0284` 2026-09-27 追記（#753）の「段 5 の後半で移す書き込み」「書き込み 14 本」→ 凍結記録のため本文は変えない。`IADR-0450` に数えの是正を書く。
- 前半の試験（T-10-1730・T-10-1731）の「書き込みは REST へ委ねる」の観点 → 反転する（書き込みも gRPC・REST は呼ばれない）。

## 規則 11（窓）

窓は「通知が gRPC を宣言した時点で、提供側がまだ書き込みの面（新 service）を持たない」配備順の窓である（既定が REST なので、逆向きの窓は起きない）。

| 形 | 増える側（提供側が新しい・通知が古い） | 減る側（通知が新しく宣言・提供側が古い） |
| --- | --- | --- |
| 後の端だけ（宣言は提供側の配備の後に限る＝運用手順） | 期待どおり（通知は前半の実装＝書き込みは REST） | 手順を守らなければ `UNIMPLEMENTED` |
| 前の端だけ（既定を REST に保つ） | 期待どおり | 同上 |
| 両端（既定 REST ＋ `UNIMPLEMENTED` は「実行されていない」失敗として返す＋ REST へ黙って落とさない） | 期待どおり | 期待どおり（`UNIMPLEMENTED` は提供側が処理していない＝状態は変わっていないので「失敗」と返してよい。「結果は不明」にはしない） |

→ **両端**を採る（試験で固定）。🔴 **失敗時に REST へ落とさない**: 落とすと「gRPC の時間切れ（実は適用済み）→ REST で再実行」が二重適用になる（再試行と同じ危険）。

## 設計

### 提供側（Risk・Report・MarketMonitor）

1. 門: 新 service はすべて `GrpcOwnerOnly`（REST の OwnerOnly と同じく s2s に開かない。人の利用者のトークンも通さない）。
   - Risk `RiskControlsOwnerWrite`（`risk_controls_owner_write.proto`）: W1〜W8。
   - Report `ReportOwnerWrite`（`report_owner_write.proto`）: W9〜W12。
   - MarketMonitor `WatchlistOwnerWrite`（`watchlist_owner_write.proto`）: W13。
2. **処理は REST と 1 つ**: 各端点のラムダの本体を `internal static` の処理関数へ切り出し、REST と gRPC の両方がそれを呼ぶ
   （操作者の解決・検証・イベントの発行・台帳への記録を 2 箇所に書かない）。REST の群のフィルタの例外の写し（400 / 409）も関数へ切り出して共有する。
   gRPC は処理関数の結果（`IResult` の状態と値）を gRPC の状態へ写す: 200 → 応答、400 → `INVALID_ARGUMENT`、404 → `NOT_FOUND`、409 → `ABORTED`、
   422 → `FAILED_PRECONDITION`、429 → `RESOURCE_EXHAUSTED`、その他の 5xx → `INTERNAL`。REST の `error` は状態の詳細（detail）に載せる。
   例外: 段階遷移の 422（受理不能）は結果の本文（拒否の理由・合格条件）を持つので、応答（`accepted=false`）で返す。
3. 利用者の文脈（`OnBehalfOf`）は今どおり**本文**（proto の `optional string on_behalf_of`）で運ぶ。信じるのは今どおり信頼クライアント（azp）のトークンに限る（ADR-0047 決定 1）。
4. 線上表現は原則 A。message 名を送り手の C# の型名にしない。

### 消費側（NotificationService）

1. 構成キーは前半と同じ（`RiskManagement:Grpc` / `Reports:Grpc` / `MarketMonitor:Grpc`。既定 REST）。宣言があれば、そのポートの**読み取りと書き込みの両方**が gRPC。
2. **deadline**（試行ごと＝ 1 回）: REST の各クライアントの上限と同値。Risk 5 秒・報告書 5 秒（確定・差し戻し）・市場監視 10 秒は前半の `*:GrpcTimeoutSeconds` をそのまま使う。
   方針の改訂と適用の内訳の記録は REST で 90 秒のクライアントを共用しているので、新キー `Reports:GrpcPolicyRevisionTimeoutSeconds`（既定 90）で持つ。
3. 🔴 **書き込みは再試行しない**（`*:GrpcMaxAttempts` は読み取りだけに効く）。上の表のとおり、繰り返すと「成功したのに失敗に見える」（W5・W6・W10・W12・W13）か
   二重に実行される（W11）。REST も再試行しない。**再試行しないので冪等キーは足さない**（再試行を入れるなら冪等キーが要る＝本件の射程外。`IADR-0450` に条件として残す）。
4. **時間切れの表示**: 書き込みの時間切れは「結果は不明」（REST と同じ文言。W7 の撤退評価だけ REST の文言が「タイムアウトしました」で不明を言っていなかったので、kill switch を自動起動しうる書き込みとして REST と gRPC の両方を「状態は不明」に揃える）。
5. **失敗の写し**（REST の同じ失敗と同じ種類の結果）:
   - 提供側が明確に応答した失敗（`INVALID_ARGUMENT`・`NOT_FOUND`・`ABORTED`・`FAILED_PRECONDITION`・`RESOURCE_EXHAUSTED`・`UNAUTHENTICATED`・`PERMISSION_DENIED`・`UNIMPLEMENTED`・`INTERNAL`・`UNKNOWN`）は REST の非 2xx と同じ扱い（「実行していない」）。
   - 届いたか分からない失敗（`DEADLINE_EXCEEDED`・`UNAVAILABLE`・呼び出し元以外の `CANCELLED`・`DATA_LOSS`）は REST の例外と同じ扱い。REST が例外を「不明」と言う操作（W11 方針の改訂・W13 適用）は gRPC でも「不明」。
   - 応答の必須の項目の欠落は「応答を解釈できない」（書き込みでは「適用された可能性がある」側の文言。成功に見せない）。
6. **REST へ黙って落とさない**: `Grpc*` は REST の実装を持たない（委譲をやめる）。
7. 解釈・文言は REST と 1 つ（`Http*` の `internal static` を共有する）。

### 監査の非ブロッキング（PR #1069）

1. `ReportOwnerReadWireMapping.ToProto(WatchlistProposalView)`: 保存済みの JSON の null 要素で NRE → `INTERNAL` になっていた。null 要素は空の行として運び、受け手の解釈を REST と揃える
   （変更の null ＝案ごと解釈できない、スナップショットの null ＝一覧ごと「分からない」＝成功）。
2. `GrpcReportReviewController.GetReviewAsync` の時間切れの文言「（結果は不明です）」は読み取りには当たらない → 「レビュー局面の照会がタイムアウトしました」にする。REST の読み取りも同じ関数を通っているので揃える。
3. `GrpcMarketMonitorWatchlistController` に `ILogger` を足し、解釈に失敗したとき Warning を出す。

### 配備

- helm の既定・values-local・compose に `*__Grpc` を置かない（既定 REST。前半の配線試験で固定済み）。新キー `Reports__GrpcPolicyRevisionTimeoutSeconds` も置かない。
- 提供側の新しい構成キーは無い（門は #1068 の `Auth__GrpcOwnerClients__0`、代理は既存の `*:DelegatedActor:TrustedClientIds`）。

## 対象範囲

- 変える: proto 3 本の新設・Risk / Report / MarketMonitor の gRPC 面と端点の処理関数の切り出し（REST の応答はバイト等価）・Notification の `Grpc*` と輸送と Program.cs・
  試験・`docs/api/east-west-grpc.md`・テスト仕様書・`scripts/proto-contract-baseline.json`・`IADR-0450`・索引・helm のコメント。
- 変えない: REST の面の応答（W7 の時間切れの文言は受け手の表示だけ）・既定の配備・他サービス。

## 受け入れ基準

- [x] 13 本の書き込みそれぞれで、gRPC 実装が REST 実装と**同じ結果**（成功・拒否・受理不能の文言と真偽）を返す
- [x] 提供側: 新しい面はボットのトークンで通り、azp の無い所有者・BFF 等の azp・s2s は `PERMISSION_DENIED`。本物の Program.cs で REST と同じ状態の変化・同じ操作者（`OnBehalfOf`）の記録・同じ拒否の分類になる
- [x] 書き込みは `*:GrpcMaxAttempts` を宣言しても 1 回しか呼ばない（`UNAVAILABLE` でも再試行しない）
- [x] 失敗時に REST へ落とさない（REST の偽の提供側は呼ばれない）
- [x] 書き込みの時間切れは「結果は不明」、`UNIMPLEMENTED` 等の明確な失敗は「実行していない」
- [x] 本番の Program.cs の組み立てで、宣言があれば書き込みも gRPC（偽の提供側がボットのトークンを受け取る）・無ければ REST
- [x] 監査の 3 点（null 要素・読み取りの時間切れの文言・監視銘柄の Warning）
- [x] 変異 5 件以上で赤
- [x] build・test・format・scripts の検査器が通る

## テスト方針（テスト ID は develop の最大 T-10-1731 の次から）

| ID | 置き場 | 観点 |
| --- | --- | --- |
| T-10-1732 | RiskManagementService.Tests | `RiskControlsOwnerWrite` の 8 rpc が REST と同じ状態の変化・同じ拒否の分類・操作者の代理・門（本物の Program.cs） |
| T-10-1733 | ReportService.Tests | `ReportOwnerWrite` の 4 rpc が REST と同じ・門。入れ替え案の null 要素（監査 1） |
| T-10-1734 | MarketMonitorService.Tests | `WatchlistOwnerWrite/ApplyWatchlistProposal` が REST と同じ・門 |
| T-10-1735 | NotificationService.Tests | `Grpc*` の書き込みが REST と同じ結果・失敗の写し・時間切れは不明・再試行しない・REST へ落とさない・トークン。監査 2・3 |
| T-10-1736 | NotificationService.Tests | 本番の Program.cs の組み立て（書き込みも gRPC・既定 REST・新キー）と既定の配備 |

## 実装の手順

1. 本仕様書をコミットする（実装より先）。
2. proto 3 本 → `check-proto-contracts --update`（削除行 0）。
3. 提供側: 処理関数の切り出し・gRPC 面・試験。
4. 消費側: 輸送・`Grpc*`・Program.cs・試験。
5. 監査の 3 点。
6. `IADR-0450`・索引・`docs/api/east-west-grpc.md`・テスト仕様書・helm のコメント。
7. 検証・変異。

## 検証の結果

（2026-09-28・実装コミット `dfc02a2` 時点）

- `dotnet build backend/backend.slnx` 警告・エラーの増加なし（既存の CS0108 1 件のみ）。`dotnet format backend/backend.slnx --verify-no-changes` exit 0。
- `dotnet test`: IntegrationTests 以外の 21 プロジェクトすべて緑（新規: T-10-1732 16 件・T-10-1733 15 件・T-10-1734 8 件・T-10-1735 23 件・T-10-1736 9 件。
  前半の T-10-1730 は「書き込みは REST へ委ねる」の 1 件を外し、読み取りの時間切れの文言を改めた）。IntegrationTests は Testcontainers（Docker）必須のため実行していない（この環境に Docker が無い）。
- 検査器: check-trace-blocks / check-proto-contracts（`--update`・`git diff --diff-algorithm=patience` で削除行 0。既定の diff の見かけの削除 64 行は同じ内容の行の並びの移動で、旧 baseline のキーと値はすべて残っていることを JSON で突き合わせた）/
  check-reading-budget / gen-knowledge-graph --check / check-test-traceability / check-cross-repo-refs / check-plan-id-qualification / check-doc-links / check-adr-index-sync / check-realm-export /
  check-commit-messages --range=origin/develop..HEAD がすべて OK。`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 477 件 pass。
- 変異 13 件すべて赤（一覧は `docs/tests/FR-10_risk-controls-tests.md` の本件の節）。
- 規則 10 の走査で見つけた既存の食い違い: 前半の仕様書・`IADR-0449`・`IADR-0284` の追記の「書き込み 14」（実数 13）→ `IADR-0450` 決定 1 で是正（凍結記録の本文は変えない）。

### ［2026-09-28 追記 / #753］CI のカバレッジの除外率（G1）の超過と是正

- 症状: PR の CI `build-and-test` が `check-coverage.js --suggest --root cov` の G1 で失敗（除外 27132/65417 行 ＝ 41.48% ＞ 上限 40%）。
  本 PR の proto 3 本で protoc の生成物（`obj/` 配下）の除外が 10136 行になったため。
- 再現: ローカルで CI と同じ手順（Release ビルド・`--filter "Category!=Integration" --collect:"XPlat Code Coverage"`・21 レポートを 1 つの `--results-directory` へ集め `--root` で指す）を実行し、同じ 27132/65417 行で失敗することを確かめた。
- 是正: G1 の割合から `obj/` 配下のビルド出力の除外を別枠にした（分子・分母の両方から外す。カバレッジの分母は不変）。根拠と採らなかった案（上限の引き上げ）は `IADR-0450` の 2026-09-28 追記。
  是正後の同じ集合で G1 30.74%（16996/55281 行）・行カバレッジ 89.92%・floor 83.00% で合格。
- 試験: `scripts/scripts.repo.test.js` に 2 件。変異（別枠を外す）で赤になることを確かめた（PR のコメントに記録する）。

### ［2026-09-28 追記 / #753］別文脈の監査（head `6dfe047`）: GO。非ブロッキング 4 件のうち 2 件を試験で塞いだ

- 監査の発行: gRPC 面の段階遷移・乖離の取り込み・GFV の解除で bus を飛ばす変異が生き残っていた → REST と同じ内容の監査イベントが gRPC からも発行されることを T-10-1732 で固定した（変異で 18 件中 2 件が赤）。
- 信頼一覧が未設定: 試験のホストが全件信頼一覧を持っていたため、代理される利用者を無条件に信じる変異が生き残っていた → 信頼一覧に無いホストで承認者・操作者がトークンの主体になる（REST と同じ）ことを T-10-1732 で固定した（変異で 18 件中 1 件が赤）。
- 受容した 2 件: 段階遷移の `approval` を gRPC で運ばないこと（呼び出し箇所 0。決定 1）、REST と gRPC の失敗の文言の表記差（「HTTP 4xx」と「gRPC <状態>」）。

