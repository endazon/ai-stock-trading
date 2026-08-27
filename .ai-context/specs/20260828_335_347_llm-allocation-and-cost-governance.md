---
title: LLM 割当・フォールバックと費用統制の再実装（#335 / #347）
type: spec
status: draft
related_ids: [FR-04, FR-06, FR-07, FR-16, FR-17, NFR, ADR-0011, ADR-0014, ADR-0015, ADR-0017, IADR-0215, IADR-0216, IADR-0217, IADR-0218, IADR-0219]
author: 実装エージェント（w1b）
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0015_report-monthly-zdr-model.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: LLM 割当・フォールバックと費用統制の再実装（#335 / #347）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 束ねの理由（1 PR に 2 issue）

#335 と #347 は、いずれも **`ILlmUsageReporter` → `LlmCostIncurred` → `LlmCostIncurredHandler` という
同一の継ぎ目**を触る。#335 は「どの用途（purpose）でどのモデルを使ったか」を、#347 は「その費用を
上限に積むか否か」を、**同じイベントの同じフィールド（purpose）で**決める。分けると

- `LlmCostIncurred` の契約変更が 2 PR に分かれて rebase 衝突する（`event-schemas.baseline.json` も同様）
- 「purpose を誰が持つか」の設計判断を 2 回下すことになり、片方が先にマージされた形へ引きずられる

ため、**利用者承認のうえ 1 PR に束ねる**（コミットは #335 / #347 で分ける）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04（AI 判断のガードレール）／FR-06・FR-07（報告書）／FR-16（費用集計）／FR-17（全体前提条件）／非機能要件（費用）
- ユースケース（UC）: UC-01（取引サイクルの事前条件＝日報確定）／UC-03〜05（報告書サイクル）
- 画面（SC）: なし
- 関連 ADR: ADR-0011（ピン留め）／ADR-0014（用途別割当）／ADR-0015（月報 ZDR）／ADR-0017（フォールバック方針）
- 計画書の所在: 隣接クローン `../project-planning` の `projects/ai-stock-trading/` 配下（`07_adr/ADR-0017_llm-fallback-policy.md`・`06_technical/01_architecture-overview.md` §判断の二段化・`06_technical/05_trading-assumptions.md` §6・§6.1）。本リポジトリは planning に依存しないため相対リンクは張らない

## 目的・背景

**本作業はゼロからの再実装ではない。** 11 サービスは実装済みであり、`CostControlService`（`CostGovernor` /
`EfCostLedger` / `LlmCostIncurredHandler` / `AssumptionsCostLimitsProvider`）と `TradeDecisionService`
（`HttpLlmCompletionClient` / `PublishingLlmUsageReporter`）が既に動いている。LlmGateway 本体は基盤側
（`microservices-platform`・参照のみ）にあり、**フォールバックの鎖そのものは基盤の `LlmRouter` が持つ**
（platform `LlmFallbackPolicy` / `LlmRoutingOptions.PurposeFallbackModels`）。

したがって本作業は **issue の要求と現行コードのギャップを差分実装する**ことである。

---

## ギャップ分析（現状 → 要求 → 差分）

### #335 LLM 割当・フォールバック

