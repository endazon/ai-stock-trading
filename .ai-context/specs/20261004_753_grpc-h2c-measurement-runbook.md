---
title: east-west gRPC（段 1〜5）の h2c 往復を稼働クラスタで実測する Runbook と、env を消さない計測 overlay（#753 段 6 の前提）
type: spec
status: accepted
related_ids: [NFR, IADR-0284, IADR-0489, IADR-0328, IADR-0331, IADR-0427, IADR-0445, IADR-0446, IADR-0448, IADR-0449, IADR-0450, IADR-0439, ADR-0047]
author: claude (Claude Code)
created: 2026-10-04
updated: 2026-10-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md
---

# 仕様書: east-west gRPC（段 1〜5）の h2c 往復の実測 Runbook と計測 overlay（#753）

## 起点となる計画書（トレーサビリティ）

- 非機能: NFR（無採番。サービス間通信の方式。`MSP/ADR-0029` の境界基準・`MSP/ADR-0075` の一括移行の義務）
- 関連 ADR: `MSP/ADR-0029`・`MSP/ADR-0075`・ADR-0047（ボットのトークンはサービスの身元）
- 関連 IADR: IADR-0284 決定 5（段 0〜6）と 2026-09-27 追記（段 6 の範囲＝呼び出し元の `Http*` アダプタと名前付きクライアントの退役）・
  IADR-0328（段 0: h2c 専用ポート `Grpc:Port`・helm の `grpcPort`）・IADR-0331 / 0427 / 0445 / 0446 / 0449 / 0450（段 1〜5）・
  IADR-0448（gRPC 面の所有者の門）・IADR-0439（`helm-release-drift.js`）・**IADR-0489（本件の決定）**
