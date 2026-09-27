---
title: IADR-0450 east-west gRPC 段 5（後半）—— Discord ボットの書き込み 13 本を gRPC でも呼べるようにし、提供側は REST と同じ処理関数を通し、書き込みは再試行せず時間切れを「結果は不明」と返す
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-06, FR-14, FR-07, FR-09, FR-10, FR-11, FR-13, FR-19, FR-20, ADR-0003, ADR-0028, ADR-0041, ADR-0047, MSP:ADR-0029, MSP:ADR-0075, IADR-0062, IADR-0098, IADR-0264, IADR-0284, IADR-0383, IADR-0420, IADR-0427, IADR-0433, IADR-0448, IADR-0449]
author: endazon (with Claude Code)
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# IADR-0450: east-west gRPC 段 5（後半）—— Discord ボットの書き込み（#753）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-28
- 決定者: Claude Code（実装）／ endazon（裁定は計画 ADR-0047・planning#690）

## 起点・関連

- 計画: ADR-0047 決定 1〜4・フォローアップ 2（ボットの呼び出しを gRPC へ移し、ボット自身のトークンをメタデータに載せ、利用者の文脈は本文の `OnBehalfOf`）。
  ADR-0041 決定 4（乖離の取り込みの窓口は REST API と Discord Bot の両方）。関連要求 FR-14・NFR-06、経路ごとの起点は FR-10/FR-11/FR-19/FR-20/FR-07/FR-09/FR-13。
- 関連する実装仕様書: [`.ai-context/specs/20260928_753_grpc-stage5-bot-writes.md`](../specs/20260928_753_grpc-stage5-bot-writes.md)
- 前提: [IADR-0449](IADR-0449_bot-read-grpc-stage5.md)（段 5 の前半・`GrpcOwnerOnly`・輸送・資格情報）、[IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md) の 2026-09-27 追記（#753。段 5 の行・段 6 の範囲）、
  [IADR-0448](IADR-0448_grpc-owner-gate-requires-bot-azp.md)（門）、[IADR-0383](IADR-0383_stage-transition-delegated-approver.md)（代理の解決）
- 関連 issue: #753（`Refs`。本 PR で段 5 が完了し、段 6 が残る）

## コンテキストと課題

段 5 の前半（IADR-0449）で読み取り 6 本を移し、書き込みは REST のまま残した。書き込みは状態を変えるため、読み取りに無い 3 つの問題がある:

1. **時間切れの意味**: 読み取りの時間切れは「分からない」で閉じるが、書き込みの時間切れは「提供側が処理したか分からない」。REST の各アダプタは既に「状態は不明」「結果は不明」と返していた（撤退評価だけが言っていなかった）。
2. **再試行**: 前半の輸送は `*:GrpcMaxAttempts`（`UNAVAILABLE` / `DEADLINE_EXCEEDED` を再試行）を持つ。書き込みに同じ規則を当てると、時間切れで実は処理済みの要求を 2 回送る。
   提供側の実測（作業仕様書の母集合の表）: 方針の改訂は**冪等でない**（LLM を呼び新しい版を作る・1 日の回数を消費する）。冪等なものでも 2 回目は GFV 解除・段階遷移・乖離の取り込みが受理不能、差し戻しが不正な遷移、入れ替え案の適用が楽観排他で拒否になり、**成功したのに失敗に見える**。
3. **写しの量**: 書き込みは 400 / 404 / 409 / 422 / 429 / 502 と、操作者の解決（`OnBehalfOf`）・監査の発行・KB への保存を持つ。gRPC 面に別に書くと、片方だけが直る。

## 検討した選択肢