| # | 要求（issue / ADR） | 現状（実測） | 差分（本作業） |
| --- | --- | --- | --- |
| 335-1 | 用途別の割当表（取引判断＝`claude-sonnet-5` ピン／月報・週報＝`claude-opus-5`／日報＝`claude-sonnet-5`）を持ち、**スナップショットテストで固定**する | **AST 側に割当表が無い。** 割当は基盤 `appsettings.json` の `Llm:Routing:PurposeModels` にのみ存在し、AST は purpose 文字列を送るだけ（`ReportNarrativePurpose` / `LlmGateway:Purpose`） | `AiStockTrading.Shared.Contracts/Llm/LlmAssignments.cs` を新設し、**AST の期待値としての割当表**を持つ。基盤の解決結果を**検証する側**の単一情報源とする（IADR-0215） |
| 335-2 | フォールバック順序（月報・週報 opus→sonnet／日報 sonnet→haiku）を固定する | AST 側に順序の表現が無い。基盤 `PurposeFallbackModels` にも `report-*` / `trade-decision` の鎖は**未登録**（実測。登録済みは `analysis` / `diagram-coding` / `default` / `rag-answer` の 4 件のみ） | 割当表に第 2 候補列を持たせる。**基盤側の鎖の未登録は本リポで直せない**ため残件として記録し環流候補にする |
| 335-3 | **取引判断はフォールバック禁止**。モデル不可なら判断を実行せず**発注しない**（障害ではなく正常な結果） | `HttpLlmCompletionClient` は非 2xx・例外・タイムアウトをすべて `HoldFallback` に倒す。発注は生じないが、**「モデルが使えなかった」と「その他の縮退」が区別されず、記録も通知もされない** | 実効モデル（`dto.Model`）をピンと突き合わせ、不一致なら**判断を実行しない**（Hold）＋ `TradeDecisionSkipped` を記録・通知（IADR-0216） |
| 335-4 | **429 は再試行／400 系はモデル不可**として分岐する | 非 2xx は状態コードを問わず 1 本の `HoldFallback`。429 と 400 が同じ扱い | `LlmFailureClassification`（純関数）を新設し、429=`Retryable` / 400 系=`ModelUnavailable` / それ以外=`Other` に分ける。`ModelUnavailable` のときだけスキップ事象を出す |
| 335-5 | 発火の可視化 3 経路（①報告書メタ ②警告通知 ③月報集計） | **3 経路すべて無い。** 報告書は実効モデルを受け取ってすらいない（`HttpReportNarrativeDrafter` の `CompletionResponse` は `Model` を持つが破棄している） | ①`ReportView.LlmModelUsage` を追加し `ReportRenderer` が節を出す ②`LlmFallbackFired` を発行し `NotificationService` が購読 ③同イベントを監査台帳へ記録し、月次集計の供給元にする（IADR-0217） |
| 335-6 | 二段判断の層別割当（スクリーニング＝`claude-haiku-4-5`／本判断＝`claude-sonnet-5`） | 両段とも同一 purpose `trade-decision` を送る（`DecisionOrchestrationOptions.PrimaryModel/SecondaryModel` は**モデル ID を直接**渡す経路） | 層別 purpose `trade-decision-screening` を定義し割当表に載せる。**計画は「取引判断サービス側で直接モデル ID を指定する経路は採らない」**（01_architecture-overview）ため、purpose での層別化に寄せる |
| 335-7 | `claude-fable-5` はどの経路からも呼ばれない（否定形） | 禁止の表現が無い。`LlmPriceTable` のテストが `claude-fable-5` を単価表に持つ（費用計上の網羅としては正しい） | 割当表に `ForbiddenModels = [claude-fable-5]` を持たせ、**実効モデルが禁止モデルなら常に安全側へ倒す**。否定形テストで固定 |
| 335-8 | 機密区分 `internal` × ZDR 有効 | `LlmGateway:Confidentiality` 既定 `"internal"`（実測。取引判断・報告書とも） | 既定を変えない。割当表テストで `internal` 既定を固定して退行を防ぐ |

### #347 費用統制（CostControlService）

| # | 要求（issue / 05 §6.1） | 現状（実測） | 差分（本作業） |
| --- | --- | --- | --- |
| 347-1 | 自動統制の対象は**月次 LLM 費用上限のみ**。80% で定時サイクル間隔延長・100% で停止 | `CostGovernor.EvaluateLlm` が実装済み（`ThrottleThreshold=0.80` / `HaltThreshold=1.00` / `ThrottledIntervalMultiplier=2`）。`CollectionPollingService` が `/costs/state` を引いて間隔を延ばす | **維持。** 退行防止テストを増やす |
| 347-2 | **対象範囲は取引判断サイクルの LLM 費用のみ**。報告書生成・情報収集は上限の対象外 | 🔴 **構造的な保証が無い。** `LlmCostIncurred(Amount, At)` は purpose を持たず、`LlmCostIncurredHandler` は**すべて** `CostCategory.Llm` として計上する。現在たまたま取引判断しか発行していないだけで、報告書側が計上を始めた瞬間に混入する | `LlmCostIncurred` に `Purpose` / `Model` を追加。`LlmCostScope.IsGoverned(purpose)` で判別し、対象外は新カテゴリ `CostCategory.LlmUncapped` へ計上する（IADR-0218） |
| 347-3 | 対象外の費用は**抑制せず、月報に実績を記載**する（#282 の過少申告の再発防止） | 🔴 **報告書生成の LLM 費用が 1 円も計上されていない。** `HttpReportNarrativeDrafter` に費用計測の呼び出しが無い（`ILlmUsageReporter` 相当を持たない） | ReportService に費用計測ポートを新設し、報告書生成の実績を `LlmCostIncurred(purpose=report-*)` として発行する（IADR-0219）。上限には積まれない（347-2） |
| 347-4 | 月報への費用実績の供給 | `/costs/review`（費用÷資金比率）のみ。**カテゴリ別の内訳を返す口が無い** | `ICostLedger.GetMonthlyTotals(month)` と `GET /costs/usage` を追加し、月報の「当月の LLM 利用実績」の供給元にする（月報側の描画は #338 の担当） |
| 347-5 | 月次データ費用・インフラ費用・月次総費用 20,000 円は目安であり**自動統制対象外** | `CostGovernor` は LLM 上限しか見ない（`MonthlyCostLimits.Llm` のみ）。**すでに正しい** | **維持。** 「他カテゴリを積んでも統制状態が動かない」否定形テストを追加して固定する |
| 347-6 | 月次リセット・上限変更（前提条件バージョン切替）の境界テスト | `CostControlService.MonthKey` による月分離は実装済み。`VersionedCostLimitsTests` が上限変更を扱う | 月跨ぎリセットと 80%/100% 同値境界のテーブルテストを追加 |

