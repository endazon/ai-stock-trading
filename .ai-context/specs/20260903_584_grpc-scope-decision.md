---
title: east-west 同期照会の gRPC 化（platform ADR-0029）の射程確定と判断（#584・判断フェーズ）
type: spec
status: done
issue: "#584"
related_ids:
  - FR-17
  - NFR
  - MSP:ADR-0029
  - ADR-0001
  - IADR-0001
  - IADR-0051
  - IADR-0063
  - IADR-0259
  - IADR-0264
  - IADR-0284
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 作業仕様書: east-west 同期照会の gRPC 化（platform ADR-0029）の射程確定と判断（#584）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。
> **本作業は判断フェーズであり、コードを 1 行も変更しない**（実装は判断確定後の別 PR）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-17（設定管理＝全体前提条件。#584 が名指しする `GET /assumptions` の照会元）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: **`MSP/ADR-0029`**（同期通信の使い分け基準・内部サービス間 gRPC／BFF・外部 REST。planning
  `projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md`。2026-08-04 追記を含む）、
  `ADR-0001`（基盤再利用。AST は基盤の拡張であり基盤の標準に揃える）、`IADR-0001`（リポ構成・規約を基盤に揃える）、
  `IADR-0051`（s2s 認証）、`IADR-0063`（前提条件の同期照会・fail-safe）、`IADR-0259` 決定 9（gRPC 化の切り出し）、
  `IADR-0264` 決定 1（`.Client` 廃止）
- 計画書リンク: `https://github.com/endazon/project-planning/blob/main/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md`

> 🔴 **ID の名前空間に注意。** #584 本文の「計画 `ADR-0029`」は **基盤（microservices-platform）側の計画 ADR** である。
> 本リポジトリの裸の `ADR-0029` は資料再編（`.ai-context/` 分離）であり別物。本書・IADR では `MSP/ADR-0029` と修飾する。
> オーケストレータ指示のコミット件名 `docs(ADR-0029,FR-17)` はスコープが本リポの ADR-0029（資料再編）へ解決されるため、
> 規約（`.claude/rules/traceability.md`「スコープには自プロジェクトが所有する ID を置く」）に従い `docs(FR-17)` とし、
> 説明文で `MSP/ADR-0029` を引く。

## 目的・背景

#526 の 3 スコープのうち「同期呼び出しを gRPC 生成クライアントへ寄せる」だけが #584 へ積み残された。
`IADR-0259` 決定 9 は「gRPC 化は行わない（トランスポートの変更＝振る舞いの変更）。gRPC 化のみ別 issue へ切り出す」と
定め、#584 は「`MSP/ADR-0029` に照らして gRPC 化／REST 継続を判断する。**REST 継続なら根拠を IADR に残して閉じてよい**」と書く。

しかし `MSP/ADR-0029` の 2026-08-04 追記（planning#180 の裁定）は

- 「サービス公開クライアント（`*.Client`）は作らない。呼び出し側は gRPC 生成クライアントを用いる」
- 「**既存の REST による east-west 同期呼び出しは、本 ADR の基準に該当するものをすべて gRPC へ移行する**（一括対応。作業の分割は妨げない）」
- 「例外を設ける場合は本 ADR を改定せず、対象経路を明記した新 ADR を起票する」

と明記しており、**#584 の「REST 継続で閉じてよい」は計画と正面衝突する**。さらに #584 の射程（Assumptions 2 本）は
ADR の射程（east-west すべて）より狭い。本作業は (1) 射程を実測で確定し、(2) 判断案を比較して推奨を 1 つ選び、
(3) 衝突を計画へ環流し、(4) 判断（暫定）を IADR に残す。

## 対象範囲

- 対象: 射程の実測表（下記）、判断案 A/B/B′/C の比較と推奨、planning への環流 issue、`IADR-0284`、README 索引、`docs/blocked-tasks.md` B-4 の裁定待ち行、#584 へのコメントと `blocked:decision` ラベル
- 対象外: コードの変更（`.proto`・CPM・Kestrel・Helm・クライアントの書き換え）、`Http.Resilience` / `HybridCache` への置換、
  結合テストの追加（いずれも #584 に残す）

## 実測（2026-09-03・develop `7c110ae9`）

### 現状の事実

