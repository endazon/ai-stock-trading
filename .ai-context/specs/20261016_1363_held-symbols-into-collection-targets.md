---
title: 情報収集の対象（Finnhub のニュース・SEC EDGAR の開示）へ保有銘柄を合流させ、SEC EDGAR の CIK を銘柄から導く（#1363）
type: spec
status: done
related_ids: [FR-01, FR-02, UC-01, ADR-0004, ADR-0020, ADR-0043, IADR-0064, IADR-0114, IADR-0435, IADR-0446, IADR-0453, IADR-0521, IADR-0547]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01, FR-02)
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 2 (b))
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md (決定 3)
  - planning:projects/ai-stock-trading/06_technical/02_datasource-candidates.md (SEC EDGAR)
---

# 仕様書: 情報収集の対象へ保有銘柄を合流させ、SEC EDGAR の CIK を銘柄から導く（#1363）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は AST `origin/develop` `e36d5d68`。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-01**（定時の収集・正規化・KB 保存）、**FR-02**（定時の判断）。関連: UC-01 基本フロー 3（判断の文脈は保有ポジション・収集情報）、
  ADR-0043 決定 2 (b)（1 巡回を巡回間隔に収める）、ADR-0020 決定 3（SEC EDGAR は欠測時「記録・通知のみ」）、ADR-0004（SEC EDGAR の採用）。
- 前提の IADR: IADR-0435（Finnhub の対象を監視銘柄から決める）、IADR-0521（定時の判断対象は監視銘柄 ∪ 保有銘柄。保有不明は監視銘柄だけ）、
  IADR-0064・IADR-0114（SEC EDGAR の User-Agent 必須・自制）。
