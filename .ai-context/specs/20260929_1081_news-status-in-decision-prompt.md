---
title: ニュースの欠測・未構成を RAG を経由せず取引判断のプロンプトへ明示する（#1081）
type: spec
status: accepted
related_ids: [FR-04, FR-01, FR-02, FR-03, FR-08, UC-01, UC-02, ADR-0020, ADR-0003, ADR-0001, IADR-0022, IADR-0220, IADR-0267, IADR-0313, IADR-0247, IADR-0451, IADR-0079, IADR-0455]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/02_datasource-candidates.md (§ニュース系を必須とする理由と、欠測時の扱い 1「欠測していることを取引判断の文脈に明示して渡す」)
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md (収集情報と RAG を別の入力として扱う)
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md (決定 2・決定 3)
---

# ニュースの欠測・未構成を RAG を経由せず取引判断のプロンプトへ明示する（#1081）

## 背景

#1078 から切り出した 1 件目。計画は「ニュース情報が欠測していることを取引判断の文脈に明示して渡す（欠測を無言で空データとして渡さない）」と定める。
実装（origin/develop `73a79e33`）では:

- 欠測の明示は `DegradationNotice`（出所 `collection-status`）の 1 文書として KB に入り、**RAG を経由しないと判断に届かない**。
- 判断側の RAG は実環境で無効（`KnowledgeBase__Search__BaseUrl` が空で NoOp。#1078）。
- `InformationCollectionAppService.cs:49` は `NewsOutage` のときだけ文書を作る。**ニュース源が未構成**（試行が 0 件）は欠測に数えない（IADR-0220）。
  経路 B（values-local）は現にニュース源が未構成であり、RAG が直っても何も届かない。
- 結果として判断の rationale は「好材料ニュース等の情報が提供されていない」と書き、**取れなかったのか・無かったのか・そもそも集めていないのか**が区別されない。

## 計画上の判定（裁定を要しない範囲）

- 計画 `01_architecture-overview.md` は収集情報と RAG を別々の入力として扱い、`02_datasource-candidates.md` §欠測時の扱い 1 は明示に使う経路を指定していない。
  よって**収集側から判断側へ、RAG と別の経路（`InformationCollected` の追加項目）で状態を運ぶ**ことは計画の内側である（IADR で足りる）。
- 「未構成」を新規建ての停止（ADR-0020 決定 2 の 2）に数えるかは**範囲外**（IADR-0220 を変えない）。本 PR は「未提供（未構成）」と**明示するだけ**で、統制（リスク管理の新規建て停止）は変えない。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC-1 | 欠測判定はニュースの状態を 3 値で持つ: 取得済み（1 つ以上成功）／欠測（試行したものがすべて失敗）／未構成（試行 0 件） | `DegradationEvaluatorTests`（追加） |
| AC-2 | `InformationCollected` に既定 null の任意項目 `NewsStatus`・`NewsStatusValidFor` を足す（追加のみ。旧 3 引数の構築はそのまま通る）。基準ファイルを `UPDATE_EVENT_BASELINE=1` で更新 | `EventBackwardCompatibilityTests`・ビルド |
| AC-3 | 収集の発行は巡回の判定結果のニュースの状態と、現況観測と同じ有効期間を載せる | `CollectionPollingServiceTests`（追加） |
| AC-4 | 取引判断は最新の状態を有効期限つきで保持する。期限切れ・未受信・旧イベント（null）・範囲外の値は「不明」。古い観測で新しい観測を上書きしない。有効期間は受け手で上下限へクランプ | `NewsCollectionStatusStoreTests`（新規） |
| AC-5 | プロンプトに「ニュース: 取得済み／欠測／未提供（未構成）／不明」の行を**無条件で**出す（本判断の定時の節・急変の節、一次スクリーニング）。現在値の有無に依らない（無言で省かない） | `NewsStatusInPromptTests`（新規） |
| AC-6 | 本番の判断フロー（`TradeDecisionAppService`）で、保持した状態が一次と本判断の両方のプロンプトへ届く。定時の起点イベント（`InformationCollectedHandler`）が状態を記録してから判断する | `NewsStatusInPromptTests`・`InformationCollectedConsumerTests`（追加） |
| AC-7 | ニュースの行は縮退の保護分（`ScreeningContextAssembler.NewsStatusReserveChars`）に入る。行の最悪長が予約を超えない | `NewsStatusInPromptTests`・既存の予算試験の追随 |
| AC-8 | 「未構成」で新規建てを止めない（リスク管理の停止集合は不変＝`InformationSourceStateObserved` の内容を変えない） | 既存の `DegradationStateTracker` 試験が通ること＋未構成の巡回で停止カテゴリが空であることの追加試験 |

