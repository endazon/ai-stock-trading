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

## ［2026-08-28 追記 / #335・#347］カバレッジ床割れとその回復

### 起きたこと（実測）

本作業の実装コミット 2 本（58 ファイル・3002 行追加）を push した結果、CI の集約ジョブ
`build-and-test` が**カバレッジ床（`coverage-floor.json` の `lineRateFloor` = 0.79）割れ**で落ちた。

```
集めたカバレッジレポート: 51 件
[check-coverage] 除外 41 ファイル・8988 行（うち被覆 0 行）。分母 26990 → 18002 行
[check-coverage] floor 79.00% を下回りました（実測 78.70%）。
[check-coverage] 行カバレッジ 78.70%（14167/18002 行・レポート 51 件）/ floor 79.00%
```

ローカル再現（CI と同じ Release ビルド・`Category!=Integration`・`cov` へ収集・レポート 51 件）は
**78.65%（14159/18002 行）**。CI との差 9 行は測定環境の差であり、床割れの事実は同じである。

🔴 **床は下げていない。** 床は回帰防止のラチェットであり、下げて緑にすることは規約上禁止である
（`coverage-floor.json` の `$comment`・`scripts/check-coverage.js` 冒頭）。**テストの追加だけで回復させた。**

### 被覆が無かった新規コードの特定（カバレッジレポートの実データから）

`cov/**/coverage.cobertura.xml` を `(ファイル, 行番号)` で和集合して未被覆行を数え、
本 PR が触った 39 の非テストファイルへ絞った。上位は次のとおり（被覆前）。

| 未被覆 / 全行 | ファイル | 内容 |
| --- | --- | --- |
| 14 / 14 | `ReportService.Infrastructure/Composable/Adapters/PublishingLlmUsageReporter.cs` | 新規。報告書費用の publish |
| 12 / 12 | `ReportService.Infrastructure/Composable/Adapters/PublishingLlmGovernanceReporter.cs` | 新規。発火の publish |
| 18 / 181 | `AuditService.Application/Services/AuditEntryFactory.cs` | `LlmCostIncurred` / `LlmFallbackFired` / `TradeDecisionSkipped` の 3 写像 |
| 12 / 140 | `AuditService.Infrastructure/Composable/Steps/AuditEventHandlers.cs` | 上記 3 イベントの監査ハンドラ本体 |
| 10 / 118 | `NotificationService.Application/Services/NotificationFormatter.cs` | `LlmFallbackFired` / `TradeDecisionSkipped` の文言 |
| 4 / 36 | `NotificationService.Infrastructure/Composable/Steps/NotificationHandlers.cs` | 同 2 イベントの通知ハンドラ |
| 10 / 190 | `ReportService.Domain/ReportRenderer.cs` | `AppendLlmModelUsage`（報告書メタの描画） |
| 5 / 34 | `CostControlService.Infrastructure/.../EfCostLedger.cs` | `GetMonthlyTotals`（カテゴリ別内訳） |
| 8 / 99 | `ReportService.Infrastructure/.../HttpReportNarrativeDrafter.cs` | 発火記録の best-effort catch・JSON null・本文空 |
| 14 / 158 | `ReportService.Api/Program.cs` | ゲートウェイ設定時の散文生成アダプタの結線 |

**除外して手を付けなかったもの（規則 6）**

| 除外 | 理由 |
| --- | --- |
| `AuditEventHandlers.cs` の残り 68 行（本 PR と無関係な 17 ハンドラ） | 射程外。#335 / #347 が触っていないイベントである |
| `AuditEntryFactory.cs` の残り 11 行（`BrokerAvailabilityObserved` / `BrokerAccountObserved`） | 同上 |
| `NotificationHandlers.cs` の残り 14 行 | 同上 |
| `AiStockTrading.Shared.Contracts/**`（Shared.Infrastructure.Tests のレポート由来・447 行） | **既存の構造的な事象**。同じソースが `<source>` 根の違いで別キーとして数えられ、契約型ほぼ全件が同キーで 0% になっている。本 PR が作ったものではなく、ここを埋めるには**別プロジェクトへ重複テストを置く**しかないため採らない |
| `EfCostLedger` の relational 経路（`pg_advisory_xact_lock`）と `AdvisoryKey` | 実 PostgreSQL が要る。`Category!=Integration` の母集合では実行し得ない |
| `CostControlEndpoints` の `ArgumentException` フィルタ 2 行 | 到達させるには不自然な入力の捏造が要る防御的分岐。**通すためだけのテストになる** |

### 追加したテストと、それが意味を持つ理由