| 事実 | 実測 |
| --- | --- |
| `.proto` | 本リポ 0 件・MSP 0 件 |
| `Grpc*` / `Google.Protobuf` | 本リポ `Directory.Packages.props` 0 件。MSP は `src/Directory.Packages.props` に `Grpc.AspNetCore` / `Grpc.Net.Client` / `Grpc.Tools` 2.83.0 の**バージョン宣言のみ**（参照する `.csproj` 0 件・`MapGrpcService` / `AddGrpcClient` 0 件） |
| MSP の gRPC 先例 | **無い**。MSP#441（Wolverine ＋ gRPC/REST 使い分けの再実装 issue）は「`.proto` 0 件・`Grpc.*` を参照する `.csproj` 0 件 → ADR-0029 の内部 gRPC は完全未着手」と実測して**クローズ**。`MSP:IADR-0122` は「`.proto` 0 件のため proto を正本にできない。east-west が gRPC へ移行した時点で切り替える」と繰延 |
| ADR-0029 のフォローアップ | 「proto 契約の配置（共有契約プロジェクト）と versioning 規約を実装ガイドへ落とす」＝**未履行**（計画・MSP のどちらにも無い） |
| Kestrel の HTTP/2 | 本リポ `Http2` / `Protocols` の設定 0 件。Helm は全 Worker が `containerPort: 8080` の 1 ポート。平文 HTTP/2（h2c）は ALPN が無いため `Http1AndHttp2` では HTTP/1.1 に落ち、**gRPC 用に Http2 専用ポートを別に開ける必要がある** |
| s2s トークン | `AddAiStockTradingServiceToken(this IHttpClientBuilder, …)` は `DelegatingHandler`（`ServiceTokenHandler`）。`AddGrpcClient<T>()` も `IHttpClientBuilder` を返すため**同じ拡張で等価配線できる**（IADR-0051 の機構は再利用可） |
| 認可ポリシー | 提供側の最小 API は `Policies.OwnerOnly` 10 箇所・`Policies.OwnerOrService` 9 箇所。gRPC 化ではサービス／メソッドの `[Authorize(Policy=…)]` へ写す |
| 結合テスト | `backend/Tests/AiStockTrading.IntegrationTests`（Testcontainers＝Docker）。s2s 同期照会の E2E は `ServiceTokenSyncQueryE2ETests` 1 本（Risk・Report の読み取り系） |

### 母集合の引き方（規則 5・6: 軸を 3 本、除外と理由を書く）

| 軸 | 検索 | 件数 |
| --- | --- | --- |
| 1 | `backend/**/Infrastructure/ExternalServices/Http*.cs`（テスト除外） | **24** |
| 2 | `AddHttpClient(` の登録（`backend/Services` `backend/Shared`・テスト除外） | 29 登録（名前付き 27 ＋ 無名 2） |
| 3 | `ExternalServices` 以外で `HttpClient` / `IHttpClientFactory` を持つ型 | 13 ファイル |

軸 2・3 で軸 1 に無かったもの（除外とその理由）:

| 発見 | 扱い | 理由 |
| --- | --- | --- |
| `Shared.KnowledgeBase` の `HttpKnowledgeBaseWriter`（MSP DocumentService `POST /documents`）・`HttpKnowledgeBaseSearch`（MSP RetrievalService `POST /search`） | **射程へ追加（＋2）**。ただし「基盤待ち」 | AST→MSP の east-west 同期呼び出し。proto の所有者は呼び出される側＝MSP |
| `FinnhubQuoteClient` / `MarketDataSourceFactory`（`marketdata` ×4）・Backtest の Stooq（`BarDataHttpClientName`）・`fx`（FRED）・Discord Webhook・Keycloak トークン取得（`TokenClientName` ×2） | **除外** | 第三者 API・IdP への外向き呼び出し。`MSP/ADR-0029` の east-west（サービス間）にも north-south（BFF→SPA・外部公開）にも当たらない |

### 射程表（24 本 ＋ 軸 2 で見つかった 2 本）

判定基準は `MSP/ADR-0029` §決定のとおり「**境界で機械的に決まる**」——(i) 同期呼び出しである、(ii) east-west（サービス間・BFF→サービス）である、
(iii) proto は呼び出される側が所有する。頻度・レイテンシ・ペイロードは**基準ではなく ADR の理由**であり、判定には使わない（使うと ADR が排除した「経路ごとの裁量判断」になる）。