### 判断が要った点（IADR へ）

1. **purpose 不明（`null`）の費用はどちら側に積むか** → **上限側（`CostCategory.Llm`）へ倒す**。理由は
   IADR-0122 と同じで、費用統制の危険側は**過小計上**である（上限が構造的に効かなくなる）。計画が挙げる
   危険（報告書の費用が積まれて日報が止まる連鎖）は **purpose が既知の `report-*`** のときにだけ起こり、
   その経路は名指しで除外される。加えて現行の発行元は取引判断のみであり、既存データの解釈も変わらない。
2. **「モデルが使えない」をどう検出するか** → 上流の 400 系だけでなく、**実効モデルがピンと一致しない**
   ことも「使えなかった」と扱う。ADR-0014 §決定3 が守ろうとしているのは「検証したモデルと本番モデルの
   一致」であり、別モデルの応答で発注することは鎖の有無にかかわらず同じ空洞化である。

---

## 対象範囲

- 対象:
  - `AiStockTrading.Shared.Contracts`（割当表・失敗分類・費用対象範囲・イベント 2 種追加・`LlmCostIncurred` 拡張）
  - `TradeDecisionService`（実効モデル検証・失敗分類・スキップの記録／通知）
  - `ReportService`（費用計測の新設・実効モデルの受け取り・報告書メタへの記録）
  - `CostControlService`（対象範囲の判別・カテゴリ分離・内訳の供給）
  - `AuditService`（新イベントの監査記録）／`NotificationService`（フォールバック発火の警告通知）
- 対象外:
  - **基盤（microservices-platform）の LlmGateway 本体**。フォールバックの鎖・`PurposeModels` の登録は基盤側の責務であり、本リポからは変更しない
  - 月報テンプレートへの「当月の LLM 利用実績」節の描画（#338 の担当。本作業は**供給**まで）
  - Stage 0 再検証そのもの（実過去データ源が要る。#296 系）

## 設計

### 1. 割当表（`Shared.Contracts/Llm/`）

```
LlmPurposes            用途キーの定数（trade-decision / trade-decision-screening / report-{monthly,weekly,daily}）
LlmAssignments         用途 → (第1候補, 第2候補以降, フォールバック可否) の表。ForbiddenModels を併せ持つ
LlmAssignmentEvaluator 実効モデルを表と突き合わせて Outcome を返す純関数
LlmFailureClassification HTTP ステータス → Retryable / ModelUnavailable / Other
LlmCostScope           purpose → 月次上限の対象か否か
```

`LlmAssignmentOutcome` は `Primary` / `FallbackFired` / `Unassigned` / `Forbidden` の 4 値。
`DecisionAllowed` は **取引判断系では `Primary` のみ真**、報告書系では `Primary` と `FallbackFired` が真。

### 2. 取引判断（フォールバック禁止の実装点）

`HttpLlmCompletionClient` に次を足す（Hold へ倒す既存のフェイルセーフは不変）。

- 非 2xx を `LlmFailureClassification.Classify` で分類し、`ModelUnavailable` のときだけ
  `ILlmGovernanceReporter.DecisionSkippedAsync` を呼ぶ。429（`Retryable`）では**呼ばない**
- `Sent=true` の応答で実効モデルを評価し、`DecisionAllowed` でなければ本文を**読まずに**破棄して Hold。
  併せて `DecisionSkippedAsync` を呼ぶ
- どちらの経路でも **`ILlmCompletionClient` は Hold JSON を返す**＝発注は構造的に生じない