| ファイル | テスト | なぜ意味があるか（カバレッジ稼ぎでない理由） |
| --- | --- | --- |
| `ReportService.Infrastructure.Tests/PublishingLlmReportersTests.cs`（新規 4 本） | `報告書生成の費用は用途と実効モデルを載せて発行する` / `フォールバック先のモデルで応答したらその単価で計上する` / `フォールバック発火は用途と期待_実効モデルを載せて発行する` / `割当外のモデルへ落ちた場合は原因を_Unassigned_として発行する` | 既存の `HttpReportNarrativeDrafterVisibilityTests` は**アダプタが呼ばれること**しか見ていない。**呼ばれた結果がメッセージバスへ出ること**は別の事実であり、publish しない実装（No-op のまま配線）でも前者は緑になる。②通知・③台帳・費用実績はこの publish が唯一の出口である |
| `ReportService.Api.Tests/LlmGovernanceWiringTests.cs`（新規 5 ケース） | `費用計測ポートは発行実装へ結線される` / `割当逸脱の可視化ポートは発行実装へ結線される` / `ゲートウェイ設定時の散文生成アダプタが組み上がる` / `ゲートウェイ未設定または不正_URI_ならプレースホルダへ倒す`（2 値） | 両ポートの**既定は No-op（fail-safe）**である。配線を落としてもアダプタ単体テストは緑のままで、**本番だけが沈黙する**。その差を観測できる唯一の場所が composition root である |
| `ReportService.Domain.Tests/ReportRendererLlmModelUsageTests.cs`（新規 5 本） | 第 1 候補／第 2 候補／割当外／未供給／実効モデル不明の 5 形 | ADR-0017 決定 4-(1) 「月報が第 1 候補で書かれたのか第 2 候補で書かれたのかは判断材料である」は**描画されて初めて満たされる**。写像が正しくても描画で落ちれば読み手に届かない。未供給を「発火なし」と読ませない否定形を含む |
| `AuditService.Application.Tests/AuditEntryFactoryLlmGovernanceTests.cs`（新規 8 本） | 3 イベントの要約・相関（月別）・見送りと発火の相関分離 | 決定 4-(3) の「**当月の**発火回数」は相関が月で分かれて初めて数えられる。見送りと発火を同じ相関へ混ぜると月報の回数が狂う——その分離を固定する |
| `AuditService.Infrastructure.Tests/LlmGovernanceAuditHandlersTests.cs`（新規 4 本） | 発火・見送り・対象外費用の台帳着地／同月の発火は抑止せず件数ぶん残る | 既存 `AuditConsumerCoverageTests` は「ハンドラが**発見される**」までしか見ない。**発見と着地の間**（ハンドラ本体・写像・冪等キー）が抜けても緑になる |
| `NotificationService.Infrastructure.Tests/LlmGovernanceNotificationTests.cs`（新規 2 本） | 発火・見送りの通知（文言＋**重大度**） | 決定 4-(2) は「埋もれない経路」を、決定 2 は「障害ではない」を求める。どちらも `Warning` が正であり、**Info に落とせば沈黙し、Critical に上げれば運用が障害として扱ってフォールバック追加を招く**——決定 2 が最も避けたい結末を固定する |
| `ReportService.Infrastructure.Tests/HttpReportNarrativeDrafterVisibilityTests.cs`（3 本追記） | `発火の記録に失敗しても散文生成は壊さない` / `応答が_JSON_null_ならプレースホルダへ倒しメタ情報も残さない` / `応答本文が空でもモデルを名乗っていればメタ情報は残す` | 可視化は best-effort であり、逆向き（記録できないなら散文も落とす）にすると通知経路の一時障害が月次の方針書を止める。後の 2 本は「モデルを知り得ない縮退」と「本文だけが空」でメタの残り方が変わる対の否定形である |
| `CostControlService.Infrastructure.Tests/EfCostLedgerTests.cs`（3 本追記） | `月次内訳は上限対象外のカテゴリも含めて返す` / `月次内訳は当月の計上だけを集計する` / `計上の無い月の内訳は空になる` | 対象外（`LlmUncapped`）も返すことが `GetMonthlyTotals` の存在理由である（§6.1）。対象内だけを返すと #282 の過少申告がそのまま再発する。空の月に 0 円行を捏造しないことも固定する |

いずれも**既にテストが厚い箇所への重複ではない**（新規経路・未検証の継ぎ目・否定形のみ）。
`ReportService.Infrastructure.Tests.csproj` に `TestSupport.PlatformShim` / `TestSupport.Messaging` の
ProjectReference を追加した（本番と同じ Wolverine 配線でホストを起こすため。
`TradeDecisionService.Infrastructure.Tests` と同型）。