## 設計（代替案は IADR-0455）

- 共有契約: `Events/NewsCollectionStatus.cs`（`Fetched = 1`・`Outage = 2`・`NotConfigured = 3`。**0 を有効値にしない**＝既定値を「取得済み」と読ませない）。
  `InformationCollected(EventId, ItemCount, CollectedAt, NewsCollectionStatus? NewsStatus = null, TimeSpan? NewsStatusValidFor = null)`。
- 情報収集: `CollectionDegradation.NewsStatus`（init。`DegradationEvaluator` が設定。`None` は null）。`CollectionPollingService` が発行時に載せる。
  有効期間は現況観測（`InformationSourceStateObserved`）と同じ `ObservationValidity`（巡回間隔 × 2・下限 5 分）。
- 取引判断: `NewsCollectionStatusStore`（singleton・プロセス内）。`Record(status, validFor, observedAt)`／`Current(now)`。
  有効期間は 1 分〜2 時間へクランプ（`InMemoryInformationDegradationStore` と同じ上下限）。null の有効期間は下限（1 分）で扱う（旧発行側を長く信じない）。
  `InformationCollectedHandler` が判断の前に記録する。`TradeDecisionAppService` は判断ごとに `Current(clock.UtcNow)` を読み、`Build`／`BuildScreening` へ `news:` で渡す。
- プロンプト: `TradeDecisionPromptBuilder.NewsStatusLine(status)`（公開・試験と縮退の見積りが同じ文字列を測る）。
  定時の節・急変の節の末尾（値動きの行の後）と、一次の対象の節の末尾に無条件で出す。文言は 4 つの const。
- 縮退: `ScreeningContextAssembler.NewsStatusReserveChars`（200）を銘柄ごとの保護分へ足す（700 → 900）。

## 母集合（規則 9・10: 誤りの側で走査した。`origin/develop` `73a79e33`）

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| `git grep -n "new InformationCollected("` | 本番 1（`CollectionPollingService`）・試験（監査・判断の購読・メトリクス） | 本番 1 を追随。試験は 3 引数のまま通る（既定 null） |
| `git grep -n "InformationCollected"`（`*.cs` 以外） | `event-schemas.baseline.json`・`pipeline.json`・`docs/api/events-and-ports.md`・`docs/tech/system-architecture.md`・運用手順 2 件 | 基準ファイルを再生成。`events-and-ports.md` の項目欄を直す。`pipeline.json`（変換段の宣言）・構成図・キュー表はイベント名の粒度で変更不要 |
| `git grep -n "collection-status\|欠測の明示"` | 収集側の文書化（`DegradationNotice`）・判断側の出典許可と保護タグ | 変えない（RAG 経路は残す。本 PR は別経路を足す） |
| `git grep -n "PerSymbolLineChars\|PriceContextReserveChars"` | 本番 1・試験の予算の内訳 3 箇所 | 予算を `+NewsStatusReserveChars` でずらして追随 |
| `git grep -n "InformationCollectedHandler("` と Wolverine の組み立て試験 | 判断の購読試験 2 本（定時・急変） | 両方に `NewsCollectionStatusStore` を登録 |

除外: #1082（`values-local.yaml`）と #1083（`HttpKnowledgeBaseSearch`・`KnowledgeBaseRetrievalContextProvider`・`KnowledgeModels`）のファイルは並行作業のため触らない。
Stage 0 の記録（`Stage0DecisionRecorder`）は as-of のニュースの状態を再構成しないため `news` を渡さない（「不明」と書く＝正直な値）。

## 残余

- 空巡回（収集 0 件）では `InformationCollected` を発行しない（現行の規律）。状態は有効期限で「不明」へ落ちる。
- 「未構成」を新規建ての停止に数えるかは範囲外（IADR-0220 は不変）。
- 判断側の RAG の実環境での有効化は #1083・ニュース源の有効化は #1082。

## 採番

- IADR は起草時 IADR-0453 だったが、#1085（IADR-0453）・#1087（IADR-0454）が先にマージされたため、後からマージする本 PR が最大＋1 の IADR-0455 へ改番した（2026-09-29）。