| 論点 | 案 | 評価 |
| --- | --- | --- |
| 提供側の処理 | **A（採用）: 端点のラムダの本体を `internal static` の処理関数へ切り出し、REST と gRPC が同じ関数を呼ぶ。gRPC は関数の結果（`IResult` の状態と本文）を gRPC の状態へ写す** | 操作者の解決・検証・監査の発行が 1 つ。REST の応答はバイト等価（ラムダは関数を呼ぶだけ） |
| | B: gRPC 面にサービスの呼び出しを書き直す | 400/409/422 の判定・代理の解決・発行を 2 箇所に書く（片方だけ直る） |
| | C: 処理関数が型付きの結果を返すよう作り替える | REST の応答の組み立ても変わる（バイト等価の確認が重い）。本件の射程を超える |
| 書き込みの再試行 | **A（採用）: 書き込みは構成に関わらず再試行しない（冪等キーも足さない）** | REST と同じ振る舞い。二重実行と「成功したのに失敗に見える」を作らない。基盤の手引き（MCP ツールの実行の面「リトライ 持たない・冪等とは限らない」）と同じ |
| | B: 冪等なものだけ再試行する | 冪等でも 2 回目は受理不能になり、利用者に誤った失敗を見せる |
| | C: 冪等キーを足して再試行する | 提供側 3 サービスに冪等キーの記録（保存・期限）を足す設計になり、REST にも無い。射程外 |
| 失敗時の REST への退避 | **A（採用）: 退避しない（`Grpc*` は REST の実装を持たない）** | 時間切れで実は処理済みの要求を REST で再実行すると、再試行と同じ二重実行になる |
| deadline | **A（採用）: REST の各クライアントの上限と同値**（Risk 5 秒・報告書 5 秒・方針の改訂と適用の内訳の記録 90 秒・市場監視 10 秒） | 基盤の手引き「REST 実装の定数をそのまま引く」。方針の改訂は LLM を待つので 90 秒（REST で 2 つが共用するクライアントと同じ） |

## 決定

### 決定 1 — 段 5 の後半は書き込み 13 本。数えを是正する

作業仕様書の母集合（`git grep` で引き直した）: kill switch 2・一時停止/再開 2・GFV 解除・段階遷移・撤退評価・乖離の取り込み・報告書の確定・差し戻し・方針の改訂・適用の内訳の記録・入れ替え案の適用 ＝ **13 本**。
IADR-0449・前半の仕様書・`IADR-0284` の 2026-09-27 追記（#753）の「書き込み 14」は、呼び出し 20 本（計画 ADR-0047 の数え）から読み取り 6 を引いた値であり、20 本は会話キーの一覧の退避先 `GET /reports` を 1 本に数えている（読み取り 7 ＋書き込み 13）。
**射程は変わらない**（ボットの REST の呼び出しはすべて移る）。凍結記録の本文は書き換えず、ここで是正する。

- 乖離の取り込みはボットの呼び出しを移す。REST の端点は ADR-0041 決定 4 の人の窓口として段 6 でも残す（`IADR-0284` の 2026-09-27 追記の表どおり。端点を残すことと、ボットの呼び出しを移すことは別）。
- 段階遷移の `approval`（空売り実弾解禁の verdict）は gRPC の要求に置かない（ボットは送らない＝呼び出し箇所 0）。必要になればフィールド追加（非破壊）。

### 決定 2 — 提供側は REST と同じ処理関数を通す

- 新 service（門はすべて `GrpcOwnerOnly`＝所有者 ∧ azp がボット・s2s に開かない）:
  - Risk `RiskControlsOwnerWrite`（`risk_controls_owner_write.proto`）: `EngageKillSwitch`・`DisengageKillSwitch`・`PauseTrading`・`ResumeTrading`・`ClearGoodFaithViolations`・`RequestStageTransition`・`EvaluateWithdrawal`・`AdoptPositionDrift`
  - Report `ReportOwnerWrite`（`report_owner_write.proto`）: `ConfirmReport`・`RequestReportChanges`・`RevisePolicy`・`RecordWatchlistApplyResult`
  - MarketMonitor `WatchlistOwnerWrite`（`watchlist_owner_write.proto`）: `ApplyWatchlistProposal`
- 各端点のラムダの本体を `<操作>Endpoint.Handle*` へ切り出した（ラムダは関数を呼ぶだけ＝REST の応答はバイト等価）。GFV 解除と適用の内訳の記録の匿名型の応答には名前を付けた（JSON は同じ）。
  各登録表の群のフィルタの例外の写し（`ArgumentException` → 400 ほか）も `MapException` に切り出し、フィルタと gRPC 面が共有する。