| # | 呼び出し元 | クライアント | 呼び出し先（所有者）・エンドポイント | 頻度（実測の根拠） | fail-safe（取得不可時） | 判定 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | ReportService | `HttpPeriodFillSource` | Risk `GET /risk-controls/fills?from&to` | 報告書生成ごと（日／週／月） | `[]` | **対象** |
| 2 | ReportService | `HttpBuyInInferenceRecordSource` | Risk `GET /risk-controls/buy-in-inferences` | 同上 | `null`（未供給） | **対象** |
| 3 | ReportService | `HttpOpenPositionSource` | Risk `GET /risk-controls/open-positions` | 同上 | `null` | **対象** |
| 4 | ReportService | `HttpOpenDUptimeSource` | Risk `GET /risk-controls/session-uptime` | 同上 | `null` | **対象** |
| 5 | ReportService | `HttpStageProgressSource` | Risk `GET /risk-controls/stage-gate` | 同上 | `null` | **対象** |
| 6 | ReportService | `HttpFxSourceStatusSource` | Audit `GET /audit/events/by-type` | 同上 | `null` | **対象** |
| 7 | ReportService | `HttpLlmUsageRecordSource` | Audit `GET /audit/events/by-type` | 同上 | `null` | **対象** |
| 8 | ReportService | `HttpBorrowFeeRecordSource` | Audit `GET /audit/events/by-type` | 同上 | `null` | **対象** |
| 9 | ReportService | `HttpTradeRationaleSource` | Audit `GET /audit/events/by-type` | 同上 | `null` | **対象** |
| 10 | ReportService | `HttpReportNarrativeDrafter` | **MSP** LlmGateway `POST /complete` | 報告書生成ごと | プレースホルダ散文 | east-west だが**基盤待ち**（proto 所有者＝MSP・MSP に proto 0 件。IADR-0061: `/complete` は匿名） |
| 11 | TradeDecisionService | `HttpAssumptionsClient` | Configuration `GET /assumptions` | 判断サイクルごと（TTL 5 分キャッシュ越し） | last known good → 既定値（IADR-0063 決定 5） | **対象**（#584 名指し） |
| 12 | TradeDecisionService | `HttpDailyPolicyProvider` | Report `GET /reports/daily-policy` | 判断サイクルごと | `null`（方針未確定＝スキップ） | **対象** |
| 13 | TradeDecisionService | `HttpHeldPositionProvider` | Risk `GET /risk-controls/open-positions` | 判断サイクルごと | `null` | **対象** |
| 14 | TradeDecisionService | `HttpSizingContextProvider` | Risk `GET /risk-controls/sizing-context` | 判断サイクルごと | 残枠 0 の安全既定 | **対象** |
| 15 | TradeDecisionService | `HttpWatchlistProvider` | MarketMonitor `GET /monitor/watchlist` | 判断サイクルごと | 構成ベース watchlist へ委譲 | **対象** |
| 16 | TradeDecisionService | `HttpLlmCompletionClient` | **MSP** LlmGateway `POST /complete` | 銘柄ごと・サイクルごと | `HoldFallback` | **基盤待ち**（行 10 と同じ） |
| 17 | NotificationService | `HttpKillSwitchController` | Risk `POST /risk-controls/kill-switch/{engage,disengage}`（OwnerOnly） | Discord コマンド（人手） | `Succeeded=false` | **対象**（owner マップ機密クライアントのトークン。IADR-0062 決定 4） |
| 18 | NotificationService | `HttpPauseController` | Risk `POST /risk-controls/{pause,resume}`・`GET /risk-controls/status` | 人手 | 同上 | **対象** |
| 19 | NotificationService | `HttpStageGateController` | Risk `GET /risk-controls/stage-gate`・`POST …/transition`・`POST …/withdrawal/evaluate` | 人手 | 同上 | **対象** |
| 20 | NotificationService | `HttpGoodFaithViolationController` | Risk `POST /risk-controls/good-faith-violations/clear` | 人手 | 同上 | **対象** |
| 21 | NotificationService | `HttpReportReviewController` | Report `GET /reports/{key}/review`・`POST …/confirm`・`POST …/request-changes` | 人手 | 同上 | **対象** |
| 22 | MarketMonitorService | `HttpPositionStore` | Risk `GET /risk-controls/open-positions` | 監視巡回ごと（既定 60 秒＝射程内で最高頻度） | 空列（損切り検知対象なし） | **対象** |
| 23 | InformationCollectionService | `HttpCostControlGate` | CostControl `GET /costs/state` | 収集巡回ごと | `Normal`（停止せず） | **対象** |
| 24 | CostControlService | `HttpAssumptionsClient` | Configuration `GET /assumptions` | 費用上限の解決ごと（TTL 5 分キャッシュ越し・`AssumptionsChanged` で即時失効） | last known good → 既定値 | **対象**（#584 名指し） |
| 25 | 各サービス（`Shared.KnowledgeBase`） | `HttpKnowledgeBaseWriter` | **MSP** DocumentService `POST /documents` | 報告書・判断根拠の保存ごと | no-op（既定オフ） | **基盤待ち** |
| 26 | 各サービス（`Shared.KnowledgeBase`） | `HttpKnowledgeBaseSearch` | **MSP** RetrievalService `POST /search` | 判断サイクルごと（既定オフ） | 文脈なしへ縮退 | **基盤待ち** |