- 起票: [#1363](https://github.com/endazon/ai-stock-trading/issues/1363)。

## 症状と原因

- PoC 夜間監査（2026-10-14・10-15）: 情報収集のログ `監視銘柄から Finnhub の対象を決めました: 米国の銘柄 8 件（監視銘柄 8 件）` が全巡回に出る一方、
  取引判断は 11 銘柄（監視 8 ＋ 保有 AAPL・AMZN・GOOGL）を判断していた。RAG は監視銘柄 10 件・AMZN/GOOGL 5 件。
  LLM の根拠に「参考情報に GOOGL 固有のニュースはなく」の類が 32 件（10-14）。
- 原因: `FinnhubSymbolSelector.UnitedStatesSymbols` が `/monitor/watchlist` の米国銘柄だけを採る。保有を合流させる箇所が無い。
- SEC EDGAR の CIK は `values-local.yaml` の `Collection__Source__SecEdgar__Ciks__0=0000320193`（AAPL）1 社に固定。

## 計画の確認

project-planning `origin/main`（`fc61ca3`）を読んだ。

| 計画書 | 記述 | 読み |
| --- | --- | --- |
| FR-01 | 定時に市況・ニュース・開示を収集し KB へ保存する | 対象銘柄の範囲は定めない |
| UC-01 基本フロー 3 | 判断は保有ポジション・収集情報を文脈にする | 判断対象（保有を含む）の情報を集める読みが自然 |
| ADR-0043 決定 2 (b) | 1 巡回の要求数を巡回間隔に収める | 保有を足しても上限は掛ける。どれを先に採るかの定めは無い |
| ADR-0020 決定 3 | SEC EDGAR の欠測は記録・通知のみ | CIK を導けないときの扱いの根拠 |
| 02_datasource-candidates | SEC EDGAR は無料・キー不要・10 回/秒/IP・User-Agent 必須 | 対応表の取得も同じ規律で送る |

結論: **収集を判断対象（監視銘柄 ∪ 保有銘柄）に揃えるのは実装の不足の是正である**（計画の変更ではない）。計画への環流は要らない。

## 母集合（規則 9）: `UnitedStatesSymbols`・`Ciks`・監視銘柄を読む箇所

`git grep` を `UnitedStatesSymbols` / `\bCiks\b` / `monitor/watchlist|IWatchlistReader|IWatchlistProvider|GetWatchlist` で引いた（基点 `e36d5d68`。`.ai-context/specs/` は対象外）。

| 箇所 | 何をしているか | 本件での扱い |
| --- | --- | --- |
| `InformationCollectionService/Features/InformationCollection/FinnhubSymbolSelector.cs:77,122`（`UnitedStatesSymbols`） | 監視銘柄から米国の銘柄を採り、Finnhub の対象にする | **直す**: 保有を先に合流（決定 1） |
| `InformationCollectionService/Features/InformationCollection/IWatchlistReader.cs` | 監視銘柄の読み口（ポート） | 変えない（保有は別の口 `IHeldSymbolsProvider` を足す） |
| `InformationCollectionService/Infrastructure/ExternalServices/HttpMarketMonitorWatchlistReader.cs`・`GrpcMarketMonitorWatchlistReader.cs` | `/monitor/watchlist`（REST・gRPC）の読み手 | 変えない |
| `InformationCollectionService/Program.cs:88-127` | 監視銘柄の読み手と選択器の組み立て | **直す**: 保有の読み手（`RiskManagement:BaseUrl`）を渡す。SEC だけ有効でも巡回の最初に決め直す |
| `InformationCollectionService/Infrastructure/ExternalServices/CollectionSourceOptions.cs:82`（`SecEdgarOptions.Ciks`） | 固定 CIK の構成 | **残す**（併用。決定 3） |
| `InformationCollectionService/Infrastructure/ExternalServices/InformationSourceFactory.cs:272,386-392`（`Ciks`） | 固定 CIK の正規化・SEC の有効化の条件・生成 | **直す**: 結線時は CIK を銘柄から導く（固定 CIK が空でも有効化） |
| `InformationCollectionService/appsettings.Development.json:17`（`"Ciks": []`） | 開発既定 | 変えない（空のまま） |
| `InformationCollectionService/Tests/InformationSourceSelectionTests.cs:48`・`Tests/Infrastructure/ExternalServices/InformationSourceFactoryTests.cs:94` | 固定 CIK での有効化の試験 | 変えない（未結線の従来の形として通る） |
| `deploy/helm/ai-stock-trading/values-local.yaml:220`（`Collection__Source__SecEdgar__Ciks__0`） | AAPL 固定 | **直す**: 空にする（銘柄から導く） |
| `deploy/helm/ai-stock-trading/values.yaml:409` | 本番既定（空） | 注記だけ足す |
| `TradeDecisionService/Infrastructure/ExternalServices/Stage0AsOfComposition.cs:15,93`・`values-local.yaml:385`（`Stage0AsOf__SecEdgar__Ciks__0`） | Stage 0 記録器の as-of 開示の CIK（AAPL 固定） | **範囲外**: 記録器は過去の判断日の再生用で、本番の KB の対象とは別の構成（#1219 の作業仕様書が「CIK を足さない」と決めている）。残余に記す |
| `TradeDecisionService/Infrastructure/Steps/InformationCollectedHandler.cs:77`・`HttpWatchlistProvider.cs`・`GrpcWatchlistProvider.cs`・`ConfigurationWatchlistProvider.cs` | 取引判断の監視銘柄の読み手 | 変えない（判断対象は IADR-0521 で既に保有を含む） |
| `TradeDecisionService/Infrastructure/ExternalServices/HttpAsOfWatchlistSource.cs` | Stage 0 の as-of 監視銘柄 | 範囲外（過去の再生） |
| `NotificationService/.../HttpMarketMonitorWatchlistController.cs`・`GrpcMarketMonitorWatchlistController.cs`・`RevisePolicy/PolicyRevisionCommandHandler.cs:69` | `/policy` の照会・入れ替えの適用 | 範囲外（収集の対象ではない） |
| `MarketMonitorService/.../GetWatchlist/Endpoint.cs`・`WatchlistReadGrpcService.cs`・`MonitorWatchlistService.cs` | 送り手 | 変えない |
| `Bff/AiStockTrading.Bff.Endpoints/MonitorBffEndpoints.cs` | 画面向けの pass-through | 範囲外 |

保有を読む既存の口（倣う形）: 取引判断の `IHeldSymbolsProvider.GetHeldSymbolsAsync`（`HttpHeldPositionProvider`・`GrpcHeldPositionProvider`。IADR-0521 決定 1）。
サービス間は直接参照しないため、同じ形のポートと REST の読み手を情報収集に置き、行の検証の規則（#943・#854）を写す。

## 判断（IADR-0547 に記録）

1. **保有の合流**: 選択器は巡回ごとに監視銘柄と保有を読み、**保有（米国）を先に、続いて監視銘柄**の並びにしてから上限（`Fit`）を掛ける。保有を読めない・例外の巡回は監視銘柄だけ（警告）。
2. **CIK**: SEC の `company_tickers.json` を 24 時間ごとに取得して ticker→CIK を引く（設定の対応表は監視銘柄の入れ替え〔`/policy`〕に追随しない）。CIK の対象は上限を掛ける前の全件。
3. **固定 CIK**: 残して「常に足す分」として併用する。`values-local` は空にする。
4. **範囲**: 1. と 2. は同じ「収集の対象」を共有するため 1 PR に収める（分けない）。gRPC の保有の読み手は足さない（残余）。

## 受け入れ基準と試験の写像

テスト ID は採番しない（FR-01 は網羅裁定 #211 の必須範囲の外。作業仕様書と xUnit を正の記録とする）。

| 受け入れ基準 | 試験 |
| --- | --- |
| 保有が対象に入る（保有が先・重複は 1 件・日本の保有は入れない） | `FinnhubSymbolSelectorHeldSymbolsTests.保有銘柄が対象に入り保有が先に並ぶ` |
| 上限では保有が優先される（後回しは監視銘柄の末尾） | `…上限を超えるときは保有を先に採る` |
| 保有を読めないときは監視銘柄だけに縮退する（警告・直前の保有を使わない） | `…保有を読めないときは監視銘柄だけに縮退して警告する`・`…保有の照会が例外でも監視銘柄だけに縮退する`・`…保有の照会で呼び出し側の中止は伝播する` |
| 監視銘柄が不明の巡回でも保有は合流する | `…監視銘柄を読めない巡回でも保有は合流させる` |
| 未結線は従来どおり | `…保有の口が未結線なら監視銘柄だけで警告しない`・既存の `FinnhubSymbolSelectorTests` 全件 |
| 保有の読み手の契約・原則 A | `HttpRiskManagementHeldSymbolsProviderTests`（送り手の本物の `OpenPositionView`・欠けた行・非 2xx） |
| CIK が銘柄から導かれる | `SecEdgarCikDerivationTests.CIKは収集の対象の銘柄から導かれる`・`…クラス株の表記の違いを吸収して引ける`・`…情報源は導いたCIKの提出書類を取りに行く` |
| 固定 CIK の併用 | `…構成の固定CIKは導いたCIKに足す`・`…対応表を一度も読めていなければ固定CIKだけで収集する` |
| 対応表の取得の規律（24 時間・失敗時は直前の表・1 時間後に再試行・User-Agent） | `…対応表は24時間ごとにだけ読み直す`・`…読み直せなければ直前の表を使い続ける`・`…CIKは収集の対象の銘柄から導かれる`（UA） |
| 一度も読めていなくても失敗の後は 1 時間待つ（403・429・503。待ちの間は固定 CIK へ縮退、待ちの後に読み直す） | `…一度も読めていなくても失敗の後は待ってから読み直す`（［2026-10-16 追記 / #1363 レビュー 🟡］） |
| 対応表が不明で固定 CIK も空なら欠測 | `…対応表が不明で固定CIKも空なら欠測にする` |
| 本番の組み立てで届く（トークン付き・保有が先・SEC の CIK） | `WatchlistFollowingCompositionTests.結線時は保有銘柄も対象に合流しSECのCIKも銘柄から導く`・`…未結線で固定CIKが空ならSECは有効化されない`・`CompositionWiringGuardTests` |

変異（手元で赤になることを確かめた）:

| 変異 | 赤になった試験 |
| --- | --- |
| 保有を監視銘柄の後ろに並べる | 保有が先・上限で保有を先・縮退（1 回目の並び）・監視銘柄不明でも合流・組み立て（5 件） |
| CIK を銘柄から導かない（固定 CIK だけ） | `SecEdgarCikDerivationTests` 9 件・組み立て 1 件 |

## 検証結果

［2026-10-16 追記 / #1363 レビュー 🟡］初回の記録は `dotnet format` を Tests の csproj だけに掛けており、本体の `InformationSourceFactory.cs` の
字下げの差分を見落としていた（CI の lint が赤）。整形し直した後の結果を次に記す（develop `89b74a46` を merge 済み）。

- `dotnet format backend/backend.slnx --verify-no-changes --include <変更した .cs 13 件>`: 差分なし（終了コード 0）。
- `dotnet build`（情報収集サービス）: 警告 0・エラー 0。
- `dotnet test backend/Services/InformationCollectionService/Tests/InformationCollectionService.Tests.csproj`: 全件合格（件数は PR 本文）。
- `dotnet test backend/Tests/AiStockTrading.Architecture.Tests`: 212 件合格（1 件 skip は既存）。
- `helm lint`（既定・`values-local`）合格。`helm template … | node scripts/check-finnhub-key-budget.js` は既定・local とも ok（Finnhub の予算は不変）。
- 文書系: `check-trace-blocks`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-adr-index-sync`・
  `check-adr-index-addendum-loss`・`check-doc-links`・`check-test-traceability`・`check-commit-messages` が OK。
- テスト ID は使っていないため、develop の T-10-2683・T-10-2684（#1371）とは衝突しない。IADR は develop の最大 0546 の次の 0547。

## PoC での確かめ方

1. 配備後、Loki で `監視銘柄から Finnhub の対象を決めました: 米国の銘柄` を引く。`米国の銘柄 8 件（監視銘柄 8 件）` →
   `米国の銘柄 11 件（監視銘柄 8 件・監視銘柄の外の保有 3 件・保有 米国 3 件）` に変わること。
2. `収集の対象の銘柄から SEC EDGAR の CIK を決めました` が出て、CIK の件数が対象の銘柄数（ETF 等の「対応表に無い」を除く）になること。
3. 取引判断の RAG の件数のログで、AMZN・GOOGL が 5 件 → 監視銘柄と同程度（10 件）に増え、AAPL の「除外 5 件」の内訳が変わること。
   LLM の根拠の「参考情報に … 固有のニュースはなく」の件数が減ること。
4. 保有の縮退: リスク管理を止めると `保有銘柄を読めなかったため、この巡回は監視銘柄だけから…` の警告が巡回ごとに出て、件数が監視銘柄だけに戻ること。

## 範囲外・残余

- 保有の gRPC の読み手（`RiskControlsRead/GetOpenPositions`）は足さない。情報収集は `RiskManagement:Grpc` を持たず、REST だけで読む。
- Google News の固定クエリ（「米国株」1 件）は本件の対象外（IADR-0453 の残余のまま）。
- Stage 0 記録器の as-of 開示の CIK（`Stage0AsOf__SecEdgar__Ciks__0`）は AAPL 固定のまま。