- 写し: 200 → 応答、400 → `INVALID_ARGUMENT`、404 → `NOT_FOUND`、409 → `ABORTED`、422 → `FAILED_PRECONDITION`、429 → `RESOURCE_EXHAUSTED`、その他 → `INTERNAL`。REST の `error` は状態の詳細（detail）に載せる。
  **段階遷移の 422（受理不能）だけは本文（拒否の理由・合格条件）を持つので応答（`accepted=false`）で返す。**
- 写しはサービスごとに持つ（`RiskWriteGrpcReplies`・`ReportWriteGrpcReplies`・市場監視は面の中）。群のフィルタが写す例外の種類がサービスごとに違い、`*.Client` を廃した裁定（IADR-0264 決定 1）と同じくサービスを跨いで共通化しない。
- 利用者の文脈は本文の `optional string on_behalf_of`（null と空を区別する）。信じるのは今どおり信頼クライアント（`*:DelegatedActor:TrustedClientIds`）のトークンに限る（ADR-0047 決定 1）。

### 決定 3 — 線上表現は原則 A

- スカラーは `optional`、列挙は `*_UNSPECIFIED = 0` を持ち名前で写す（段階・市場）。未指定は REST の項目の省略（null）と同じ＝ 400。日本（C# の 0）を未指定に化けさせない。
- 「無い」と「空」を分ける入れ物: 方針の改訂の `current_watchlist`（照会できなかった）、入れ替え案の適用の `expected_watchlist`・`changes`。
- message 名を送り手の C# の型名にしない（`KillSwitchRequest` → `KillSwitchChangeRequest`、`ConfirmReportRequest` → `ReportConfirmationRequest`、`WatchlistProposalApplyRequest` → `WatchlistProposalApplicationRequest` ほか。IADR-0420 決定 3 の ⑤）。

### 決定 4 — 消費側: 書き込みは再試行しない・時間切れは「結果は不明」・REST へ落とさない

- 構成キーは前半と同じ（`RiskManagement:Grpc` / `Reports:Grpc` / `MarketMonitor:Grpc`。既定 REST）。**宣言したポートは読み取りも書き込みも gRPC**（`Grpc*` は REST の実装を持たない）。
- `NotificationGrpcCalls.CallOnceAsync`: 書き込みの 1 回の呼び出し。**`*:GrpcMaxAttempts` に関わらず再試行しない**（`*:GrpcMaxAttempts` は読み取りだけに効く）。失敗は状態と提供側の詳細を返す。
  🔴 **再試行を入れるなら冪等キーが要る**（提供側で同じキーの 2 回目を 1 回目の結果で返す記録）。本件では入れない（REST も持たない）。
- deadline: Risk 5 秒・報告書 5 秒・市場監視 10 秒（前半の `*:GrpcTimeoutSeconds`）。方針の改訂と適用の内訳の記録は新キー `Reports:GrpcPolicyRevisionTimeoutSeconds`（既定 90）。
- 失敗の写し（REST の同じ失敗と同じ種類の結果）:

  | gRPC | REST の対応 | ボットの結果 |
  | --- | --- | --- |
  | `DEADLINE_EXCEEDED` | タイムアウト | **「状態は不明」「結果は不明」**（REST と同じ文言。撤退評価は kill switch を自動起動しうる書き込みなので REST も含めて「状態は不明」に揃えた） |
  | `UNAVAILABLE` ほか届いたか分からない失敗 | 例外 | REST が例外を「不明」と言う操作（方針の改訂・入れ替え案の適用）は不明。ほかは REST と同じ「失敗しました（…）」 |
  | `INVALID_ARGUMENT`・`NOT_FOUND`・`ABORTED`・`FAILED_PRECONDITION`・`RESOURCE_EXHAUSTED`・`INTERNAL` | 非 2xx | REST と同じ（受理不能・版の不一致・拒否。提供側の説明をそのまま見せる） |
  | `UNAUTHENTICATED`・`PERMISSION_DENIED`・`UNIMPLEMENTED` | 401/403・（配備順の窓） | 失敗＝**実行していない**（「不明」にしない）。資格情報の失敗には REST と同じ注記 |