集計: **AST 内 22 本（提供側 6 サービス＝Risk 14 エンドポイント・Audit 1・Configuration 1・Report 4・MarketMonitor 1・CostControl 1）＝ADR の基準に該当し AST が proto を所有できる**／
**AST→MSP 4 本＝基準に該当するが proto の所有者は MSP であり、MSP に proto が 0 件のため AST 単独では移行できない**／基準外（REST 残置の根拠が立つもの）**0 本**。

> 🔴 **「REST 残置」の根拠は 26 本のどれにも立たない。** 全 26 本が同期・east-west であり、ADR-0029 は頻度や fail-safe の有無を
> 判定に使わない。頻度（人手・分単位）と fail-safe（全 24 本が例外を外へ出さない）は「ADR-0029 の**理由**（低レイテンシ）が AST の
> 呼び出しプロファイルには当たらない」という**計画側への事実の報告**であって、実装側が例外を自認する根拠にはならない
> （例外は「対象経路を明記した新 ADR」＝計画側の裁定）。

## 設計（判断案の比較）

| 案 | 内容 | 工数（概算） | 退行リスク | 計画整合 |
| --- | --- | --- | --- | --- |
| **A** | ADR-0029 どおり AST 内 22 本を一括 gRPC 化（段階分割） | 土台（CPM 4 パッケージ・Kestrel h2c 第 2 ポート・Helm Service/Deployment・E2E ハーネス・proto 互換検査の新設）＋提供側 6 サービスの gRPC サービス実装（21 エンドポイント）＋消費側 22 本の書き換え＋単体テスト 27 ファイル（`HttpMessageHandler` スタブ→gRPC テストサーバ）＋REST 並走→撤去。**6〜8 PR** | fail-safe の写像ミスは**統制が緩む向き**（行 14 の残枠 0・行 22 の空列・行 23 の Normal はいずれも「取得不可＝安全側」の意味を `RpcException.StatusCode` で再現する必要がある）。認可ポリシー 19 箇所の写し忘れ。h2c 配備の運用未経験 | ADR-0029 逐語に合致。**ただし基盤に現物が無く、AST が proto 配置・versioning・h2c・認可の慣行を先に決めることになる**（`IADR-0001` / `IADR-0259` の「揃える先の現物を見て決める」と逆転） |
| **B** | REST 継続を「対象経路を明記した新 ADR」として計画へ求める | 環流 1 件＋裁定後に IADR 改定 | なし | ADR-0029 の例外規定の形式は満たす。ただし根拠（頻度・fail-safe）は ADR-0029 が「経路ごとの裁量判断と構成の揺れを排除する」ために**採らなかった軸**であり、通るかは計画側の判断 |
| **B′（推奨）** | **着手せず、移行の「順序」の裁定を環流する**。REST は「例外」ではなく「ADR-0029 自身が過渡的と呼ぶ状態の継続（移行待ち）」と位置づける。#584 は `blocked`（裁定待ち）で開けたまま | 環流 1 件＋本 IADR | なし | ADR-0029 に反しない（例外を求めない・移行義務を否定しない）。基盤先行が裁定されれば AST は現物へ揃える（`IADR-0001`）。AST 先行が裁定されれば案 A の切り方で着手する |
| **C** | Assumptions 2 本（行 11・行 24）を先行し先例を作る | 土台の固定費は A と同じ（h2c・Helm・CPM・互換検査）を **2/22 本のために払う** | A と同じ写像リスク（範囲は小） | 「作業の分割は妨げない」に合致。**ただし先例を AST が作る問題は A と同じ**で、2 本 gRPC・20 本 REST の混在が長期化する |

