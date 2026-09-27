---
title: east-west gRPC 段 5（前半）—— Discord ボットの読み取りを gRPC でも呼べるようにし、構成で切り替える（#753）
type: spec
status: done
related_ids: [NFR, NFR-06, FR-14, FR-07, FR-10, FR-13, FR-20, ADR-0047, IADR-0062, IADR-0098, IADR-0284, IADR-0328, IADR-0427, IADR-0446, IADR-0448, IADR-0449]
author: endazon (with Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md (planning#690)
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 仕様書: east-west gRPC 段 5（前半）—— ボットの読み取り（#753）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR-0047 決定 1〜4・フォローアップ 2（段 5 でボットの呼び出しを gRPC へ移し、ボット自身のトークンをメタデータに載せる）・フォローアップ 3（`IADR-0284` の段 5 の行と段 6 の範囲の改訂）。
  隣接クローン `origin/main` の `projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md`（Accepted・2026-09-27）で読んだ。
- `MSP/ADR-0075`（移行順序・一括移行の義務・例外 ADR を起こさない）・`MSP/ADR-0029`（east-west は gRPC）。
- 関連要求: FR-14（Discord の対話）・NFR-06（発注機能へのアクセスは利用者本人のみ）。経路ごとの起点は FR-10（稼働状態）・FR-20（段階ゲート）・FR-07（報告書のレビュー）・FR-13（監視銘柄と入れ替え案）。
- 親 issue: #753（段 2〜6 の受け皿。**本 PR では閉じない**＝`Refs`）。前提: #1067 / PR #1068（gRPC 面の所有者の門・IADR-0448）。
- 実装判断: `IADR-0449`（新設。develop の最大 0448 の次）・`IADR-0284` の `［2026-09-27 追記 / #753］`。

## 目的・背景

ADR-0047 は、ボットの所有者トークンをサービスの身元と分類し（決定 1）、ボットの呼び出しを east-west として gRPC へ移すと裁定した（決定 2）。
門は #1068 で閉じた（決定 3）。本件は段 5 のうち**読み取り**を移す。書き込みは段 5 の後半（別 PR）で移す。

## 段 5 を 2 つの PR に割る理由（実装判断。IADR-0449 決定 1）

- 段 5 の母集合（下表）は 20 の呼び出し箇所で、読み取り 6・書き込み 14 である。読み取りは**そのまま既存の gRPC 面に載るもの（2）と、所有者限定の新しい面が要るもの（4）**、
  書き込みは**すべて新しい面**（書き込みの rpc・`OnBehalfOf` の運び方・冪等と 409 の写し）が要る。1 PR にすると提供側 3 サービス・rpc 20 本・消費側 8 クラスが 1 つの差分に載る。
- `MSP/ADR-0029` の追記「一括対応とする。**作業の分割は妨げない**」・`IADR-0284` 決定 5（段 2 は「1〜2 PR」）の範囲内の分割である。**例外ではなく、段 5 の後半で残りを移す**（ADR-0075 決定 3 を緩めない）。
- 読み取りを先にするのは、失敗が「分からない」で閉じる（状態を変えない）ためである。書き込みは 409・冪等・結果不明の写しが要り、設計の量が違う。

## 母集合（規則 9: 誤りの側の文字列で全ファイルを走査してから挙げる）

走査（`origin/develop` `ee9f803`）:

1. `git grep -n -E "AddHttpClient|AddDiscordOwnerToken" -- backend/Services/NotificationService ':!**/Tests/**'` → 名前付きクライアント 8 つ（`risk-kill-switch`・`risk-pause`・`risk-stage-gate`・`risk-good-faith-violations`・`risk-position-drift`・`report-review`・`report-policy-revision`・`market-monitor-watchlist`）がすべて `AddDiscordOwnerToken`。ほかに `discord-webhook`（第三者 API・射程外）と owner トークン取得用（IdP・射程外）。
2. `git grep -n -E "(GetAsync|PostAsync|PostAsJsonAsync|PutAsync|DeleteAsync|SendAsync)\(" -- backend/Services/NotificationService ':!**/Tests/**'` → 呼び出し箇所を下表に全件挙げた（`SendAsync` の残りは通知の送信口＝ Discord Webhook・内部の抽象で射程外）。
3. BFF の中継（ADR-0047 実測 8 の検算。**他人の数えを転記しない**・規則 10）: `git grep -n 'ProxyAsync(httpFactory, http, HttpMethod' -- backend/Bff ':!**/*Tests*'` → 21 ルート。うちボットの呼び出しと重なるのは `GET /risk-controls/status`・`GET /risk-controls/stage-gate`・`GET /monitor/watchlist` の **3 本**（実測 8 と一致）。

| # | 呼び出し箇所（クラス・メソッド） | ルート | 提供側の認可 | 種別 | 本 PR |
| --- | --- | --- | --- | --- | --- |
| 1 | `HttpPauseController.GetStatusAsync` | `GET /risk-controls/status` | OwnerOnly | 読み | **移す**（新 rpc。BFF も中継） |
| 2 | `HttpStageGateController.GetStatusAsync` | `GET /risk-controls/stage-gate` | OwnerOrService | 読み | **移す**（既存 rpc へ項目を足す。BFF も中継） |
| 3 | `HttpReportReviewController.GetReviewAsync` | `GET /reports/{periodKey}/review` | OwnerOnly | 読み | **移す**（新 rpc） |
| 4 | `HttpReportReviewController.ListPeriodKeysAsync` | `GET /reports/period-keys`（404 のときだけ `GET /reports` へ退避） | OwnerOnly | 読み | **移す**（新 rpc。退避は gRPC 側に持たない。下の「除外」） |
| 5 | `HttpPolicyRevisionController.GetWatchlistProposalAsync` | `GET /reports/policy-revisions/watchlist-proposal` | OwnerOnly | 読み | **移す**（新 rpc） |
| 6 | `HttpMarketMonitorWatchlistController.GetWatchlistAsync` | `GET /monitor/watchlist` | OwnerOrService | 読み | **移す**（既存 rpc のまま。BFF も中継） |
| 7〜8 | `HttpKillSwitchController` engage / disengage | `POST /risk-controls/kill-switch/*` | OwnerOnly | 書き | 段 5 後半 |
| 9〜10 | `HttpPauseController` pause / resume | `POST /risk-controls/pause`・`/resume` | OwnerOnly | 書き | 段 5 後半 |
| 11 | `HttpGoodFaithViolationController` | `POST /risk-controls/good-faith-violations/clear` | OwnerOnly | 書き | 段 5 後半 |
| 12 | `HttpStageGateController.RequestTransitionAsync` | `POST /risk-controls/stage-gate/transition` | OwnerOnly | 書き | 段 5 後半 |
| 13 | `HttpStageGateController.EvaluateWithdrawalAsync` | `POST /risk-controls/stage-gate/withdrawal/evaluate` | OwnerOnly | 書き | 段 5 後半 |
| 14 | `HttpPositionDriftAdoptionController` | `POST /risk-controls/position-drift/adopt` | OwnerOnly | 書き | 段 5 後半 |
| 15〜16 | `HttpReportReviewController` confirm / request-changes | `POST /reports/{periodKey}/confirm`・`/request-changes` | OwnerOnly | 書き | 段 5 後半 |
| 17 | `HttpPolicyRevisionController.ReviseAsync` | `POST /reports/policy-revisions` | OwnerOnly | 書き | 段 5 後半 |
| 18 | `HttpPolicyRevisionController.RecordWatchlistApplyAsync` | `POST /reports/policy-revisions/{attemptId}/watchlist-apply-result` | OwnerOnly | 書き | 段 5 後半 |
| 19 | `HttpMarketMonitorWatchlistController.ApplyProposalAsync` | `POST /monitor/watchlist/proposal-apply` | OwnerOnly | 書き | 段 5 後半 |

- 数え: 読み取り 6 ＋ 書き込み 14 ＝ **20**（ADR-0047 の「呼び出し 20 本」と一致。#4 の退避先 `GET /reports` は #4 の中の配備順の窓の分岐であり、1 本に数えない）。
- 名前付きクライアント 8 つ（ADR-0047 の数えと一致）。

### 除外とその理由

- **#4 の退避（`GET /reports`）は gRPC 側に持たない。** 退避は「通知だけが新しく、報告書に `/period-keys` が無い」配備順の窓のためにある（IADR-0418 決定 3）。
  gRPC の rpc は本 PR で提供側と同時に出るので、同じ窓は「報告書に `ReportOwnerRead` が無い」＝ `UNIMPLEMENTED` になる。退避先の REST の全件照会へは戻らず、候補なし（空）へ倒す
  （REST 側も 500・例外では退避しない＝障害中に重い照会を重ねない、と同じ向き）。REST の実装は変えない。
- **書き込み 14 本**は段 5 の後半（上の「2 つの PR に割る理由」）。本 PR は書き込みを REST のまま委ねる（gRPC 実装は読み取りだけを差し替え、書き込みは REST の実装へ委譲する）。
- 通知の送信（Discord Webhook）・owner トークンの取得（Keycloak）は第三者 API・IdP で射程外（`IADR-0284` 決定 2）。

## 規則 10: この変更で新たに誤りになる自分の記述

- `IADR-0284` の段 5 の行（「OwnerOnly 書き込み 5 本」「`AddDiscordOwnerToken` を同様に `IHttpClientBuilder` へ」）→ 追記で読み方を改める（凍結記録のため本文は書き換えない）。
- `IADR-0284` の段 6 の行（「REST エンドポイントの撤去」）→ ADR-0047 決定 4 に合わせ、消すのは呼び出し元が east-west だけの端点に限り、残す端点を列挙する。
- `risk_controls_read.proto` の `GetStageGate` の注記「本 rpc は現段階だけを運ぶ」→ 項目を足すので是正する。
- `docs/api/east-west-grpc.md` の段の表・構成キーの表 → 通知の行を足す。
- 各 Grpc 面の冒頭コメント「s2s ＋ボット」の呼び出し元の記述（段 2・4 の面）: 段 2・4 の面は「呼び出し元はサービス」と書いている箇所がある → 走査して是正する（下の実装の手順 8）。

## 規則 11（窓）

本件の窓は「通知が gRPC を宣言した時点で、提供側がまだ新しい rpc を持たない」配備順の窓である（逆向きの窓 ＝提供側だけ新しい は、既定が REST なので起きない）。

| 形 | 増える側（提供側が新しい・通知が古い） | 減る側（通知が新しく gRPC を宣言・提供側が古い） |
| --- | --- | --- |
| 後の端だけ（gRPC の宣言は提供側の配備の後に限る＝運用の手順） | 期待どおり（REST のまま） | 手順を守れば起きない。守らなければ `UNIMPLEMENTED` ＝各読み取りの失敗の文言（状態を変えない） |
| 前の端だけ（既定を REST に保つ） | 期待どおり | 同上 |
| 両端（既定 REST ＋未実装は失敗の文言へ閉じる） | 期待どおり | 期待どおり（分からないと返す。空の一覧・古い版を成功と見せない） |

→ **両端**を採る: 既定は REST（宣言しなければ何も変わらない）、宣言したのに提供側が古ければ `UNIMPLEMENTED` を失敗として返す（試験で固定）。

## 設計

### 提供側

1. **門**: 所有者限定の読み取りの gRPC 面のために、ポリシー `GrpcOwnerOnly` を足す: 認証済み ∧ `trading-owner` ∧ `azp` が許可集合（`Auth:GrpcOwnerClients`）。
   REST の `OwnerOnly` と同じく s2s（`trading-service` だけ）は通さない。判定は `GrpcOwnerClientGate.AllowsOwner`（`IsTrustedOwnerClient` を再利用）。
2. **Risk**:
   - 新 service `aistocktrading.riskmanagement.v1.RiskControlsOwnerRead`（`risk_controls_owner_read.proto`）の `GetRiskStatus`。REST の `GET /risk-controls/status` と同じ `RiskStatusService.Build()`。門は `GrpcOwnerOnly`。
   - 既存 `RiskControlsRead.GetStageGate` の応答へ、ボットが読む項目（設定・履歴・昇格評価・撤退評価・Stage 1 の合格条件）を**フィールド追加**で足す（非破壊。既存の読み手は `current_stage` だけを読む）。
3. **Report**: 新 service `aistocktrading.report.v1.ReportOwnerRead`（`report_owner_read.proto`）の `GetReportReview`・`ListReportPeriodKeys`・`GetWatchlistProposal`。REST と同じサービス・同じ検証。門は `GrpcOwnerOnly`。
   - REST の 404 → `NOT_FOUND`、409（その版で確定されていない）→ `FAILED_PRECONDITION`、400（会話キー・版の誤り）→ `INVALID_ARGUMENT`。
4. MarketMonitor は変えない（`WatchlistRead.GetWatchlist` をそのまま使う。門は `GrpcOwnerOrService` でボットが通る）。
5. 線上表現は原則 A（`IADR-0427` 決定 3）: スカラーは `optional`、列挙は `*_UNSPECIFIED = 0` を持ち名前で写す、金額・率は不変文化の 10 進文字列、日付は `yyyy-MM-dd`・時刻は `O`。
   message 名を送り手の C# の型名にしない（`RiskStatusView` → `GetRiskStatusResponse` 等。`IADR-0420` 決定 3 の ⑤）。

### 消費側（NotificationService）

1. 構成キー（既定は REST。宣言したときだけ gRPC）:

   | キー | 既定 | 意味 |
   | --- | --- | --- |
   | `RiskManagement:Grpc` | 未設定＝REST | 稼働状態・段階ゲートの読み取りの宛先 |
   | `Reports:Grpc` | 未設定＝REST | レビュー局面・会話キーの一覧・入れ替え案の読み取りの宛先 |
   | `MarketMonitor:Grpc` | 未設定＝REST | 監視銘柄の読み取りの宛先 |
   | `<上記>:GrpcTimeoutSeconds` | 5 / 5 / 10 | 試行ごとの deadline（各 REST の HttpClient.Timeout と同値。入れ替え案の照会は REST では 90 秒の方針改訂用クライアントを共用していたが、照会そのものは台帳の読み取りなので 5 秒） |
   | `<上記>:GrpcMaxAttempts` | 1 | 試行回数（再試行は `UNAVAILABLE` / `DEADLINE_EXCEEDED` だけ） |

   宣言してあるのに使えない値（相対・`https`・scheme 無し）は起動時に落とす（段 1〜4 と同じ）。
2. **資格情報**: チャネルの `CallCredentials` に**ボットの owner マップ機密クライアントのトークン**（`Notifications:Discord:OwnerAuth`。REST の `AddDiscordOwnerToken` と同じ構成・同じ取得器）を載せる（ADR-0047 決定 2）。
   取得器は DI の `IServiceAccessTokenProvider` として公開しない（s2s と取り違えない。`DiscordOwnerAuthExtensions` の既存の規律）。3 つの輸送で 1 つの取得器（トークンのキャッシュ）を共有する。
   資格情報が未構成なら no-op（メタデータ無し → `UNAUTHENTICATED` → 各読み取りの失敗）＝ REST の「トークン無し → 401」と同じ向き。
3. **差し替え**: 読み取りを持つ 5 つのポート（`IPauseController`・`IStageGateController`・`IReportReviewController`・`IPolicyRevisionController`・`IMarketMonitorWatchlistController`）に `Grpc*` 実装を足す。
   読み取りだけを gRPC で行い、**書き込みは REST の実装へ委ねる**（段 5 後半で差し替える）。輸送が DI に在るときだけ工場が `Grpc*` を選ぶ。
4. **解釈は REST と 1 つ**: 表示の整形（`HttpPauseController.Format`・`HttpStageGateController.FormatStatus` ほか）・会話キーの並び替え・未供給の注記・入れ替え案の検証を `internal static` で共有する。
   gRPC は線上の値を REST と同じ射影（`RiskStatusView` 等の nullable の形）へ写すだけにする。
5. **失敗の写し**: `UNAUTHENTICATED` / `PERMISSION_DENIED` は REST の 401/403 と同じ注記（owner クライアントの設定を確認）、`NOT_FOUND` は REST の 404 と同じ文言、`DEADLINE_EXCEEDED` はタイムアウトの文言、
   ほかは「（gRPC <状態>）」を添えた失敗。会話キーの一覧は REST と同じく失敗をすべて空で返す。**失敗を成功に見せない**（古い版・空の一覧を成功として返さない）。

### 配備

- 既定は REST のまま（helm の既定・values-local・compose に `*__Grpc` を置かない）。gRPC の面は既定配備で開いていない（#1068 の配備の注意）ので、宣言しなければ何も変わらない。
- 提供側の新しい面は既存の `Auth__GrpcOwnerClients__0`（#1068 で 6 面に配線済み）をそのまま使う。**新しい構成キーは提供側に無い。**
- helm の `values.yaml` の notification にコメントで有効化の手順を書く（段 4 と同じ）。配線試験は「既定の描画に通知の `*__Grpc` が無い」ことを固定する。

## 対象範囲

- 変える: proto 2 本の新設と 1 本の追加・shim の認可（`GrpcOwnerOnly`）・Risk / Report の gRPC 面・Notification の輸送と `Grpc*` 実装と Program.cs・helm のコメント・試験・`docs/api/east-west-grpc.md`・テスト仕様書・`scripts/proto-contract-baseline.json`・`IADR-0449`・`IADR-0284` の追記・索引。
- 変えない: REST の面・REST の実装（読み取りの解釈の切り出しはバイト等価）・書き込み・他サービスの呼び出し元・helm / compose の既定の描画。

## 受け入れ基準

- [x] 6 本の読み取りそれぞれで、gRPC 実装が REST 実装と**同じ結果**（成功時の文言・版番号・一覧・入れ替え案・警告）を返す（同じ送り手の値を REST と gRPC の両方で読ませて比べる）
- [x] 失敗（`UNAUTHENTICATED`・`PERMISSION_DENIED`・`NOT_FOUND`・`FAILED_PRECONDITION`・`UNIMPLEMENTED`・`UNAVAILABLE`・deadline 超過・項目の欠落）は成功に見えない（REST の同じ失敗と同じ種類の結果）
- [x] 書き込みは gRPC を宣言しても REST の実装へ委ねられる（偽の REST の提供側が呼ばれる）
- [x] 通知のチャネルはボットの owner トークンを `authorization` に載せる（s2s のトークンではない）。資格情報が未構成ならメタデータを付けない
- [x] 提供側: 新しい面（`RiskControlsOwnerRead`・`ReportOwnerRead`）はボットのトークンで通り、azp の無い所有者・BFF 等の azp・s2s（`trading-service` だけ）は `PERMISSION_DENIED`。REST の面は変わらない
- [x] 提供側: gRPC の値が REST と同じ（本物の Program.cs で REST と gRPC を並べて比べる）。`GetStageGate` の既存の読み手（`current_stage`）は変わらない
- [x] 本番の Program.cs の組み立てで、宣言があれば `Grpc*` が選ばれ実際に偽の提供側を呼ぶ・宣言が無ければ REST・使えない宛先は起動時に落ちる
- [x] helm（values・values-local）・compose の既定の描画に通知の `*__Grpc` が無い（配線試験）
- [x] `IADR-0284` の段 5 の行と段 6 の範囲を改めた（残す REST の端点を列挙）
- [x] 変異 3 件以上で赤
- [x] build・test・format・scripts の検査器が通る

## テスト方針（テスト ID は develop の最大 T-10-1726 の次から）

| ID | 置き場 | 観点 |
| --- | --- | --- |
| T-10-1727 | PlatformShim.Tests | `GrpcOwnerOnly` を本物の登録で評価（ボット可・azp 無し / 他の azp / s2s だけ は不可・`GrpcOwnerOrService` と REST の門は不変） |
| T-10-1728 | RiskManagementService.Tests | `GetRiskStatus`・`GetStageGate` の追加項目が REST と同じ値・門（本物の Program.cs） |
| T-10-1729 | ReportService.Tests | `ReportOwnerRead` の 3 rpc が REST と同じ値・同じ誤りの写し・門（本物の Program.cs） |
| T-10-1730 | NotificationService.Tests | `Grpc*` 実装が REST と同じ結果・失敗の写し・書き込みは REST へ委ねる（偽の提供側を実 Kestrel の h2c で立てる） |
| T-10-1731 | NotificationService.Tests | 本番の Program.cs の組み立て（宣言の有無・使えない宛先・owner トークンがメタデータに載る）と helm / compose の既定の描画 |

## 実装の手順

1. 本仕様書をコミットする（実装より先）。
2. proto（Risk 追加・新 2 本）→ `check-proto-contracts --update`。
3. shim の `GrpcOwnerOnly` と試験。
4. Risk・Report の gRPC 面と試験。
5. Notification の輸送・`Grpc*`・Program.cs と試験。
6. helm のコメントと配線試験。
7. `IADR-0449`・`IADR-0284` 追記・索引・`docs/api/east-west-grpc.md`・テスト仕様書。
8. 規則 10 の走査（「呼び出し元はすべてサービス」等の記述）。

## 検証の結果

（2026-09-27・コミット `ce4d40e` 時点）

- `dotnet build backend/backend.slnx` 警告・エラーの増加なし。`dotnet format backend/backend.slnx --verify-no-changes` exit 0。
- `dotnet test`: IntegrationTests 以外の 21 プロジェクトすべて緑（新規: T-10-1727 12 件・T-10-1728 12 件・T-10-1729 16 件・T-10-1730 20 件・T-10-1731 9 件）。
  IntegrationTests は 35 件中 11 件が失敗し、11 件すべて `DockerUnavailableException`（この環境に Docker が無い。Testcontainers 必須）。
- 検査器: check-trace-blocks / check-proto-contracts（baseline を `--update` で更新・差分は非破壊の追加だけ）/ check-reading-budget / gen-knowledge-graph --check /
  check-test-traceability / check-cross-repo-refs / check-plan-id-qualification / check-doc-links / check-adr-index-sync / check-realm-export /
  check-commit-messages --range=origin/develop..HEAD がすべて OK。`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 477 件 pass。
- 変異 11 件すべて赤（一覧は `docs/tests/FR-10_risk-controls-tests.md` の本件の節）。
- 規則 10 の走査で見つけた既存の食い違い: `docs/api/east-west-grpc.md` の認可の記述 5 箇所（「利用者またはサービス」）が #1067 の門の後も古いままだった → 是正した。