### 回復後の実測

```
[check-coverage] 除外 41 ファイル・8988 行（うち被覆 0 行）。分母 26990 → 18002 行
[check-coverage] 行カバレッジ 79.30%（14276/18002 行・レポート 51 件）/ floor 79.00%
```

**78.65% → 79.30%（+117 行）。** レポート件数は 51 件で不変（テストプロジェクトの増減なし）。
床 79.00% に対し 0.30 ポイント（約 54 行）の余裕を持たせた。

---

## ［2026-08-28 追記 / #335・#347］レビュー指摘の是正 —— 層別 purpose が実行時配線では区別できなかった

### 何が欠陥だったか（実コードで確認した事実）

上記までの実装は、`purpose` を入力に取る統制を 3 つ導入した（IADR-0215 の割当表・IADR-0216 の実効モデル照合・
IADR-0218 の費用の対象範囲判別）。**しかし実行時の配線では、二段判断の 2 つの層を purpose で区別できていなかった。**

| # | 実測した事実 | ファイル |
| --- | --- | --- |
| 1 | `CompleteAsync(prompt, model, cancellationToken)` に **purpose を渡す引数が無い** | `TradeDecisionService.Application/Ports/ILlmCompletionClient.cs` |
| 2 | `ILlmUsageReporter` と `ILlmCompletionClient` を `LlmGateway:Purpose ?? trade-decision` で**固定して 1 インスタンスだけ**登録 | `TradeDecisionService.Api/Program.cs` |
| 3 | 一次・二次の**両方で同じインスタンス**を使い、変えていたのは `PrimaryModel` / `SecondaryModel` という**希望値だけ** | `TradeDecisionService.Application/Services/DecisionOrchestrator.cs` |

**希望値は判定に使われない。** 割当照合（`LlmAssignmentEvaluator.Evaluate(purpose, dto.Model)`）も、
基盤 `LlmRouter` のモデル解決も、費用の計上区分も、**すべて purpose の側で引かれる**。

帰結: `Decision:EnableScreening=true` にすると

- **軽量モデルによる絞り込みという費用統制が成立しない**（一次が本判断と同じモデルへ着地する）
- 一次に軽量モデルが割り当たった場合は、その応答が本判断の割当（sonnet-5 ピン留め・フォールバック禁止）と
  照合されて**必ず「割当外」となり、全サイクルが見送りへ倒れる**
- `LlmCostIncurred` の `Purpose` が両層とも `trade-decision` になり、**層別の内訳が取れない**
  （金額は合うので、症状は内訳の欠落だけ＝台帳を読むまで気づけない）

安全側には倒れるが、**スクリーニング機能そのものが成立しない**。`EnableScreening` の既定 false と
基盤側の `trade-decision-screening` 未登録により潜伏していた。

### 🔴 テストが緑だった理由 —— 「配線では作れない状態」を検証していた

`HttpLlmCompletionClientFallbackBanTests.スクリーニング層もピン以外なら見送る` は緑だった。
しかしそれは **purpose を `HttpLlmCompletionClient` のコンストラクタへ直接渡して**組んだクライアントであり、
**`Program.cs` の配線では決して生成されないインスタンス**である。テストは**実在しない配線**を検証していた。

**アダプタ単体の粒度では、この種の退行は原理的に捕まらない。** 単体テストが緑であることと、
composition root がその状態を作れることは、別の事実である。

### 是正内容

決定と却下した案は [IADR-0212](../adr/IADR-0212_per-call-llm-purpose.md) に記録した。要点のみ:

1. **`purpose` を `CompleteAsync` の引数にした**（`DecisionOrchestrator` が層に応じて渡す）。
   用途キーは ADR-0017 決定1 と 01_architecture-overview が確定させた統制値であり、**構成で可変にしない**。
2. **用途の解決は egress の 1 箇所（`HttpLlmCompletionClient.ResolvePurpose`）へ閉じた**。
   `構成の明示上書き → 呼び出し側の申告 → 安全既定 trade-decision` の順。安全既定を**上限対象内かつ最も厳しい
   統制の側**に置き、用途不明の呼び出しを対象外・統制外へ倒さない。
3. **費用計上の用途も同時に正した。** `LlmUsage` に `Purpose` を**必須の先頭位置引数**として持たせ、
   `PublishingLlmUsageReporter` からコンストラクタ引数の purpose を削除した。省略可にすると載せ忘れが
   静かに通り、#347 の対象範囲判定が誤った区分で積まれる。**計上側が用途を決めてはならない**
   （決めてよいのは用途を知っている egress だけ）。報告書側 `ILlmUsageReporter` と同型になった。
