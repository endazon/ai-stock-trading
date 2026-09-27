---
title: east-west gRPC の段 4 —— Report daily-policy・MarketMonitor watchlist・CostControl costs/state の読み取りを生成クライアントへ寄せ、REST と並走させる
type: spec
status: done
related_ids: [NFR, FR-01, FR-02, FR-04, FR-07, FR-13, FR-15, IADR-0031, IADR-0095, IADR-0284, IADR-0328, IADR-0331, IADR-0420, IADR-0427, IADR-0435, IADR-0440, IADR-0442, IADR-0445, IADR-0446, MSP:ADR-0029, MSP:ADR-0075]
author: endazon (with Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 仕様書: east-west gRPC 段 4（Report・MarketMonitor・CostControl の読み取り）（#1061）

## 起点

- issue #1061（段 4 専用。受け皿は #753・`Refs`）。雛形は段 2（[IADR-0427](../adr/IADR-0427_risk-read-grpc-stage2.md)）・
  段 3（[IADR-0445](../adr/IADR-0445_audit-read-grpc-stage3.md)）。射程と段の切り方は
  [IADR-0284](../adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5。本 PR の判断は
  [IADR-0446](../adr/IADR-0446_report-monitor-cost-read-grpc-stage4.md)。
- 計画 ID: NFR（トランスポートの差し替え。振る舞いは変えない）。経路ごとの起点は FR-04・FR-07（日報の方針）・FR-02・FR-13・FR-15
  （監視銘柄）・FR-01（情報収集の巡回と費用統制）。
- 同じ PR で段 3 の監査の指摘（`HttpFxSourceStatusSource` の文書コメントの位置）を直す。

## 射程の確定（IADR-0284 決定 5 の逐語と、引き直した母集合）

決定 5 の段 4 行（逐語）:

> | 4 | Report `daily-policy`・MarketMonitor `watchlist`・CostControl `costs/state` | 1 PR |

### 母集合の引き方（規則 1〜6・9・10。`origin/develop` `1d7e9ca2`）

| 軸 | 引いたもの | 件数 |
| --- | --- | --- |
| 1（呼び出し先の側） | `backend/Services/*/Infrastructure/ExternalServices/*.cs`（テスト以外）の `"/reports…"`・`"/monitor…"`・`"/costs…"`（`$"…"` を含む） | TradeDecision 3 ファイル（daily-policy・watchlist・watchlist/as-of）・InformationCollection 2（watchlist・costs/state）・Notification 3（review／confirm／request-changes／period-keys・policy-revisions・watchlist／proposal-apply） |
| 2（構成の側） | `Reports:BaseUrl` / `MarketMonitor:BaseUrl` / `CostControl:BaseUrl` | TradeDecision・InformationCollection・Notification の Program.cs |
| 3（提供側の側） | 3 サービスの `read`（OwnerOrService）サブグループ | Report `GET /reports/daily-policy`・MarketMonitor `GET /monitor/watchlist`・`GET /monitor/watchlist/as-of`・CostControl `GET /costs/state` |
| 4（BFF） | `backend/Bff/**` の `"/monitor…"` など | 中継（north-south） |

### 段 4 で移すもの（決定 5 の 3 ルート＝射程表の 3 本 ＋ 表の後に追加された 2 本）

| 射程表の行 | 呼び出し元 | クライアント | 提供側のルート | rpc |
| --- | --- | --- | --- | --- |
| 12 | TradeDecision | `HttpDailyPolicyProvider` | Report `GET /reports/daily-policy` | `DailyPolicyRead/GetConfirmedDailyPolicy` |
| 15 | TradeDecision | `HttpWatchlistProvider`（定時サイクル・判断のプロンプトの 2 口） | MarketMonitor `GET /monitor/watchlist` | `WatchlistRead/GetWatchlist` |
| （#1049 で追加） | TradeDecision | `HttpAsOfWatchlistSource` | MarketMonitor `GET /monitor/watchlist/as-of` | `WatchlistRead/GetWatchlistAsOf` |
| （#1015 で追加） | InformationCollection | `HttpMarketMonitorWatchlistReader` | MarketMonitor `GET /monitor/watchlist` | `WatchlistRead/GetWatchlist` |
| 23 | InformationCollection | `HttpCostControlGate` | CostControl `GET /costs/state` | `CostStateRead/GetCostState` |

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| Notification の `HttpReportReviewController`・`HttpPolicyRevisionController`・`HttpMarketMonitorWatchlistController` | owner マップ機密クライアントのトークンで呼び、同じクラスに OwnerOnly の書き込みを持つ。段 5 でクラスごと移す（段 2 の Notification `stage-gate` と同じ扱い。IADR-0427 決定 1） |
| BFF の中継 | north-south（BFF は REST） |
| テスト内のパス | 母集合はプロダクションコード |
| helm の既定値・values-local | **既定は REST**（既定描画を変えない）。values.yaml にはコメントで有効化の手順だけを書く |

### PR の分け方

提供側 3・呼び出し元 2 だが、経路は 5 本・rpc 4 本と段 2（10 本・rpc 8 本・呼び出し元 3）より小さく、提供側は各 1 ファイルの薄い gRPC 面である。
呼び出し元の Program.cs（取引判断・情報収集）は 2 つの提供側にまたがるため、提供側ごとに PR を割ると同じファイルを 2 本の PR が触って直列化が要る。
**1 PR** とする（決定 5 の「1 PR」のまま）。

## 設計

詳細は IADR-0446。要点のみ。

1. **proto 3 本**（`Shared.Grpc/Protos/aistocktrading/{report,marketmonitor,costcontrol}/v1/`）。項目は呼び出し元が読むものだけ・スカラーは `optional`・
   市場は `UNSPECIFIED = 0` を持つ列挙・as-of の一覧は存在を持つ入れ物。日報の方針の未確定は `policy` の無い応答（NOT_FOUND にしない）。
2. **提供側**: 3 サービスに `*GrpcService`（REST と同じサービス・同じ `OwnerOrService`）。as-of の時刻の検証は REST のエンドポイントから切り出して共有。
   Program.cs は各 2 行（`AddAiStockTradingGrpcListener` と `MapGrpcService`）。REST 面は不変。
3. **呼び出し元**: 取引判断は `Reports:Grpc`・`MarketMonitor:Grpc`、情報収集は `MarketMonitor:Grpc`・`CostControl:Grpc`。宣言があれば輸送を singleton で
   登録し、各ポートの工場が `Grpc*` 実装を選ぶ（BaseUrl より優先・使えない宛先は起動時に落とす）。deadline の既定は各 HttpClient の Timeout（5 秒）、
   再試行の既定 1。**呼び出しの規則（deadline・再試行）は各呼び出し元サービスに 1 つ**（取引判断は段 2 の輸送から `TradeDecisionGrpcCalls` へ切り出して
   共有、情報収集は `InformationCollectionGrpcCalls` を新設）。
4. **解釈の共有**: 行の検証（#1041 の欠けた行・値域外の市場、#915 の `isHalted` の欠落・倍率の非正、as-of の再構成の可否）は REST のアダプタの
   解釈を `internal static` に切り出し、gRPC は同じ nullable の行へ写してから呼ぶ。

## 受け入れ基準（#1061）

- [x] proto と生成クライアント・サーバ実装。提供側 3 サービスに `MapGrpcService` 各 1 件。REST 面は不変
- [x] 同値: 本物の Program.cs で REST と gRPC が同じ値を返し、入力の誤りも同じ扱い
- [x] 否定形: 資格情報なし `UNAUTHENTICATED`・ロール不足 `PERMISSION_DENIED`
- [x] 呼び出し元 2 サービスが宛先の有無で切り替え、既定は REST。**本番の Program.cs から**解決して実際に呼ぶ
- [x] 原則 A: 欠落を既定値で読まない。縮退の向きは REST と同じ
- [x] timeout / retry の陽性・陰性対照（実 Kestrel h2c・127.0.0.1）
- [x] 変異注入の実測
- [x] proto 互換検査器の baseline 更新と陰性対照
- [x] helm の既定描画と values-local の描画がバイト等価
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` と文書系検査器が通る

## テスト方針（テスト ID は T-10-1690〜T-10-1698。develop の最大 T-10-1677 から間を空けた新しい区画）

| ID | 置き場 | 観点 |
| --- | --- | --- |
| T-10-1690 | ReportService.Tests | 日報の方針: gRPC が REST と同じ値（確定・未確定）・認可の否定形 |
| T-10-1691 | MarketMonitorService.Tests | 監視銘柄（現在・当時）: gRPC が REST と同じ値・as-of の時刻の検証・認可の否定形 |
| T-10-1692 | CostControlService.Tests | 費用統制の判定: gRPC が REST と同じ値・認可の否定形 |
| T-10-1693 | TradeDecisionService.Tests | 日報の方針の受け手: 未確定・欠落・失敗は「取引しない」 |
| T-10-1694 | 〃 | 監視銘柄の受け手（定時サイクル・プロンプト・当時）: 欠けた行・未指定の市場の扱いが REST と同じ |
| T-10-1695 | InformationCollectionService.Tests | 監視銘柄の受け手・費用統制の受け手: 欠けた行は不明、`isHalted` の欠落・倍率の非正は Normal、停止は尊重 |
| T-10-1696 | 各呼び出し元 | 契約: 送り手の本物の型 → 提供側の写し → 実 h2c → 受け手が同じ値で読む |
| T-10-1697 | 各呼び出し元 | 配線: 本番の Program.cs で宣言あり → `Grpc*` で実際に呼ぶ、無し → 従来、不正 → 起動時に例外 |
| T-10-1698 | 各呼び出し元（結合） | 実 Kestrel h2c で deadline・再試行（陽性・陰性） |

## 計画書との差異

- 差異: なし（射程・順序・一括移行の義務は IADR-0284 決定 1・2 と `MSP/ADR-0075` のまま）。決定 5 の段 4 行へ表の後の 2 本を加えることは
  IADR-0446 決定 1 と IADR-0284 の日付つき追記に残す。

## 未決事項

- 稼働クラスタでの h2c 往復は未実測（段 1〜3 と同じ）。
- 実効構成の自己申告（introspection）は REST の構成しか見ない（既存の欠落。段 6 までに別 issue）。

## 着手後に判明した制約（規則 10）

| 判明したこと | 実測 | 扱い |
| --- | --- | --- |
| 日報の方針の未確定は REST では 404 で、受け手は警告なしで「取引しない」へ倒す。gRPC で `NOT_FOUND` にすると呼び出しの規則が毎朝「照会に失敗」と警告する | `HttpDailyPolicyProvider` の 404 分岐・`TradeDecisionGrpcCalls` の警告 | `policy` の無い応答で運ぶ（IADR-0446 決定 3） |
| 取引判断は段 2 の輸送が呼び出しの規則を持ち、情報収集には gRPC の呼び出しが無かった | `RiskManagementGrpcTransport.CallAsync`・情報収集の csproj に `Shared.Grpc` の参照が無い | 取引判断は `TradeDecisionGrpcCalls` へ切り出して共有、情報収集は `InformationCollectionGrpcCalls` を新設（IADR-0446 決定 5） |
| 情報収集の起動時の日次要求の見積り（`EstimateAtStartup`）と introspection は `MarketMonitor:BaseUrl` しか見ない | `InformationCollectionService/Program.cs` | 既存の欠落と同じ種類として IADR-0446 の結果に残した（本 PR は既定を変えないので実害は無い。段 6 までに輸送へ追随させる） |
| 取引判断・情報収集の常駐の巡回が起動直後に照会し得る | 段 2 の #1010 の実測（市場監視） | 配線の試験は rpc ごとの呼ばれた回数を**差分**で数える |
| 段 3 の監査の指摘（`HttpFxSourceStatusSource` の文書コメントが `Window` に付いていた） | PR #1060 の監査 | 同じ PR で `GetStatusAsync` へ付け直した（別コミット） |

## 検証の記録（2026-09-27）

- `dotnet build backend/backend.slnx`: 0 エラー。
- `dotnet test`: 件数は PR 本文。新規の試験は報告書 4・市場監視 8・費用統制 5・取引判断 23・情報収集 26。
- `dotnet format backend/backend.slnx --verify-no-changes`: exit 0。
- `node scripts/check-proto-contracts.js`: `--update` 後 OK（7 ファイル）。陰性対照は PR 本文。
- 変異注入 12 件（1 つずつ入れて実行し、コミット済みの版を取り出して書き戻した）はテスト仕様書の本節の表に載せた。
- helm: `helm template ast deploy/helm/ai-stock-trading`（既定）と `-f values-local.yaml` の描画は変更前後で sha256 が一致（差分 0 行）。
  陽性対照として 3 つの提供側に `grpcPort=8081` を与えると、各サービスの `grpc` ポート・`containerPort: 8081`・env `Grpc__Port` だけが増える。
  稼働中のクラスタ・OpenD には触れていない（描画はローカルのみ）。