- 応答の必須の項目の欠落は「応答を解釈できない」（書き込みでは「適用・保存された可能性がある」側の文言。確定の `transitioned` / `version` の欠落は REST の「旧版の窓」と違い gRPC では起きないので、確定したと騙らない）。
- 解釈と文言は REST と 1 つ（`Http*` の `internal static`: `Cleared`・`ToTransitionResult`・`FormatWithdrawal`・`FormatAdopted`・`InterpretConfirmed`・`InterpretRevision`・`Rejected`・`InterpretApplied`・`Stale` と各文言）。
- PR #1069 の監査: 読み取り（レビュー局面の照会）の時間切れの文言から「結果は不明」を外した（REST も同じ関数）。監視銘柄の gRPC の読み取りに Warning ログを足した。
  提供側の入れ替え案の写しは、保存済みの JSON の null 要素を空の行として運ぶ（NRE → `INTERNAL` にしない。受け手は REST と同じに読む）。

### 決定 5 — 配備は既定で REST のまま

- helm の既定・values-local・compose に `*__Grpc` も `Reports__GrpcPolicyRevisionTimeoutSeconds` も置かない（配線試験で固定）。提供側の新しい構成キーは無い（門は #1068 の `Auth__GrpcOwnerClients__0`、代理は既存の `*:DelegatedActor:TrustedClientIds`）。

### 決定 6 — 試験

| ID | 観点 |
| --- | --- |
| T-10-1732 | リスク管理の 8 rpc が REST と同じ状態の変化・同じ操作者（トークンの名前・代理）・同じ拒否の分類と文言・門（本物の Program.cs） |
| T-10-1733 | 報告書の 4 rpc が REST と同じ・門・入れ替え案の null 要素 |
| T-10-1734 | 入れ替え案の適用が REST と同じ内訳・変更者・拒否・門 |
| T-10-1735 | `Grpc*` の書き込みが REST と同じ結果（送り手の本物の値）・受理不能・時間切れは不明・再試行しない・明確な失敗と不明の区別・欠けた項目・本文の `on_behalf_of`・監視銘柄の Warning |
| T-10-1736 | 本物の Program.cs の組み立て（書き込みも gRPC・失敗しても REST へ落とさない・既定は REST・方針の改訂の deadline）と既定の配備 |

## 理由

- **処理関数を共有するのは、書き込みの判定（代理の解決・値域・監査の発行）が統制そのものだからである。** 2 箇所に書くと、片方だけ直ったときに gRPC からだけ統制が緩む（例: 承認者を特定できない段階遷移が gRPC では通る）。
- **再試行しないのは、書き込みの 2 回目が「成功したのに失敗」か「二重実行」になるからである。** REST も再試行しない。冪等キーは REST にも無い仕組みで、足すなら提供側の記録の設計が要る。
- **REST へ落とさないのは、落とすことが再試行と同じだからである。**
- **時間切れを「不明」と返すのは、書き込みでは正しいからである**（失敗と言えば利用者が再実行し、成功と言えば確かめない）。

## 結果

- 良い影響: ボットの east-west の呼び出し（読み取り 6・書き込み 13）がすべて gRPC でも呼べる。段 5 が完了し、段 6（REST の端点・`Http*` アダプタ・名前付きクライアントの退役）へ進める。REST の応答は変わらない。
- 悪い影響・トレードオフ:
  - 提供側の gRPC 面は `IResult` を読み戻して写す（処理関数を型付きの結果にすれば不要だが、REST の応答の組み立てが変わる）。段 6 で REST の端点を消すときに型付きへ寄せられる。
  - `UNAVAILABLE` の書き込みは、REST の例外と同じく多くの操作で「失敗しました」と言う（接続が切れる前に処理された場合を区別できない。REST と同じ残余）。
  - 稼働クラスタでの h2c 往復は未実測（前半と同じ）。
- フォローアップ:
  1. 段 6 を #753 から切る（`IADR-0284` の 2026-09-27 追記の範囲。BFF・人の窓口の母集合を着手時に引き直す）。
  2. 段 6 で `Http*` アダプタを退役させるとき、`Http*` の `internal static` に置いた解釈と文言を `Grpc*` 側へ移す。

## 関連

- Supersedes: なし
- Superseded by: なし
