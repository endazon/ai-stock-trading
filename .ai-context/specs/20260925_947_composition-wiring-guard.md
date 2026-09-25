---
title: 配線が消えても全テストが緑のままになる形を、本番の組み立てを組んで機械的に止めるガード
type: spec
status: accepted
related_ids: [NFR, IADR-0163, IADR-0335, IADR-0374, IADR-0380, IADR-0370, IADR-0397, IADR-0066, IADR-0067, IADR-0153, IADR-0169]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs: []
---

# 仕様書: 本番の組み立て（Program.cs）を組んで配線の消失を機械的に止める（#947）

## 起点

- [#947](https://github.com/endazon/ai-stock-trading/issues/947)。「配線が消えても全テストが緑のまま」の形が今週 4 回、監査で実測された
  （PR #919 / #929 / #918 / #940）。規約「検査器の追加は同型事故 2 回から」を超えている。
- 原則は IADR-0163 決定2 が散文で持つ（「不在が統制の無効を意味する依存は必須引数にする」）。本作業は**それを機械化**する。
- 計画 ID: NFR（メタ作業＝検査器。製品の FR には当たらない。`traceability.repo.md` の NFR 採番に当たる番号は無い）。

## 実測（着手時・`origin/develop` = `3d9b91c2`）

| 事実 | 出典 |
| --- | --- |
| 全 12 サービスが `WebApplicationFactory<Program>` の既存ファクトリを持つ（`Program.cs` をそのまま組める） | `backend/Services/*/Tests/*WebApplicationFactory.cs` |
| #919: `TradeDecisionAppService` は `skipReporter` を省略可能引数で受け、既定 NoOp | `TradeDecisionService/Program.cs:294`（登録）・PR #919 監査 M3 |
| #929 の是正前（`93edcd9d`）、MarketMonitor の試験は `FakeSchedule : IMarketSchedule` だけを使い、`MarketHoursSchedule` を 1 度も参照していない | `git grep MarketHoursSchedule 93edcd9d -- backend/Services/MarketMonitorService` → 本番 3 行のみ |
| #918: `ProtectiveStopDriftAdopter` はファクトリ登録で `sp.GetService<IBrokerPositionSource>()` を渡す。moomoo 構成でだけ照会が登録される | `OrderExecutionService/Program.cs:148-153, 210-217` |
| 既存の DI 検査器は「登録された型が本番から使われるか」（ソース走査）だけを見る。**呼ばれるが中身が抜ける**形は見ない | `backend/Tests/AiStockTrading.Architecture.Tests/UnwiredDiRegistrationTests.cs`（IADR-0335） |

## 設計（IADR-0397）

各サービスのテストに **組み立てガード**（`CompositionWiringGuardTests`）を 1 本置き、共有の検査エンジン
（`backend/TestSupport/AiStockTrading.TestSupport.Composition`）へ **本番の `Program.cs` を組んだホストの
`IServiceProvider`・`IServiceCollection` の写し・本番アセンブリ・テストアセンブリ** を渡す。
外界（DB・RabbitMQ・ブローカーの伝送）は既存ファクトリと同じく伝送の境界でだけ差し替える。

### 検査規則

| 規則 | 形 | 捕まえる事故 |
| --- | --- | --- |
| **W1 省略可能依存の未解決** | 型で組む登録（`ImplementationType`）・Wolverine ハンドラについて、DI が選ぶ構築子の**既定値つき参照型引数**を、組み立てが解決できない | #919（登録を消すと既定 NoOp へ黙って落ちる） |
| **W2 渡し忘れ** | 組み立てが作った実体を本番型の範囲で辿り、**依存フィールドが null** なのに組み立てはその型を解決できる／**本番の null-object（`NoOp*`/`Null*`）を保持**しているのに組み立ての解決先はそれではない | #918（ファクトリが `null` を渡す）・#919 のファクトリ版 |
| **W3 偽物の陰の本物** | 組み立てがポート P を本番の実装 R へ結線し、テストアセンブリが P を実装する偽物を持つのに、**テストアセンブリが R を 1 度も参照しない**（メタデータの TypeRef） | #929 の前提（本物を解決・参照する試験が無い） |
| **W0 組めない** | 本番型の登録・ハンドラを組み立てから作れない | unknown ≠ none（組めなかったものを「違反なし」に数えない） |

- **allowlist はラチェット**（`UnwiredDiRegistrationTests` と同じ規律）: 1 行ごとに理由・外す条件・issue 番号。実体を失った行は赤。
- **母集団の空振り検知**: 検査した根・辿ったフィールドの件数が下限を下回れば赤（0 件で無条件に緑にしない）。
- **ガードの欠落を止める**: Architecture.Tests が `backend/Services/*/Tests` のすべてにガード試験があることを表明する。
- **構成の分岐**: 発注先の分岐がある OrderExecution は paper / moomoo の両構成で走らせる（#918 は moomoo 構成でしか現れない）。

### 射程外（フォローアップ）

- **W3 は本物の実装の「中身の変異」を検出しない**（参照があれば通る）。#929 の変異そのものを殺すのは振る舞いの試験であり、
  W3 はその試験が**存在しない状態**を赤にする。
- サービス間 DTO の契約試験（#940 / #943 の形）は別 issue へ切り出す。

## 母集合（着手前に引いたもの）

- **ガードを置くサービス**: `backend/Services/*/Tests/*.Tests.csproj` を実ツリーから列挙した 12 本（11 サービス＋OpenD 認証ゲートウェイ）。
  除外なし。BFF エンドポイント（`backend/Bff`）は `Program` を持たない（platform の BFF 合成点へ組み込まれる）ため対象外。
- **構成の分岐**: 事故が起きた分岐（発注先 paper / moomoo）と、稼働中の経路B が既定と違う値にしている分岐
  （`values-local.yaml` の `MarketData__EnableMarkToMarket=true`）の 2 つ。他の分岐（LLM・KB・市場データの接続先）は
  既存の `*SelectionTests` が持つため本作業では足さない（IADR-0397「残る制約」）。
- **事故**: PR #919 / #929 / #918 / #940（issue #947 の表）。#940 の形（サービス間 DTO）は #952 へ切り出した。

## 受け入れ基準

- [x] develop に対してガードを走らせた所見の件数と、本物／偽陽性の内訳を IADR-0397 に記録する（15 件＝本物の穴 11・設計による正当な不在 4。設計中の偽陽性 8 件は規則を直して除いた）
- [x] #919 の変異（登録行の削除）でガードが赤になる（W1＋W2）
- [x] #929 の是正前の状態（本物を参照する試験が無い）でガードが赤になる（W3。現在のツリーの変異だけでは是正の試験が赤・ガードは緑）
- [x] #918 の変異（`null` を渡す）でガードが赤になる（moomoo 構成の W2）
- [x] allowlist は 1 行ごとに理由と issue 番号を持つ（4 行。検査で強制）
- [x] 全サービスがガードを持つことを Architecture.Tests が表明する（1 本外すと赤になることを実測）
- [x] CI 時間への影響を測り、記録する（IADR-0397「CI 時間への影響」）
- [x] ワークフローの起動条件・必須チェック名が変わらない（ワークフロー無変更。`node scripts/check-workflow-job-refs.js` OK）

## テスト方針

- エンジン自体の否定形／肯定形の試験（小さな DI で W0〜W3 がそれぞれ赤・緑になること）を TestSupport のテストに置く。
- 各サービスのガードは本番構成で 1 本（＋構成分岐ぶん）。
- 変異の再注入は作業ツリーで一時的に行い、結果（赤になった試験名と所見の文字列）を IADR と PR 本文に残す。

## 計画書との差異

- 差異: なし（メタ作業）

## 未決事項

- なし