4. **keyed DI は採らなかった。** リポジトリ内の前例 0 件であり、`[FromKeyedServices]` は
   **Application 層へ DI 属性を持ち込む**（層の依存規律に反する）。得られる区別は選択肢 4 と同じである。

### 追加したテスト（再発防止の本体）

| ファイル | テスト | なぜこの退行を捕まえられるか |
| --- | --- | --- |
| **`TradeDecisionService.Api.Tests/LlmPurposeWiringTests.cs`（新規 3 本）** | `二段判断の各層は層別の用途でゲートウェイへ届く` / `層別の用途が届くならスクリーニングは割当外へ倒れず二次へ進む` / `費用計上イベントも層別の用途で発行される` | 🔴 **`Program.cs` の DI 登録を実際に起こし**、`Decision:EnableScreening=true` で 1 サイクル判断させ、**送信された要求本文の `purpose`** と **publish された `LlmCostIncurred.Purpose`** を観測する。スタブは**実ゲートウェイと同じく purpose からモデルを解決して名乗る**（未登録の用途は `DefaultModel` へ無音で落ちる挙動・platform IADR-0102 を模す）ため、用途を取り違えれば**割当外と判定されて一次で打ち切られる**という本番の帰結がそのまま再現される |
| `TradeDecisionService.Application.Tests/DecisionOrchestratorTests.cs`（2 本追記） | `二段_用途ルーティング_一次はスクリーニング用途_二次は本判断用途を渡す` / `スクリーニング無効なら本判断の用途しか渡さない` | 既存の `二段_モデルルーティング_…` はモデル（希望値）しか見ておらず、**判定に使われる側**を検証していなかった。否定形で一次の用途が漏れ出さないことも固定する |
| `TradeDecisionService.Infrastructure.Tests/HttpLlmCompletionClientTests.cs`（2 本追記） | `呼び出しごとの用途が要求と費用計測の両方へ届く`（2 値） / `構成の明示上書きがあるときは呼び出し側の用途より優先する` | 用途の解決順（決定 2）を、**送信の purpose と計測の purpose の両面**で固定する。上書きの否定形は既存デプロイの非破壊を守る |
| `TradeDecisionService.Infrastructure.Tests/PublishingLlmUsageReporterTests.cs`（1 本追記） | `計上イベントには計測ごとの用途がそのまま載る`（2 値） | 計上側が用途を決めていないことを固定する。金額は正しいまま内訳だけが壊れる欠陥なので、**金額のテストでは捕まらない** |

#### 退行検知の実証（変異による確認）

「テストを足した」ではなく「**足したテストが当の退行で赤くなる**」ことを実測した。
`DecisionOrchestrator` の一次呼び出しの用途を `TradeDecisionScreening` → `TradeDecision` に戻した状態で:

```
Failed!  - Failed: 3, Passed: 0, Skipped: 0, Total: 3 - TradeDecisionService.Api.Tests.dll
  Expected handler.Purposes to be equal to {"trade-decision-screening", "trade-decision"},
    but {"trade-decision", "trade-decision"} differs at index 0.
  Expected session.Sent.MessagesOf<LlmCostIncurred>().Select(e => e.Purpose) to be equal to
    {"trade-decision-screening", "trade-decision"}, but {"trade-decision", "trade-decision"} differs at index 0.
```

変異は確認後に戻した（本 PR の成果物は是正後の状態である）。

### 母集合の取り方（規則 6: 引いた結果と除外の理由）

`purpose` 固定・`LlmUsage` の構築・`CompleteAsync` の実装/呼び出しを、**誤りの側の文字列**で全走査した
（`CompleteAsync` / `LlmUsage(` / `new LlmUsage` / `LlmGateway:Purpose` / `LlmPurposes`。拡張子で絞らずパスの除外のみ）。
コンパイラが署名変更で全実装を落とすため、**取りこぼしは構造的に起こらない**（fail-loud）。

| 除外 | 理由 |
| --- | --- |
| `ReportService` 側の `ILlmUsageReporter` / `LlmUsage` / `HttpReportNarrativeDrafter` | **既に同じ形で解決済み**（IADR-0120 決定1 / IADR-0219）。本欠陥は取引判断側にのみ存在する。むしろ本是正はそちらへ**揃えた**ものである |
| `PlaceholderLlmCompletionClient` の本文 | 常に Hold を返す安全既定であり用途を使わない。署名の追随のみ行った |
| 基盤（microservices-platform）の `Llm:Routing:PurposeModels` への `trade-decision-screening` 登録 | **本リポジトリの射程外**。本システムは検知する側に立つ（IADR-0215 と同じ立場） |
