---
title: IADR-0449 east-west gRPC 段 5（前半）—— Discord ボットの読み取り 6 本を gRPC でも呼べるようにし、所有者限定の読み取りは新しい門の service に分け、ボット自身のトークンを載せて構成で切り替える（書き込みは段 5 の後半）
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-06, FR-14, FR-07, FR-10, FR-13, FR-20, ADR-0041, ADR-0047, MSP:ADR-0029, MSP:ADR-0075, IADR-0062, IADR-0098, IADR-0264, IADR-0284, IADR-0328, IADR-0331, IADR-0418, IADR-0420, IADR-0427, IADR-0446, IADR-0448]
author: endazon (with Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# IADR-0449: east-west gRPC 段 5（前半）—— Discord ボットの読み取り（#753）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-27
- 決定者: Claude Code（実装）／ endazon（裁定は計画 ADR-0047・planning#690）

## 起点・関連

- 計画: ADR-0047 決定 1〜4・フォローアップ 2・3（planning#690 の裁定）。関連要求 FR-14（Discord の対話）・NFR-06。経路ごとの起点は FR-10・FR-20・FR-07・FR-13。
- 関連する実装仕様書: [`.ai-context/specs/20260927_753_grpc-stage5-bot-reads.md`](../specs/20260927_753_grpc-stage5-bot-reads.md)
- 前提: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5（段の切り方）、[IADR-0448](IADR-0448_grpc-owner-gate-requires-bot-azp.md)（gRPC 面の所有者の門）、
  [IADR-0427](IADR-0427_risk-read-grpc-stage2.md)（原則 A・解釈の共有・チャネルの所有）、[IADR-0446](IADR-0446_report-monitor-cost-read-grpc-stage4.md)（段 4 の面）、
  [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)・[IADR-0098](IADR-0098_owner-realm-client.md)（ボットの機密クライアント）
- 関連 issue: #753（段 2〜6 の受け皿・`Refs`）、#1067（前提の門）

## コンテキストと課題

ADR-0047 は、ボットの所有者トークンをサービスの身元と分類し（決定 1）、段 5 でボットの呼び出しを gRPC へ移してボット自身のトークンをメタデータに載せる（決定 2）と裁定した。
門は #1068 で閉じた（決定 3・IADR-0448）。段 5 の母集合（作業仕様書）は呼び出し 20 本で、読み取り 6・書き込み 14 である。読み取り 6 本の提供側の面は次のとおり:

| 読み取り | 提供側の REST の認可 | gRPC の面 |
| --- | --- | --- |
| 稼働状態（`GET /risk-controls/status`） | OwnerOnly | **無い** |
| 段階ゲート（`GET /risk-controls/stage-gate`） | OwnerOrService | 段 2 の `GetStageGate`（現段階だけ） |
| レビュー局面・会話キーの一覧・入れ替え案（報告書 3 本） | OwnerOnly | **無い** |
| 監視銘柄（`GET /monitor/watchlist`） | OwnerOrService | 段 4 の `GetWatchlist` |

## 検討した選択肢

| 論点 | 案 | 評価 |
| --- | --- | --- |
| 段 5 の PR の分け方 | **A（採用）: 読み取り（本 PR）と書き込み（後半）の 2 PR** | 書き込みは 14 本すべてに新しい面（書き込みの rpc・`OnBehalfOf`・冪等と 409・結果不明の写し）が要り、設計の量が違う。`MSP/ADR-0029` の追記「作業の分割は妨げない」の範囲 |
| | B: 20 本を 1 PR | 提供側 3・rpc 20・消費側 8 クラスが 1 差分。監査できる大きさを超える |
| 所有者限定の読み取りの門 | **A（採用）: 新ポリシー `GrpcOwnerOnly`（所有者 ∧ azp がボット）の service に分ける** | REST の OwnerOnly と同じく s2s に開かない。1 service ＝ 1 門（段 4 の「REST の read サブグループと 1 対 1」と同じ） |
| | B: 既存の `RiskControlsRead` に rpc を足し、メソッドに厳しい属性を重ねる | クラスとメソッドの属性の AND に依存し、読み手が門を 1 箇所で読めない |
| | C: `GrpcOwnerOrService` の面に載せる | s2s に開く（REST の OwnerOnly より広い）。最小権限（IADR-0051）に反する |
| 段階ゲートの項目 | **A（採用）: 既存 `GetStageGateResponse` へフィールド追加** | 非破壊。段 2 の読み手は `current_stage` だけを読む |
| 書き込みの扱い | **A（採用）: `Grpc*` 実装は読み取りだけを gRPC で行い、書き込みは REST の実装へ委ねる** | ポートのインターフェイスを割らない（窓口の処理は 1 つのポートを見る）。後半で委譲先を差し替える |
| 資格情報 | **A（採用）: ボットの owner マップ機密クライアントのトークン（REST と同じ構成・同じ取得器の型）** | ADR-0047 決定 2。s2s へは替えない |

## 決定

### 決定 1 — 段 5 を 2 つの PR に割る。本 PR は読み取り 6 本

段 5 の母集合（作業仕様書の表・20 本）のうち**読み取り 6 本**を本 PR で移す。書き込み 14 本は段 5 の後半で移す。**例外ではなく作業の分割である**（ADR-0075 決定 3 の一括移行の義務は緩めない）。

- 会話キーの一覧の REST の退避（`/period-keys` が 404 のときだけ従来の `GET /reports`）は gRPC に持たない。gRPC の rpc は提供側と同時に出るので、同じ窓は `UNIMPLEMENTED` になり、候補なしへ倒す（IADR-0418 決定 3 の「障害中に重い照会を重ねない」と同じ向き）。

### 決定 2 — 所有者限定の読み取りは `GrpcOwnerOnly` の service に分ける

- ポリシー `GrpcOwnerOnly`（shim の `AddAiStockTradingAuth` が登録）: 認証済み ∧ `trading-owner` ∧ `azp` が許可集合（`Auth:GrpcOwnerClients`。IADR-0448 決定 2 と同じ集合）。
  判定は `GrpcOwnerClientGate.AllowsOwner`（`GrpcOwnerOrService` の所有者の分岐と同じ関数。2 箇所に書かない）。**s2s には開かない。**
- 新 service: リスク管理 `RiskControlsOwnerRead`（`GetRiskStatus`）・報告書 `ReportOwnerRead`（`GetReportReview`・`ListReportPeriodKeys`・`GetWatchlistProposal`）。REST と同じサービス・同じ判定を呼ぶ。
  入れ替え案の判定（検証・案の有無・その版で確定されているか）は `WatchlistProposalEndpoints.Lookup` に切り出し、REST と gRPC が共有する（REST の応答はバイト等価）。
- 誤りの写し: 400 → `INVALID_ARGUMENT`、404 → `NOT_FOUND`、409（その版で確定されていない）→ `FAILED_PRECONDITION`。gRPC の空の会話キーは `INVALID_ARGUMENT`（REST の経路引数は空になり得ない）。

### 決定 3 — 線上表現は原則 A

- `GetStageGateResponse` にフィールド 2〜6（設定・履歴・昇格評価・撤退評価・Stage 1 の合格条件）を足した。列挙 `StageTransitionKind`・`StageGateCriterion`・`WithdrawalReason` は
  `*_UNSPECIFIED = 0` を持ち**名前で**写す（C# の廃止済みの序数 1 は線上に置かない）。C# の null（最上段・理由なし・提案なし）は設定しない。
- `GetRiskStatusResponse`: スカラーは `optional`、日次発注の上限・解除日の null は設定しない（0 と書くと「上限 0」＝ #990 の誤り）。
- message 名を送り手の型名にしない（`RiskStatusView` → `GetRiskStatusResponse`、`ReportReviewView` → `GetReportReviewResponse`、`ReportPeriodKeyItem` → `ReportPeriodKeyRow`、
  `WatchlistProposalView` → `GetWatchlistProposalResponse`、`WatchlistChangeView` → `WatchlistChangeRow`、`WatchlistSnapshotEntryView` → `WatchlistSnapshotRow`。IADR-0420 決定 3 の ⑤）。

### 決定 4 — 消費側は構成で切り替え、解釈と文言は REST と 1 つ

| 構成キー | 既定 | 意味 |
| --- | --- | --- |
| `RiskManagement:Grpc` / `Reports:Grpc` / `MarketMonitor:Grpc` | 未設定＝**REST** | 宛先。使えない値（相対・`https`・scheme 無し）は起動時に落とす |
| `<上記>:GrpcTimeoutSeconds` | 5 / 5 / 10 | 試行ごとの deadline（各 REST の HttpClient.Timeout と同値。入れ替え案の照会は REST では方針改訂用の 90 秒のクライアントを共用していたが、台帳の読み取りなので 5 秒） |
| `<上記>:GrpcMaxAttempts` | 1 | 再試行は `UNAVAILABLE` / `DEADLINE_EXCEEDED` だけ |

- **資格情報**: チャネルの `CallCredentials` はボットの owner トークン（`Notifications:Discord:OwnerAuth`）。取得器は包み型 `DiscordOwnerGrpcCredentials` でだけ引け、DI の `IServiceAccessTokenProvider` として公開しない（s2s と取り違えない）。3 つの輸送で 1 つ。未構成なら no-op（メタデータ無し → `UNAUTHENTICATED` → 失敗＝REST の 401 と同じ向き）。
- **差し替え**: 読み取りを持つ 5 ポートに `Grpc*` 実装を足し、読み取りだけを gRPC で行って書き込みは REST の実装へ委ねる。輸送が DI に在るときだけ工場が選ぶ。
- **呼び出しの規則**: 本サービスの `NotificationGrpcCalls`（他の呼び出し元と同じ規則）。ただし失敗を `null` ではなく**状態コードつき**で返す —— ボットは失敗の理由（401/403 の注記・404 の文言・タイムアウト）を利用者へ示すため。
- **解釈の共有**: REST のアダプタから `internal static` を切り出した（`HttpStageGateController.ToStatusResult`、`HttpReportReviewController.ReviewResult` / `OrderPeriodKeys` / `StartOf` / `NotFoundMessage`、
  `HttpPolicyRevisionController.InterpretProposal` / 2 つの文言、`HttpMarketMonitorWatchlistController.InterpretWatchlist`）。REST の判定は変えていない。gRPC は線上の値を REST と同じ射影へ写すだけ（`NotificationGrpcWire`）。
- 🔴 原則 A: 必須の項目の欠落・未指定の段階は既定値で作らず「応答を解釈できない」。表示の補助の列挙（種別・基準・理由・モード）の未知は -1（表示は REST の未知と同じ「不明(-1)」の形）。

### 決定 5 — 配備は既定で REST のまま

- helm の既定・values-local・compose に通知の `*__Grpc` を置かない（配線試験で固定）。gRPC の面は既定の配備で開いていない（#1068 の配備の注意）ので、**宣言しなければ何も変わらない**。
- 提供側の新しい面は、#1068 で 6 面に配線した `Auth__GrpcOwnerClients__0` をそのまま使う。**提供側の新しい構成キーは無い。**
- values.yaml の notification に有効化の手順をコメントで書いた（提供側の `grpcPort` と同じ変更で揃える）。

### 決定 6 — 試験

| ID | 観点 |
| --- | --- |
| T-10-1727 | `GrpcOwnerOnly` を本物の登録で評価（ボット可・s2s だけ・azp の欠落 / 変種 / 複数は不可・既存の門は不変） |
| T-10-1728 | リスク管理の面が REST と同じ値（本物の Program.cs）・門・写しの原則 A・列挙の全値 |
| T-10-1729 | 報告書の面が REST と同じ値・同じ誤りの分類・門（本物の Program.cs） |
| T-10-1730 | `Grpc*` が REST と同じ結果（送り手の本物の値を REST と gRPC の両方で読ませて比べる）・失敗の写し・欠けた項目・deadline・再試行・トークン・書き込みの委譲 |
| T-10-1731 | 本物の Program.cs の組み立て（宣言の有無・部分宣言・使えない宛先・owner トークンの取得と共有）・helm / compose の既定 |

## 理由

- **読み取りを先にしたのは、失敗が「分からない」で閉じる（状態を変えない）からである。** 書き込みの写し（409・冪等・結果不明）は設計の量が違い、同じ差分に載せると監査で見落とす。
- **所有者限定の面を分けたのは、門を 1 箇所で読めるようにするためである。** 同じ service に厳しい rpc を混ぜると、クラスの属性だけを読んだ読み手が「s2s に開いている」と誤読する。
- **既定を REST に据えたのは、gRPC の面が既定の配備で開いていないからである。** 宣言しても提供側が古ければ失敗の文言へ閉じる（成功に見せない）。

## 結果

- 良い影響: ボットの読み取りが east-west として gRPC でも呼べる。人の利用者のトークンは新しい面も通らない。REST の振る舞いは変わらない。
- 悪い影響・トレードオフ:
  - REST のアダプタに `internal static` の解釈が増えた。`Grpc*` は書き込みを REST へ委ねるため、段 5 の後半まで 1 つのポートに 2 つの輸送が混ざる。
  - 実効構成の自己申告（introspection の `AddPortFromBaseUrl`）は BaseUrl しか見ない（段 1〜4 と同じ既存の欠落。段 6 までに輸送に追随させる）。
  - 稼働クラスタでの h2c 往復は未実測。
- フォローアップ:
  1. 段 5 の後半（書き込み 14 本）を #753 から切る。書き込みの面も `GrpcOwnerOnly`（所有者 ∧ ボット）で持ち、`OnBehalfOf` を本文で運ぶ。
  2. 段 6 は `IADR-0284` の 2026-09-27 追記（#753）の範囲で行う。

## 関連

- Supersedes: なし
- Superseded by: なし
