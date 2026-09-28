---
title: IADR-0453 ニュースの状態（取得済み／欠測／未構成）を InformationCollected の任意項目で運び、取引判断が有効期限つきで保持してプロンプトへ無条件で明示する（RAG を経由しない）
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-01, FR-02, FR-03, FR-08, UC-01, UC-02, ADR-0020, ADR-0003, ADR-0001, IADR-0022, IADR-0079, IADR-0220, IADR-0247, IADR-0267, IADR-0313, IADR-0451]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/02_datasource-candidates.md
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
---

# IADR-0453: ニュースの状態を RAG を経由せず取引判断のプロンプトへ明示する（#1081）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-29
- 決定者: Claude Code（実装）。計画の内側で経路を選ぶ実装判断であり、裁定は要らない（下記「計画上の位置づけ」）

## 起点・関連

- 関連する計画書 ID: FR-04（AI 判断）・FR-01（収集）・ADR-0020 決定 2（ニュース系の欠測は「欠測していることを取引判断の文脈に明示して渡す」）・ADR-0003（判断入力の限定）。
  計画 `06_technical/02_datasource-candidates.md` §ニュース系を必須とする理由と、欠測時の扱い 1。
- 関連する実装仕様書: [`.ai-context/specs/20260929_1081_news-status-in-decision-prompt.md`](../specs/20260929_1081_news-status-in-decision-prompt.md)
- 前提: [IADR-0220](IADR-0220_source-tier-catalog-and-degradation-evaluator.md)（未構成は欠測に数えない。**本 IADR は変えない**）、
  [IADR-0267](IADR-0267_information-degradation-state-heartbeat-and-fail-closed.md)（現況観測の有効期間と受け手のクランプ）、
  [IADR-0451](IADR-0451_scheduled-decision-intraday-price-context.md)（値動きの行と「出来高: 未提供」の明示・縮退の保護分の予約）、
  [IADR-0313](IADR-0313_screening-context-budget-default.md)／[IADR-0247](IADR-0247_screening-context-degradation.md)（縮退）、[IADR-0079](IADR-0079_event-backward-compat-contract-test.md)（契約は追加のみ）
- 関連 issue: #1081（起点）・#1078（親。判断側の RAG が実環境で無効）・#1082（経路 B のニュース源の有効化）・#1083（判断側の KB 検索）

## コンテキストと課題

欠測の明示は収集側が KB に書く `collection-status` 文書だけであり、判断へは RAG 経由でしか届かない。判断側の RAG は実環境で無効（#1078）で、
しかも**ニュース源が未構成のときは文書そのものが作られない**（IADR-0220。欠測ではないため）。稼働 PoC の rationale は「好材料ニュース等の情報が提供されていない」と書き、
**取れなかった（欠測）／無かった／集めていない（未構成）**が判断の文脈で区別されていなかった。

## 計画上の位置づけ

計画 `01_architecture-overview.md` は収集情報と RAG を別々の入力として扱い、`02_datasource-candidates.md` §欠測時の扱い 1 は明示の経路を指定しない。
収集側から判断側へ RAG と別の経路で状態を運ぶことは計画の内側である。「未構成」を新規建ての停止（ADR-0020 決定 2 の 2）に数えるかは範囲外とし、IADR-0220 は変えない。

## 検討した選択肢

### 運び方

| 案 | 評価 |
| --- | --- |
| **A（採用）: `InformationCollected` に既定 null の任意項目（状態・有効期間）を足す** | 定時サイクルの起点そのものが状態を運ぶ。追加のみ（IADR-0079）で旧イベント・旧購読者はそのまま動く。新しいイベント・キュー・監査ハンドラが要らない |
| B: 新イベント（例: `NewsCollectionStatusObserved`）を毎巡回出す | 空巡回でも鮮度を保てるが、全イベント監査の規約で監査ハンドラ・写像・標本が増え、判断側の購読キューも増える。空巡回では判断サイクルが起きないため得る鮮度の利点が小さい |
| C: 既存の `InformationSourceStateObserved`（現況観測）を判断も購読する | 載るのは「新規建てを止めるカテゴリ」だけ（IADR-0249 決定 1 の規律）であり、**未構成と取得済みを区別できない**。区別を載せると受け手（リスク管理）が停止範囲を再解釈する余地を作る |
| D: 未構成でも `collection-status` 文書を KB に書く | RAG が無効な限り届かない（本件の原因そのもの） |

### 型

状態は共有契約の列挙 `NewsCollectionStatus`（`Fetched = 1`・`Outage = 2`・`NotConfigured = 3`）とする。**0 を有効値にしない** —— 既定値・範囲外の値を受け手が「取得済み」と読まないためである（受け手は未定義の値を「不明」とする）。

## 決定

### 決定 1 — 収集側: 3 値の判定と発行