`ILlmGovernanceReporter` は既存の `ILlmUsageReporter` と同型のポート（既定 No-op・Worker が発行実装を配線）。

### 3. 報告書（可視化 3 経路）

- `IReportNarrativeDrafter` に**既定実装つき**の `DraftAsync` を足し、`ReportNarrativeDraft(Text, ModelUsage)`
  を返す。既定実装は従来の `DraftNarrativeAsync` に委譲するため、**既存の fake・呼び出しは非破壊**
- `HttpReportNarrativeDrafter` が `DraftAsync` を override し、実効モデルの評価結果を載せる
- `ReportView.LlmModelUsage`（`null` = 未供給）を `ReportRenderer` が節として出す（既存の
  `AppendFxSourceStatus` と同じ「null は節ごと出さない」方式＝既存のレンダリング結果は不変）
- フォールバック発火時は `LlmFallbackFired` を発行 → `NotificationService` が警告通知（経路②）、
  `AuditService` が台帳へ記録（経路③の供給元）

### 4. 費用統制

- `CostCategory` に `LlmUncapped` を追加（末尾追加＝既存の永続値と互換）
- `LlmCostIncurredHandler` が `LlmCostScope.IsGoverned(message.Purpose)` でカテゴリを決める
- `CostGovernor.EvaluateLlm` の入力は従来どおり `CostCategory.Llm` の月次累計のみ（変更不要）
- `ICostLedger.GetMonthlyTotals(month)` と `GET /costs/usage`（OwnerOrService）を追加

## 受け入れ基準

### #335

- [ ] 割当表・フォールバック順序がスナップショットテストで固定されている（計画の確定値）
- [ ] 取引判断はモデル不可時に**発注ゼロ・フォールバック呼び出しゼロ**であり、見送りが正常系として記録される
- [ ] 429（再試行）と 400 系（モデル不可）が分岐し、429 ではスキップ事象を出さない
- [ ] 発火 3 経路（報告書メタ・警告通知・監査記録）すべてに記録が残る
- [ ] `claude-fable-5` がどの経路からも呼ばれない（否定形）

### #347

- [ ] 報告書生成・情報収集の費用が上限カウンタに**混入しない**（否定形）
- [ ] 上限超過時にサイクル間隔延長が発動し、**取引停止・報告書生成停止に波及しない**
- [ ] 月次リセット・上限変更（前提条件バージョン切替）の境界が固定されている
- [ ] 対象外費用も台帳へ記録され、月報へ供給できる（`/costs/usage`）

## テスト方針

統制系は**境界値テーブル・プロパティベース・否定形の 3 点セット**（`docs/tests/README.md`）。

| 受け入れ基準 | テスト |
| --- | --- |
| 割当表スナップショット | `LlmAssignmentsTests.割当表は計画の確定値と一致する` |
| 発注ゼロ・フォールバック呼び出しゼロ | `HttpLlmCompletionClientTests.取引判断は実効モデルがピンと違えば発注へ進まない`（Hold JSON・フォールバック候補への再呼び出しが 0 回） |
| 429 と 400 の分岐 | `LlmFailureClassificationTests`（境界値テーブル）＋ `HttpLlmCompletionClientTests` の 2 本 |
| 3 経路 | `HttpReportNarrativeDrafterTests`（メタ）／`NotificationConsumersTests`（通知）／`AuditConsumerCoverageTests`＋`AuditEntryFactoryTests`（台帳） |
| fable-5 非呼び出し | `LlmAssignmentsTests.claude_fable_5_はどの用途にも現れない` ＋ `HttpLlmCompletionClientTests.実効モデルが禁止モデルなら本文を破棄する` |
| 費用非混入 | `LlmCostScopeTests`（テーブル）＋ `LlmCostIncurredConsumerTests.報告書生成の費用は上限カウンタへ積まれない` |
| 波及しない | `CostControlServiceTests.対象外カテゴリの計上は統制状態を動かさない` |
| 月次・上限境界 | `CostGovernorTests`（境界値テーブル）＋ `CostControlServiceTests`（月跨ぎリセット） |

## 母集合の引き直し（`traceability.repo.md` 規則 9・10）

**誤りの側の文字列で走査した。** 軸は 3 本。出力は生のまま読み、`head` で切っていない。

