---
title: IADR-0397 本番の組み立て（Program.cs）を組んで配線の抜けを機械的に止める組み立てガードを全サービスに置く
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0163, IADR-0335, IADR-0374, IADR-0380, IADR-0370, IADR-0390, IADR-0066, IADR-0067, IADR-0153, IADR-0169]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs: []
---

# IADR-0397: 本番の組み立てを組んで、配線の抜けを機械的に止める（組み立てガード）

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 [#947](https://github.com/endazon/ai-stock-trading/issues/947)。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: [#947](https://github.com/endazon/ai-stock-trading/issues/947)。フォローアップ: [#952](https://github.com/endazon/ai-stock-trading/issues/952)（サービス間 DTO の契約。[#943](https://github.com/endazon/ai-stock-trading/issues/943) を含む）
- 関連する実装仕様書: [20260925_947_composition-wiring-guard](../specs/20260925_947_composition-wiring-guard.md)
- 関連 IADR: [IADR-0163](IADR-0163_allow-list-and-required-dependency-scope.md) 決定2（原則の散文。本 IADR はその機械化）、
  [IADR-0335](IADR-0335_unwired-di-registration-detection.md)（DI 登録の未利用検出。「本番から呼ばれない型」を止める。本 IADR は「呼ばれるが中身が抜ける」側）、
  [IADR-0374](IADR-0374_decision-skip-reasons-and-first-alert-rule.md)（PR #919）、[IADR-0380](IADR-0380_market-session-schedule-and-closed-protection-gap.md)（PR #929）、
  [IADR-0370](IADR-0370_drift-adoption-protective-stop-followup.md)（PR #918）、[IADR-0390](IADR-0390_working-entries-in-decision-input.md)（PR #940）。
- 計画 ID: NFR（メタ作業＝検査器。製品の FR には当たらない）。

## コンテキストと課題

「**配線が消えても全テストが緑のまま**」の形が、2026-09-23〜24 の 1 週間に監査で **4 回**実測された。
リポジトリの規約「検査器・規約の追加は同型の事故が 2 回起きたら」を超えている。

| PR | 生存した変異 | 緑のまま通った理由 | 当時の是正 |
| --- | --- | --- | --- |
| #919 | 判断サービスの `Program.cs` から `AddSingleton<IDecisionSkipReporter, MetricsDecisionSkipReporter>()` を消す | `TradeDecisionAppService` が省略可能引数（既定 NoOp）で受け、DI は黙って既定値で組む。**716 件すべて緑** | 個別の `DecisionSkipReporterRegistrationTests` |
| #929 | 本物の `MarketHoursSchedule.IsOpen` を常に真／偽にする | 巡回の試験は `FakeSchedule` だけを使い、本物を解決・参照する試験が無い。**MarketMonitor 153 件すべて緑** | 個別の `MarketScheduleWiringTests` |
| #918 | 発注執行の `Program.cs` が `ProtectiveStopDriftAdopter` へ建玉照会の代わりに `null` を渡す | ハンドラの試験は自前の DI を組み（コメントは「本番と同じ DI の組み立て」と主張）、単体試験は照会を自分で渡す。**755 件すべて緑** | 個別の `ProtectiveStopDriftAdopterCompositionTests` |
| #940 | サービス間 DTO `WorkingEntryOrderView` の項目名を変える | アダプタが自前の DTO を読み、契約試験が無い。**両サービスとも緑** | （#943 が同じ欠落を `/open-positions` について追う） |

共通の根は、**個々の試験が部品を自分で組む**（`new` する・自前の DI を組む・偽物を差す）ことにある。
本番の組み立て（`Program.cs`）から依存が抜けても、部品の試験は 1 件も赤くならない。
IADR-0163 決定2 は原則を散文で持つが、機械で守らせる仕組みは無く、事故のたびに**個別の登録試験を後追いで足していた**。

## 決定

### 決定1: 部品ではなく「本番の組み立てが実際に作った実体」を検査する共有エンジンを置く

`backend/TestSupport/AiStockTrading.TestSupport.Composition`（テスト専用）に検査エンジン `CompositionWiringGuard` を置く。
入口 `WebApplicationFactory<Program>.InspectComposition(testAssembly)` は、各サービスの**既存のファクトリ**
（外界＝DB・RabbitMQ・ブローカーの伝送だけを差し替えたもの）から本番の `Program.cs` をそのまま組み、
登録の写し（`IServiceCollection`）とルートの `IServiceProvider` をエンジンへ渡す。

- 対象の型（「本番型」）は、サービス自身のアセンブリと `AiStockTrading.Shared.*`。
- **自前の常駐（本番アセンブリの `IHostedService`）は起動させない**。外界を巡回させず、検査の結果を巡回のタイミングから
  独立させるためである。代わりに写しの登録どおり（型なら `ActivatorUtilities`、ファクトリならその関数）に
  組み立てのコンテナから作って辿る。
- Wolverine のハンドラは DI に登録されない（Wolverine がコンテナから構築する）ため、`HandlerGraph`
  （`ICodeFileCollection` として登録される）からハンドラ型を読み、`ActivatorUtilities` で組み立てのコンテナから構築する。

### 決定2: 規則は 4 つ。それぞれが「その週の事故の形」に対応する

| 規則 | 形 | 捕まえる事故 |
| --- | --- | --- |
| **W0 組めない** | 本番型の登録・常駐・ハンドラを組み立てから作れない | **unknown ≠ none**（組めなかったものを「違反なし」に数えない） |
| **W1 省略可能依存の未解決** | 型で組む登録・常駐・ハンドラについて、DI が選ぶ構築子（満たせる引数が最も多い公開構築子）の**既定値つき参照型引数**を、組み立てが解決できない | #919 |
| **W2 渡し忘れ** | 組み立てが作った実体を本番型の範囲で辿り（深さ 12・循環は 1 度だけ）、**依存フィールド**（宣言型の構築子が同じ型の引数を持つフィールド。主構築子の捕獲 `<x>P` を含む）が ① `null` なのに組み立てはその型を解決できる、② 本番の null-object（`NoOp*` / `Noop*` / `Null*`）を保持するのに組み立ての解決先はそれではない | #918（①）・#919 のファクトリ版（②） |
| **W3 偽物の陰の本物** | 組み立てがポート P を本番の実装 R へ結線し、テストアセンブリが P を実装する偽物を持つのに、**テストアセンブリが R を 1 度も参照しない**（メタデータの TypeRef 表。`typeof`・`new`・総称引数・`BeOfType<R>` で 1 行できる。`nameof` は数えない） | #929 の前提 |

W2 の「依存フィールド」の絞り込みは、状態のフィールド（キャッシュ・直近値）を依存と取り違えないためである。
W2 ② で、宣言型そのものがそのポートの解決先である（＝装飾である）とき、内側の null-object は**組み立てが包むと決めた実体**
なので比べない（「実測」の「設計中に除いた偽陽性」の 1 件目）。
W3 で R が共有物（`AiStockTrading.Shared.*`）のときは、**その共有物自身の試験プロジェクト**
（`backend/Shared/<アセンブリ名>.Tests` のソース。行コメントを除いて型名を識別子として探す）の参照でも満たす。
メタデータでなくソースを読むのは、サービス単体の試験実行では兄弟の試験 DLL がビルドされていないためである。

### 決定3: 構成の分岐はガードを分けて走らせ、片方の「正当な不在」はもう片方で結線を検査する

同じ `Program.cs` でも構成で組み立てが変わる。**#918 は moomoo 構成でしか現れない**（内蔵 paper では建玉照会が
登録されず、`null` が正しい）。したがって分岐のあるサービスは両方の構成でガードを走らせる。

- 発注執行: **内蔵 paper** と **moomoo（SIMULATE）**。moomoo は伝送の境界（`IMoomooTradeClient`）だけを
  `TransportStub`（呼ばれたら投げる空の実装）へ差し替え、`MoomooBrokerAdapter` とそこから `Program.cs` が変換する
  建玉照会・口座種別・稼働観測は本物のまま組ませる。
- リスク管理: **既定（時価評価 無効・values.yaml）** と **時価評価 有効**（稼働中の経路B・values-local.yaml）。

### 決定4: allowlist はラチェットであり、1 行ごとに理由・外す条件・issue 番号を持つ

各ガードの allowlist は所見の鍵（`W1 型(引数)` / `W2 型.フィールド` / `W3 ポート -> 実装`）から理由への辞書で、
**所見が消えた行も赤**（外し忘れを残さない）、**理由に `#NNN` が無い行も赤**にする
（`UnwiredDiRegistrationTests` の `KnownUnwired` と同じ規律）。現在の allowlist は **4 行**で、
**すべて「構成の分岐・設計による正当な不在」**である。うち 3 行は、もう片方の構成のガードが同じ依存の結線を検査している
（残る 1 行は既定値そのものが本番値である設計。「実測」の表）。

### 決定5: 母集団の空振りと、ガードの欠落も機械で止める

- 各ガードは、組み立てから作った本番型の根・辿った依存フィールド・構築したハンドラの件数に**下限**を持つ
  （実測の半分。実測値は各ガードのコメントに残す）。検査が 0 件で無条件に緑にならない。
- `Program` がサービスごとに別の型で、1 つのテストプロジェクトから 12 個の組み立てを組めないため、ガードは
  各サービスのテストに置く。したがって**新しいサービスはガードを持たずに増え得る**。
  `AiStockTrading.Architecture.Tests` の `CompositionWiringGuardPresenceTests` が、実ツリーの
  `backend/Services/*/Tests/*.Tests.csproj` すべてに `InspectComposition(` と `AssertNoUnexpectedFindings(` の
  呼び出しがあることを表明する（一覧を手で書かない）。

### 決定6: 所見への対処

| 所見 | 直し方（優先順） |
| --- | --- |
| W0 | 組み立てを直す。伝送の境界で差し替えるべき外界ならファクトリで差し替える |
| W1 / W2 | 不在が統制の無効を意味する依存なら**必須引数にする**（IADR-0163 決定2。コンパイルエラーへ落とす）／登録を足す／構成の分岐による正当な不在なら allowlist（もう片方の構成のガードが結線を検査していることを理由に書く） |
| W3 | 本物を通す試験を足す（**振る舞いを表明する**。型の参照だけの試験で通すと、規則は満たしても変異は殺せない） |

## 実測

### develop（`3d9b91c2`）での所見

最終の規則で 12 サービス・14 構成を走らせた所見は **15 件**。**偽陽性は 0 件**（設計中に除いた偽陽性は下の表）。

| 分類 | 件数 | 内訳 | 対処（本 PR） |
| --- | --- | --- | --- |
| **本物の穴（W3）・実質的** | 6 | 通知 `HttpGoodFaithViolationController`（GFV 停止の解除。兄弟の `HttpPauseController` 等には試験があるのに、これだけ 1 本も無い）／報告書 `UnsuppliedBuyInInferenceRecordSource`・`UnsuppliedFxSourceStatusSource`・`UnsuppliedPeriodDriftAdoptionSource`（「未供給は null であって空ではない」が存在理由。空列へ変えても全部緑のまま報告書だけが嘘を書く）／判断 `PublishingScreeningReductionReporter`（縮退の記録の発行）／判断 `NoStage0DecisionRecordSink`（保存できなかった＝false） | 振る舞いを表明する試験を足した |
| **本物の穴（W3）・軽微** | 5 | `SystemClock` × 4（構成・収集・市場監視・報告書）／判断 `NoOpDailyPolicyUnconfirmedNotifier` | 試験を足した（UTC の現在時刻を返す／例外を出さずに完了する） |
| **構成の分岐による正当な不在** | 4 | 判断 W1 `TradeDecisionAppService(retrievalSourcePolicy)`（#252 / IADR-0169 決定2。既定値そのものが本番値）／発注執行 paper W1 `BrokerAvailabilityProbeService(accountSource)`（#375 / IADR-0153 決定2）／発注執行 moomoo W1 `OrderAmendmentService(amendmentBroker)`（#154 / IADR-0067 決定3）／リスク管理 既定 W2 `LedgerPortfolioStateProvider.currentPrices`（#81 / IADR-0066） | allowlist（理由・外す条件・issue 番号つき）。retrievalSourcePolicy 以外は反対側の構成のガードが結線を検査する |
| 本物の配線の消失（W0 / W1 / W2） | 0 | —— 3 件の事故はいずれも個別の是正で塞がれていた | —— |

**設計中に除いた偽陽性（8 件）**:
W2 ② が装飾の内側を「渡し忘れ」と読んだもの 1 件（報告書 `LastKnownQuoteSource.inner` が組み立ての選んだ `NoOpMarketDataSource` を包む）→ 装飾の除外を足した。
W3 が共有物の本物を「未参照」と読んだもの 7 件（`NoOpMarketDataSource` × 3・`NoOpKnowledgeBaseWriter` × 2・`NoOpKnowledgeBaseSearch`・`LastKnownQuoteSource`。いずれも `backend/Shared/*.Tests` が試験している）→ 共有物は兄弟の試験プロジェクトの参照でも満たす形にした。

### 事故の変異の再注入（本 PR のブランチで実測）

| 事故 | 注入 | ガードの結果 |
| --- | --- | --- |
| #919 | 判断 `Program.cs` の `AddSingleton<IDecisionSkipReporter, MetricsDecisionSkipReporter>();` を消す | **赤**: `W1 …TradeDecisionAppService(skipReporter)` と `W2 …TradeDecisionAppService._skipReporter`（null-object `NoOpDecisionSkipReporter` を保持するが組み立ては登録していない） |
| #918 | 発注執行 `Program.cs` の `ProtectiveStopDriftAdopter` 構築で `sp.GetService<IBrokerPositionSource>()` を `null` にする | **moomoo 構成が赤**: `W2 …ProtectiveStopDriftAdopter.positions`（null だが組み立ては `MoomooBrokerAdapter` を解決できる）。paper 構成は緑（null が正しい） |
| #929 | ① 現在のツリーで `MarketHoursSchedule.IsOpen` を常に真にする | ガードは**緑**、是正の試験 T-10-724 / T-10-725 が赤（**ガードは中身の変異を見ない**。下の「残る制約」） |
| #929 | ② 監査前の状態を再現する（是正の `MarketScheduleWiringTests.cs` を外す）＋ ① の変異 | **ガードだけが赤**（156 件緑・1 件赤）: `W3 …IMarketSchedule -> …MarketHoursSchedule`（偽物 `FakeSchedule` の陰で本物を 1 度も参照しない）。是正前のコミット `93edcd9d` でも本物を参照する試験は 0 本だった（`git grep`） |

いずれも注入は作業ツリーで一時的に行い、戻したことを `git status` で確かめた。

### CI 時間への影響

- 追加した試験は、ガード 14 件（12 サービス・発注執行とリスク管理は 2 構成）、エンジンの否定形／肯定形 16 件、
  ガードの存在検査 2 件、所見の是正の試験 22 件（GFV 解除 11・未供給 3 種 4・判断の本物 3・`SystemClock` 4）。
- ガード 1 件の所要は、同じプロセスで他の試験がホストを温めた後で **0.1〜1 秒**（リスク管理の 2 件を他の組み立て試験と
  同時に回した実測: 91 ms / 1 s）。そのプロセスで最初にホストを組む場合は 0.5〜8 秒（最初のホスト起動の費用で、
  スイートは他の組み立て試験で既に払っている）。エンジンの試験プロジェクトは 0.4 秒。
- テストの分配（`scripts/list-test-projects.js` の LPT）は新しい試験プロジェクト 1 本をシャード 4 へ入れた。
  必須チェック名・ワークフローの起動条件は変えていない（ワークフローを編集していない）。

## 検討した代替案

| 代替案 | 却下理由 |
| --- | --- |
| (b) 本番型の「既定値 NoOp の省略可能引数」をソース／リフレクションで走査し、登録試験が無ければ赤 | 省略可能引数は IADR-0163 決定2 が正当と認める形であり、**型だけを見ても組み立てで解決されるかは分からない**（#919 は「登録が在るか」の問題）。ファクトリ登録が `null` を渡す #918 は、構築子の形からは見えない |
| 事故のたびに個別の登録試験を足す（従来） | 書き忘れが起こる。4 件とも**事故の後で**足されている |
| 全ポートの実装型を 1 件ずつ固定する登録試験を機械生成する | 生成物は組み立ての現状を写すだけで、「抜けた」ことを判定する規則を持たない |
| 構成（values-local.yaml）を解析して本番構成そのものを組む | 本番構成の常駐は外界へ出る。構成の分岐のうち事故が起きた 2 つ（発注先・時価評価）を明示の構成として持つほうが、決定的で速い。残りの分岐は下の「残る制約」 |
| 全依存を必須引数へ改める | IADR-0163 決定2 が「構成しないことが正当な依存は省略可能のまま」と定めている。実測の 4 行の allowlist がまさにその形 |

## 残る制約

- **W3 は本物の中身の変異を検出しない。** 参照があれば通る。#929 の変異そのものを殺すのは振る舞いの試験であり、
  W3 はその試験が**存在しない状態**を赤にする。型の参照だけの試験（`BeOfType<R>`）でも W3 は満たされる
  （既存の `TradeHistoryWiringTests` 等はこの形）。
- **null-object の判定は命名規約**（`NoOp*` / `Noop*` / `Null*`）に依る。規約に従わない内部の代替（`?? new InMemoryX()` 等）は
  W2 ② では見えない（型で組む登録なら W1 が見る）。
- **値型・文字列・デリゲートの依存は見ない**（オプションの数値・時計のデリゲート等）。
- **検査する構成は明示したものだけ**（既定＋発注先 moomoo＋時価評価 有効）。他の構成スイッチ（LLM・KB・市場データの
  接続先など）を有効にした組み立ては、各サービスの既存の選択試験（`*SelectionTests`）に依る。
- **Wolverine のハンドラは `ActivatorUtilities` で構築する。** Wolverine 自身のコード生成と構築子の選び方が
  異なる場合、その差は見えない。
- **サービス間 DTO の契約（#940 / #943 の形）は射程外**。[#952](https://github.com/endazon/ai-stock-trading/issues/952) で扱う。
- `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim`（README が「de facto な配線」と明記する shim）は本番型に数えていない。