`DegradationEvaluator` がニュース系の状態を 3 値で決める（試行 0 件＝未構成、試行したものがすべて失敗＝欠測、1 つ以上成功＝取得済み）。`CollectionDegradation.NewsStatus`（init。評価器を通らない `None` は null）。
`CollectionPollingService` は `InformationCollected(…, NewsStatus, NewsStatusValidFor)` を発行する。有効期間は現況観測と同じ `ObservationValidity`（巡回間隔 × 2・下限 5 分）。
未構成でも `NewsOutage` は false のままで、`InformationSourceStateObserved` の停止集合も変わらない（新規建ては止めない）。

### 決定 2 — 判断側: 有効期限つきの保持（不明が既定）

`NewsCollectionStatusStore`（singleton・プロセス内・永続化しない）。`InformationCollectedHandler` が**判断の前に** `Record(status, validFor, CollectedAt)` を呼ぶ。
`Current(now)` は未受信・期限切れ・null（旧イベント）・範囲外の値で null（＝不明）を返す。有効期間は 1 分〜2 時間へクランプ（`InMemoryInformationDegradationStore` と同じ上下限）、宣言が無ければ下限。
古い観測（再配送・順序の入れ替わり）は無視する。**null の観測も最新の観測として前の値を上書きする**（「いま不明」を「前回の値」にすり替えない）。

### 決定 3 — プロンプト: 4 状態を無条件で明示

`TradeDecisionAppService` は判断ごとに `Current(clock.UtcNow)` を 1 回読み、本判断（`Build`）と一次（`BuildScreening`）の両方へ同じ値を渡す（定時・急変とも）。
`TradeDecisionPromptBuilder.NewsStatusLine` がトリガーの節（定時・急変）の末尾と一次の対象の節の末尾に、**現在値の有無に依らず**1 行を出す:
「ニュース: 取得済み／欠測／未提供（未構成）／不明」＋「材料となるニュースが無い」とは扱わない旨（欠測は加えて、裏取りできない急シグナルでの新規建てを行わないこと・手仕舞いと損切りは止まっていないこと）。
Stage 0 の記録は as-of のニュースの状態を再構成しないため渡さない（「不明」）。

### 決定 4 — 縮退の保護分

ニュースの行は欠測の明示であり**保護分**とする。`ScreeningContextAssembler.NewsStatusReserveChars`（200。4 状態の最長を試験で固定）を銘柄ごとの保護分へ足す（700 → 900 文字）。
行そのものは参考情報ではないため縮退の削減対象にならず、予約で予算の見積りだけを合わせる（値動きの行と同じ作法・IADR-0451 決定 4）。

## 理由

- 案 A は定時サイクルの起点に状態を相乗りさせ、契約の追加だけで RAG の有効・無効に依らず判断へ届く。
- 「不明」を既定にし、期限切れで最後の値を信じ続けないことで、健全な値（取得済み）への誤った倒れを防ぐ。

## 結果

- 良い影響: RAG が無効・ニュース源が未構成の構成でも、判断は「ニュースが未提供（未構成）」「欠測」を区別して読む。
- 悪い影響・トレードオフ: すべての判断プロンプトに 1 行増える（最長 200 文字未満）。銘柄ごとの保護分の見積りが 200 文字増え、同じ予算では縮退がわずかに早く起きる。
- 残余:
  - 空巡回（収集 0 件）では `InformationCollected` を発行しない（現行の規律）。その間は状態が有効期限で「不明」へ落ちる。
  - 急変の判断は、最後の定時の起点イベントから有効期間内でなければ「不明」と書く。
  - 「未構成」を新規建ての停止に数えるかは範囲外（IADR-0220 不変）。判断側の RAG の有効化は #1083、経路 B のニュース源は #1082。
- フォローアップ: なし（本 PR で完結）。

## 試験

| 観点 | 試験 |
| --- | --- |
| 3 値の判定（1 つでも成功＝取得済み・全失敗＝欠測・試行 0＝未構成で欠測に数えない）・`None` は null | `InformationCollectionService.Tests/Domain/DegradationEvaluatorTests.cs` |
| 発行が状態と有効期間（現況観測と同値）を運ぶ・未構成で停止集合が空 | `CollectionPollingServiceTests.InformationCollected_はニュースの状態と有効期間を運ぶ` |
| 保持: 未受信・期限切れ・旧イベント null・範囲外は不明、古い観測は無視、クランプ | `TradeDecisionService.Tests/Features/TradeDecision/NewsCollectionStatusStoreTests.cs` |
| 文言 4 状態・null と範囲外は不明・最長が予約内・定時／急変／一次に無条件・判断サービスで一次と本判断の両方（縮退の予算 500 でも）・期限切れは不明 | `NewsStatusInPromptTests.cs` |
| 定時の起点イベントの状態が判断のプロンプトへ届く・旧イベントでも判断が動く | `InformationCollectedConsumerTests` |
| 本番の組み立てで singleton の同じ実体を判断サービスが保持 | `NewsCollectionStatusStoreRegistrationTests.cs` |
| 契約（追加のみ） | `event-schemas.baseline.json`（`UPDATE_EVENT_BASELINE=1` で再生成） |

## 関連

- Supersedes: なし
- Superseded by: なし