### 推奨: B′

- **決め手は「揃える先の現物が無い」こと。** ADR-0029 の 2026-08-04 追記は MSP 側の裁定（planning#180 Q26）だが、MSP 自身が
  proto 0 件・フォローアップ（配置・versioning の実装ガイド）未履行・MSP#441 は gRPC を未着手のままクローズしている。
  この状態で AST が先に gRPC を入れると、proto の置き場・バージョニング・h2c ポート・認可の写し方を AST が決め、
  後から MSP が別の形で決めたときに**揃え直しが 22 本ぶん発生する**。
- **A/C は「実装してから合わせる」、B は「例外を自認する」であり、どちらも計画側の判断を実装側で先取りする。**
  推奨が ADR-0029 と食い違うわけではない（移行義務は認める）ので、**実装せず環流を先にする**（オーケストレータ指示の原則）。
- 裁定後に A を採る場合の切り方は `IADR-0284` 決定 5 に先に書いておく（判断が下りたら即着手できるように）。

## 受け入れ基準

- [x] 26 本（24 ＋ 軸 2 の 2）の射程表が、各行の判定根拠（境界・所有者・fail-safe）とともに本書と IADR にある
- [x] 判断案 A/B/B′/C の工数・退行リスク・計画整合の表があり、推奨が 1 つ選ばれている
- [x] #584 の記述と `MSP/ADR-0029` の衝突が planning へ環流されている（既存 issue を先に検索し 0 件を確認）
- [x] `IADR-0284` に判断（暫定）と環流先が記録され、README 索引が更新されている
- [x] #584 へ要約と planning issue URL をコメントした
- [x] コードを変更していない（`git diff --stat` が `.ai-context/` と `docs/blocked-tasks.md`〔B-4 の裁定待ち行〕のみ）
- [x] `check-trace-blocks` / `check-cross-repo-refs` / `check-doc-links` / `check-adr-index-sync` / `check-plan-id-qualification` が緑

## テスト方針

コード変更なし。文書検査器（上記）を PR で通す。

## 計画書との差異

- 差異: **あり**。#584（本リポの issue）の「REST 継続なら閉じてよい」と `MSP/ADR-0029` 2026-08-04 追記「該当するものをすべて gRPC へ移行する」が衝突。
  対応: planning へ環流（feedback.yml・`feedback` `decision-needed`）。issue 番号・URL は `IADR-0284` §環流 に記録。

## 未決事項（計画側の裁定待ち）

1. 移行の**順序**: 基盤（MSP）が proto の配置・versioning・h2c・認可の現物を先に作り AST が追随する／AST が先行して先例を作る／同時
2. AST→MSP 4 本（LlmGateway ×2・KB ×2）の proto 公開時期（MSP 側）
3. AST の呼び出しプロファイル（人手・分単位・全件 fail-safe）を踏まえ、それでも一括移行か、対象経路を明記した例外 ADR を切るか

## 申し送り

- **ID 名前空間の罠**: 本リポで `ADR-0029` と裸で書くと資料再編 ADR を指す。gRPC 基準は必ず `MSP/ADR-0029`（本文）／`MSP:ADR-0029`（frontmatter）。
- **IADR 番号**: 当初 `ls .ai-context/adr | sort | tail -1` ＝ 0281 → 0282 を採ったが、PR #639（watchlist シード）が先に develop へマージして 0282 を確保し、
  0283 は PR #647 が予約したため、**先着尊重で 0284 へ改番した**（2026-09-03）。改番はファイル名・索引・仕様書・blocked-tasks 行・PR 本文・#584 コメントの全 6 箇所へ追随した。