- 起票: [#753](https://github.com/endazon/ai-stock-trading/issues/753)。最後のコメント（2026-09-28）が段 6 の着手条件を 2 つ置いた:
  1. 稼働クラスタで `*:Grpc` を宣言し、gRPC のポートを開いた配備を行う。
  2. 段 1〜5 の全経路の h2c 往復を実測し、結果を #753 に記録する。
- 依頼: PoC セッション（稼働 k3s クラスタを操作する側）が、上の 2 つを実施するための手順書と values overlay を求めた。
  過去の事故（values の `extraEnv` は配列なので、2 つ目の values / `--set` で重ねると配列ごと置き換わり `Reconciliation__*` 等が消えた）を再発させない形を条件とする。
- 基点コミット: `origin/develop` `3daeb2a0`

## 目的・背景

- 段 1〜5 で east-west の呼び出しは gRPC の実装を持ったが、消費側はどれも「既定 REST、`<提供側>:Grpc` を宣言したときだけ gRPC」である。
  helm・compose に宣言は無く、提供側の `grpcPort` も宣言していない（＝gRPC の面は既定の配備で開いていない）。
- 段 6（REST のアダプタの退役）の前に、稼働クラスタで宣言を入れて全経路の往復を実測する必要がある。本件はその**手順と道具**を用意する。
  実測そのもの（クラスタの操作）は本件の外（PoC セッションが行う）。

## 対象範囲

- 対象:
  1. chart: 呼び出し側の宣言を**マップのキー** `services.<name>.grpcClients.<提供側>: true` で描くテンプレート（`templates/deployment.yaml`）。
     宛先は呼び先の `grpcPort` から導出し、片方だけの宣言・未知の提供側・`extraEnv` との二重定義は描画時に止める。既定描画・values-local 描画はバイト等価。
  2. 計測 overlay `deploy/helm/ai-stock-trading/values-grpc-measurement.yaml`（提供側 6 の `grpcPort: 8081`・呼び出し側 13 の `grpcClients`。配列を 1 つも持たない）。
  3. CI: `helm.yml` に overlay の検査（env が 1 本も消えない・足すのは gRPC の宣言だけ・数がそろう・異常系が描画で止まる）。
  4. Runbook `docs/operations/grpc-h2c-measurement-runbook.md`（前提・適用・計測・合否・中止条件・切り戻し・記録）。運用仕様書と通信仕様書から参照を張る。
  5. values.yaml の有効化の案内コメント（「env 配列へ足す」）を `grpcClients` へ改める（配列へ足す案内は事故の形そのものである）。
  6. IADR-0489・索引行。
- 対象外:
  - 稼働クラスタでの適用・実測（PoC セッションの作業）。結果の #753 への記録も実測した側が行う。
  - 既定（values.yaml / values-local.yaml）で gRPC を有効にすること（段 6 の判断。実測の後）。
  - REST のアダプタ・名前付きクライアントの退役（段 6）。
  - AST→MSP の LlmGateway（chart の外の呼び先。`LlmGateway__Grpc` は従来どおり extraEnv。段 1′ の別系列）。
  - NetworkPolicy のテンプレート新設（chart に枠が無い。セキュリティ仕様書の未実装項目。本件は「在れば中止」を手順に置く）。
  - `scripts/k8s-local-deploy.sh` への overlay の取り込み（下の設計 3）。

## 母集合（規則 9。`origin/develop` `3daeb2a0`）

引き方:
- 呼び出し側の宣言キー: `git grep -nE '(AddressKey|GrpcAddressKey)\s*=\s*"' -- backend ':!*/Tests/*'` → 14 行。うち `LlmGateway:Grpc`（共有契約・AST→MSP）を除く **13**。
- 提供側: `git grep -ln 'AddAiStockTradingGrpcListener' -- backend/Services` → **6**（Audit・Configuration・CostControl・MarketMonitor・Report・RiskManagement）。
  `git grep -n 'MapGrpcService' -- backend/Services ':!*/Tests/*'` → 12 service。
- 経路（rpc 単位）: `git grep -noE '\.(Get|List|Engage|…)[A-Za-z]*Async\(' -- 'backend/Services/*/Infrastructure/ExternalServices/Grpc*.cs'` と、
  通信仕様書 `docs/api/east-west-grpc.md` §5〜§9 の表を突き合わせた。
- chart の配列: `yq '.services | to_entries[] | …extraEnv…'` で values.yaml / values-local.yaml の各サービスの env 名・BaseUrl を列挙した。

| 呼び出し側 | 宣言（構成キー） | 呼び先（提供側） | rpc | 段 | REST の現状（values-local） |
| --- | --- | --- | --- | --- | --- |
| cost-control | `Configuration:Grpc` | configuration | `Assumptions/Get` | 1 | 結線（`Configuration__BaseUrl`。values.yaml） |
| trade-decision | `Configuration:Grpc` | configuration | `Assumptions/Get` | 1 | 🔴 **未結線**（`Configuration__BaseUrl` が chart に無い＝既定プロバイダ） |
| trade-decision | `RiskManagement:Grpc` | risk-management | `GetOpenPositions`・`GetWorkingEntryOrders`・`GetSizingContext`・`GetEntryBlockers` | 2 | 結線 |
| market-monitor | `RiskManagement:Grpc` | risk-management | `GetOpenPositions` | 2 | 結線 |
| report | `RiskManagement:Grpc` | risk-management | `GetOpenPositions`・`GetFills`・`GetDriftAdoptions`・`GetBuyInInferences`・`GetSessionUptime`・`GetStageGate` | 2 | 結線 |
| report | `Audit:Grpc` | audit | `AuditEventsRead/GetEventsByType`（6 供給元） | 3 | 🔴 **未結線**（`Audit__BaseUrl` が chart に無い＝未供給） |
| trade-decision | `Reports:Grpc` | report | `DailyPolicyRead/GetConfirmedDailyPolicy` | 4 | 結線 |
| trade-decision | `MarketMonitor:Grpc` | market-monitor | `WatchlistRead/GetWatchlist`・`GetWatchlistAsOf` | 4 | 結線 |
| information-collection | `MarketMonitor:Grpc` | market-monitor | `WatchlistRead/GetWatchlist` | 4 | 結線 |
| information-collection | `CostControl:Grpc` | cost-control | `CostStateRead/GetCostState` | 4 | 結線 |
| notification | `RiskManagement:Grpc` | risk-management | 読み 2（`RiskControlsOwnerRead/GetRiskStatus`・`RiskControlsRead/GetStageGate`）・書き 8（`RiskControlsOwnerWrite/*`） | 5 | 結線 |
| notification | `Reports:Grpc` | report | 読み 3（`ReportOwnerRead/*`）・書き 4（`ReportOwnerWrite/*`） | 5 | 結線 |
| notification | `MarketMonitor:Grpc` | market-monitor | 読み 1（`WatchlistRead/GetWatchlist`）・書き 1（`WatchlistOwnerWrite/ApplyWatchlistProposal`） | 5 | 結線 |

- 除外: `LlmGateway:Grpc`（report・trade-decision）。呼び先が基盤（chart の外）で、本 chart の `grpcPort` から宛先を導出できない。段 1′ の別系列で段 6 の数えにも入らない（IADR-0284 2026-09-11 追記）。
- 🔴 **未結線の 2 経路は、宣言で輸送だけでなく結線そのものが変わる**（trade-decision の全体前提条件は既定値から提供側の値へ、report の監査台帳の 6 供給元は「照会できませんでした」から実値へ）。
  Runbook では「輸送の切り替え」と分けて扱い、比較の基準（REST の往復）が無いことを明記する。

### 実測の証跡（観測点）をコードから引いた結果

- 呼び出し側の成功は**ログに出ない**（各輸送の `CallAsync` は成功時に何も書かない）。失敗だけが警告で出る:
  `gRPC 照会に失敗` / `gRPC 照会がタイムアウト` / `gRPC 呼び出しに失敗` / `gRPC 呼び出しがタイムアウト`（通知の書き込み）/
  `gRPC 応答を読めません` / `gRPC 応答が不正`（全体前提条件）/ `サービストークンを取得できないため`（報告書の門）。
  `git grep -n 'LogWarning' -- 'backend/Services/*/Infrastructure/ExternalServices/*Grpc*.cs'` で引いた。
- gRPC クライアントのライブラリのログは出ない（`CreateAiStockTradingChannel` は `LoggerFactory` を渡さない）。
- 提供側のライブラリ（Grpc.AspNetCore.Server 2.83.0）のログ `Error status code '{StatusCode}' with detail '{Detail}' raised.` は、
  カテゴリが `Microsoft.*` ではないので appsettings の `Microsoft: Warning` の抑止に掛からない（dll の文字列で確認）。
- 成功の積極的な証跡は OTel のメトリクス・トレースに頼る: 提供側の `http.server.request.duration`（`http.route` が `/<package>.<Service>/<Method>`、
  `network.protocol.version` が `2`）、呼び出し側の `http.client.request.duration`（`server.port` が 8081）。`ObservabilityExtensions` が
  `AddAspNetCoreInstrumentation` / `AddHttpClientInstrumentation` を両方のパイプラインに入れている。gRPC の状態コードの属性は
  OTel の実験的な旗（`OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_ENABLE_GRPC_INSTRUMENTATION`）が要り、配備で立てていないので**使わない**。
  Prometheus 上の系列名は collector の変換に依存するため、Runbook の最初の手順で実名を引かせる（既存ダッシュボードは旧名 `http_server_duration_milliseconds` を引いている）。
- 業務の縮退の印（既存の系列・アラート）: `ast_trade_cycle_decision_skips_total{reason=~"HoldingsUnknownOpen|WorkingEntriesUnknownOpen"}`（アラート
  `AstEntriesBlockedByUnknownHoldings`）・`ast_market_monitor_position_rows_degraded_total`（`AstStopLossPositionRowsDegraded`）・
  `ast_information_collection_finnhub_symbol_set_resolutions_total{outcome!="watchlist"}`。

## 設計

1. **呼び出し側の宣言はマップのキーにする（IADR-0489）。** `extraEnv` は配列で、helm は values を重ねるとき配列を**丸ごと置き換える**。
   実測（helm v3.16.4）: report の `extraEnv` に `Audit__Grpc` を 1 行だけ書いた overlay を values-local に重ねると、report の env は
   53 本 → 17 本になり `ServiceAuth__*`・`LlmGateway__*`・`RiskManagement__BaseUrl` 等 37 本が消えた。
   マップのキーは深くマージされるので、`grpcPort`（既存）と `grpcClients`（新設）だけを書く overlay は何も消さない。
2. **宛先はテンプレートが導出する。** 提供側の名前 → 呼び先のサービス名の表（6 行）をテンプレートに置き、`http://<呼び先>-service:<grpcPort>` を描く。
   「呼び先の `grpcPort` と呼び出し側の宛先を同じ変更で揃える」（values.yaml の注意書き）を構造で強制する —— 片方だけだと描画で止まる。
3. **適用は `helm upgrade --reset-then-reuse-values -f <overlay>`。`k8s-local-deploy.sh` は変えない。**
   - スクリプトは前回リリースの値の引き継ぎ（`broker.tier` 等）を自前で組むため、手で `helm upgrade -f values-local.yaml` を打つと
     `--set` で入れていた値が既定へ戻る（#626 の事故の形）。`--reset-then-reuse-values` はリリースの利用者の値をそのまま使い、新しいチャートの既定と overlay を重ねる。
   - 適用前の差は既存の `helm-release-drift.js --values <overlay>` で出す（リリースの値の後に overlay を重ねて描く＝適用と同じ合成）。env の差が gRPC の宣言だけで、OpenD が「変化なし」であることを確かめる。
   - 切り戻しは `helm rollback ast <適用前のリビジョン>`。スクリプトの再実行でも REST の既定に戻る（overlay を参照しないため）。
   - 棄却: スクリプトに追加の `-f` を受ける env を足す案。スクリプトは画像の作り直しと restart を含み、計測の一時適用には重い。
     計測が 1 回限りの操作である間は、既存の読み取り専用の差分検査と helm の標準機能で足りる（段 6 で既定にするときは values.yaml へ入れる）。
4. **CI の検査**（`helm.yml`「Assert gRPC measurement overlay keeps every env」）: 既定・values-local の 2 つで、overlay の有無の env 名の集合を Deployment ごとに比べ、
   ①overlay が配列を持たない ②overlay なしに gRPC の宣言が無い ③消えた env が 0 ④足した env が gRPC の宣言だけで、呼び出し側 13・提供側 6
   ⑤宛先の導出 ⑥異常系（片方だけ・未知の提供側・真偽値でない・二重定義）が描画で止まる、を検める。
5. **NetworkPolicy**: chart は NetworkPolicy を持たない（`git grep -i networkpolicy -- deploy` は chart の README と values-local のコメントの 2 件だけ）。
   Service の gRPC ポートは既存の `grpcPort` が `name: grpc` / `appProtocol: grpc` で描く。名前空間に NetworkPolicy が在る場合（chart の外で入れられたもの）は
   8081 を塞ぎ得るので、Runbook の前提確認で列挙させ、在れば中止とする（本件で許可の規則を足さない）。

## 受け入れ基準

- [x] AC-1: 既定描画・values-local 描画がテンプレート変更の前後でバイト等価（`cmp` で実測）。
- [x] AC-2: values-local ＋ overlay の描画で、全 Deployment の env が 1 本も消えず、足されるのは `*__Grpc` 13・`Grpc__Port` 6 だけ（helm.yml の新ステップがローカルで緑）。
- [x] AC-3: 自己変異 —— overlay に配列を足すと①で赤、①を外すと③で赤（order-execution の `Reconciliation__*` が消えたと名指しする）、宣言を 1 つ消すと④で赤。
- [x] AC-4: 呼び先の `grpcPort` 無し・未知の提供側・文字列の真偽値・`extraEnv` との二重定義は描画で止まる。`--set …=false` で 1 経路だけ外せる。
- [x] AC-5: Runbook が、経路表・適用・観測点（コードの文言と系列）・合否・中止条件・切り戻し・記録先を持ち、`docs/` の trace ブロック規約を満たす。
- [x] AC-6: 文書系の検査（trace-blocks・doc-links・cross-repo-refs・plan-id-qualification・knowledge-graph）・コミット件名・gitleaks が緑。

## 試験

- 描画の試験は helm.yml のステップ（CI）。ローカルでは helm v3.16.4 で同じスクリプトを走らせた（CI は v4.2.1）。
- C# のコードは変えていないので xUnit の追加は無い。

## 残余リスク

- 実測そのものは未実施（本件は道具まで）。系列名・属性名は OTel の版と collector の変換に依存し、Runbook の手順 0 で実名を確かめる前提である。
- `--reset-then-reuse-values` は helm 3.14 以降の旗。古い helm では使えない（Runbook の前提に書いた）。
- 未結線だった 2 経路（trade-decision の全体前提条件・report の監査台帳）は、宣言で業務の入力が変わる。Runbook で分けて観測させる。
- 書き込みの経路（ボット 13 本）は人の操作でしか発火しない。kill switch の起動など影響の大きい操作を実測のために打たない、を中止条件に置いた。未発火の経路は「未実測」として記録させる。