| 軸 | 検索語 | 走査コマンド | ヒット |
| --- | --- | --- | --- |
| 1 | モデル ID | `git grep -Il -e claude-fable-5 -e claude-opus-5 -e claude-sonnet-5 -e claude-haiku-4-5 -e claude-opus-4-8 -- . ':!.ai-context/specs'` | 17 ファイル / 114 行 |
| 2 | 用途キー | `git grep -Iln -e trade-decision -e report-monthly -e report-weekly -e report-daily -- . ':!.ai-context/specs' ':!CHANGELOG.md'` | 51 ファイル |
| 3 | 費用の結線 | `git grep -Iln -e LlmCostIncurred -e CostCategory -e CostThresholdReached -- . ':!.ai-context/specs' ':!CHANGELOG.md'` | 56 ファイル |

軸を 1 本で終わらせていない（規則 5）。軸 2・3 が交わらない箇所として
`backend/Shared/AiStockTrading.Shared.Contracts.Tests/event-schemas.baseline.json` と
`AuditConsumerCoverageTests` が出た —— **どちらも「新イベントを足すと落ちる」側の追随先**であり、
軸 1 だけを見ていたら取りこぼしていた。

### 除外したものと理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `.ai-context/specs/`（作業仕様書） | point-in-time の凍結記録。表記や値を後から直すと当時の記述と食い違う（`traceability.repo.md` の既定除外と同じ） |
| `.ai-context/adr/IADR-0101/0104/0114/0120/0122/0123` 本文 | 同じく凍結記録。**本文プロズを後から書き換えない**（`.ai-context/README.md`）。追随は新 IADR（0215〜0219）側で行う |
| `CHANGELOG.md` | 生成物。コミット件名は書き換えず、必要なら `scripts/changelog-overrides.json` の `remap` で是正する |
| `.github/workflows/claude-*.yml`・`scripts/k8s-local-images.sh` | AI 実行基盤の設定であり、本システムの LLM 割当とは別の関心事（ヒットは実行モデルの指定） |
| `deploy/helm/**`・`docker-compose.yml`・`docs/operations/operations.md` の単価表 | **単価**の設定であって**割当**ではない。`claude-fable-5` の単価行は「呼ばれないモデル」ではなく「未知モデルに倒れたときの最大単価」を成立させる要素であり、消すと過小計上が生まれる（IADR-0122 決定3）。**残す判断**である |

### 導出値は走査ではなく計算し直した（規則 10）

- 本 PR で追加するイベント型は 2 種（`LlmFallbackFired` / `TradeDecisionSkipped`）。
  `EventMessageTypeNameTests` の固定は 33 → **35 件**、`event-schemas.baseline.json` は 2 型が増える。
  いずれも**走査ではなく「既存件数＋2」で計算**し、テストの実行結果で突き合わせる。
- 自己参照の補正（規則 8）: 上表の軸 2・軸 3 の件数は**本仕様書を書く前**に採った値である。本ファイルは
  検索語（`trade-decision` 等）を含むため、コミット後に同じコマンドを叩くと軸 2 は 51 → 52、
  軸 3 は 56 → 57 になる。**「52 行 → 自己参照 1 行を引く → 51 行」**と読むこと。

## 計画書との差異

- 差異: あり
  1. **基盤 `PurposeFallbackModels` に `report-monthly` / `report-weekly` / `report-daily` の鎖が無い**
     （実測: 登録済みは `analysis` / `diagram-coding` / `default` / `rag-answer` の 4 件）。ADR-0017 決定 1 の
     報告書 3 種の鎖が**基盤側で未配備**である。本リポからは直せないため、AST 側は「発火したら記録・通知する」
     受け側だけを実装し、**環流候補**として残す。
  2. **層別 purpose `trade-decision-screening` が基盤 `PurposeModels` に未登録**（MSP#383 は duplicate で
     クローズ済み）。未登録の purpose は基盤で `DefaultModel` へ黙って落ちる（platform IADR-0102 の罠）。
     本作業の実効モデル検証は**その落下を検知して取引判断を止める**側に働くため、統制としては安全側に倒れる。
     ただし**スクリーニングが動かない**という運用上の帰結が残るため、環流候補として記録する。
- どちらも計画の決定に反する実装ではなく、**基盤側の配備待ち**である。ADR-0017 §フォローアップが
  「実体の大半は基盤側である」と明記しているとおりの分担である。

## 未決事項

- 基盤側の鎖・層別 purpose の配備時期（上記 1・2）。配備までは AST の実効モデル検証が
  「割当が効いていない」ことを**取引の停止**という形で顕在化させる。**これは設計上の正常な結果**である
  （ADR-0017 決定 2）が、Stage 0 検証の実施前に配備が要る。
</content>
</invoke>
