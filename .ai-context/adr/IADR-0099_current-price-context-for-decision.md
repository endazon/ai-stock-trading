---
title: IADR-0099 取引判断へ現在値を供給し、発注の参照価格を権威ある現在値へアンカリングする（既定 no-op・fail-safe）
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-04, FR-10, FR-16, UC-01, UC-02, ADR-0003, ADR-0004]
author: endazon (with Claude Code)
created: 2026-07-24
updated: 2026-07-24
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
---

# IADR-0099: 取引判断へ現在値を供給し、発注の参照価格を権威ある現在値へアンカリングする（既定 no-op・fail-safe）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-24
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-02（取引サイクル・判断トリガー）、FR-04（AI 判断）、FR-10（リスク統制）、
  FR-16（報告書の数値定義）、UC-01（定時サイクル）、UC-02（価格変動サイクル）、ADR-0003（AI 判断の権限＝
  確定日報の方針・リスク制約の範囲内）、ADR-0004（情報源・現在値ソース）
- 対象 Issue: [#158](https://github.com/endazon/ai-stock-trading/issues/158)（実市況実装の消費者拡張）／
  親 [#81](https://github.com/endazon/ai-stock-trading/issues/81)（受け入れ条件 1 の消費者）
- 関連する実装仕様書: [20260724_current-price-context-supply](../specs/20260724_current-price-context-supply.md)
- 関連 IADR: [IADR-0068](IADR-0068_live-quote-feed-finnhub-extraction.md)（**本 IADR の前提**。実市況＝Finnhub を共有
  `IMarketDataSource` へ抽出・`MarketDataSourceFactory`・既定 no-op）、[IADR-0066](IADR-0066_market-valuation-supply-and-gate.md)
  （現在値供給・前回値フォールバック・鮮度期限 `MaxQuoteStalenessSeconds`）、
  [IADR-0023](IADR-0023_trading-cycle-scheduling-and-merge.md)（定時・価格変動の合流と `DecisionTrigger`）、
  [IADR-0017](IADR-0017_trade-decision-structure.md)（判断コア・安全既定＝取引しない）、
  [IADR-0035](IADR-0035_stop-loss-authoritative.md)（損切り価格の権威化＝下流へ渡る権威データ）、
  [IADR-0072](IADR-0072_rag-trade-decision-context.md)（判断文脈へのポート／アダプタ分離の形）、
  [IADR-0076](IADR-0076_trade-decision-profitability-gate.md)（採算ゲート・参照価格由来の notional）、
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（実弾 triple-latch＝本決定は触れない）

## 背景・課題

定時取引サイクル（`InformationCollectedConsumer` → `DecisionTrigger.Scheduled(symbol, market)`）は **`Price=null`** の
トリガーを作る。`TradeDecisionPromptBuilder` は定時トリガーに対して銘柄・市場しか出さず**現在値を載せない**ため、
LLM は ADR-0003 の「方針の範囲外・不確実なら必ず Hold」に従い常に Hold へ倒れ、発注意図が生成されない。結果として
SIMULATE(paper) でも約定・含み損益が発生せず、dogfood の売買が回らない。

加えて `TradeDecisionService` はサイジング・OrderIntent・損切り・採算 notional の参照価格に `decision.ReferencePrice`
（LLM が出力する数値）を用いており、**権威ある市場価格ではない**。仮に Buy が出ても 1取引%・損切り幅・採算が
LLM の幻覚しうる価格に対して効くため、リスク統制の整合が担保されない。

IADR-0068 で実市況（Finnhub `/quote`）は共有 `IMarketDataSource` へ抽出済みで、リスク管理の mark-to-market・報告書・
市場監視が既にこれを消費している。**判断サービスだけが現在値を消費していない**。本決定はこの供給経路を判断サービスへ
通す。

## 決定

### 1. 判断文脈の現在値は Application ポート `ICurrentPriceProvider` で供給する（既定 no-op）

RAG 取得（`IRetrievalContextProvider`・IADR-0072）・採算見積り（`IProfitabilityAssumptionsProvider`・IADR-0076）と
同じ「ポート／アダプタ」分離を採る。Application/Domain を `Shared.Infrastructure` の `Quote` DTO へ直結させないためと、
「有効化されているか（`IsEnabled`）」を判断側の fail-safe ゲート（決定 3）が参照できるようにするため。

- ポート `ICurrentPriceProvider`（`Application/Ports`）: `bool IsEnabled` ＋ `Task<decimal?> GetCurrentPriceAsync(trigger, ct)`。
- 既定 `NoOpCurrentPriceProvider`（`Application/Adapters`）: `IsEnabled=false`・常に null（＝現在値なし＝現行動作）。
- Worker アダプタ `MarketDataCurrentPriceProvider`（`Worker/Composable/Adapters`）: 共有 `IMarketDataSource`（IADR-0068）を
  包み、`Quote` を取得して鮮度（`AsOf` と `IClock.UtcNow` の差が `MaxQuoteStalenessSeconds` 以内）を検査し、
  成功かつ鮮度内のときだけ `Quote.Price` を返す。取得不可・鮮度切れ・例外は null。`IsEnabled` は現在値ソースが実結線
  （`MarketData:Provider` が既知の live provider）のときのみ true。

### 2. 現在値は「定時プロンプトへの注入」と「発注参照価格のアンカリング」の両方に使う

現在値が非 null のとき（有効化＋取得成功＋鮮度内）:

- **プロンプト注入**: `TradeDecisionPromptBuilder.Build(..., currentPrice)` が定時（Scheduled）節に「現在値」行を追記する。
  これで LLM は定時サイクルでも価格文脈を得て Buy/Sell を判断できる。価格変動（PriceMovement）節は既に `trigger.Price`
  を出しているため**変更しない**（現在値供給の有無で PriceMovement 経路のプロンプト文言を変えない）。
- **アンカリング**: 発注に用いる参照価格を `referencePrice = currentPrice ?? decision.ReferencePrice` とし、
  サイジング（`PositionSizer.CalculateCappedQuantity`）・OrderIntent・損切り価格（IADR-0035）・採算 notional（IADR-0076）の
  すべてを `referencePrice` から導出する。1取引%・損切り幅・採算を**実市場価格に対して**効かせる。
- アンカリング後は IADR-0035 の不変量を権威価格に対して再検証する: `referencePrice<=0` /
  `StopLossDistancePerShare<=0` / `StopLossDistancePerShare>=referencePrice` のいずれかなら見送り。

現在値が null（既定 no-op）のときは `referencePrice==decision.ReferencePrice` となり、プロンプトも注入行を出さないため
**現行挙動とバイト等価**（Parser が既に距離＜LLM 参照価格を保証しているので再検証も素通り）。

### 3. 有効化時のみ「取得不可・鮮度切れ」を安全側（Hold・発注抑止）へ倒す

`ICurrentPriceProvider.IsEnabled=true`（現在値ソースが結線されている）かつ現在値が null（取得不可・鮮度切れ・例外）の
とき → **見送り（発注抑止・null 返却）**。「有効化したのに現在値が取れない」ときに古い/無い価格で発注しないための安全側。

**`IsEnabled=false`（既定）のときはこのゲートを一切適用しない**。既定で全銘柄が Hold になると既存 SIMULATE 検証
（情報収集→判断）が壊れ、かつ「現在値供給を有効化していない」状態と「有効化したが取得できない」状態は安全上の意味が
異なる（前者は現状維持、後者は明示的な発注抑止）。この 2 状態を分けるために `IsEnabled` をポートに置く。

### 4. 有効化は構成で行い、既定・不備はすべて no-op へ倒す

判断サービス（`TradeDecisionService.Worker`）が共有 `MarketDataSourceFactory`（IADR-0068）で現在値ソースを組む。

| 構成 | 結果 |
| --- | --- |
| 未設定・空・`none`（**既定**） | no-op（`IsEnabled=false`・現在値なし＝現行挙動） |
| `finnhub` ＋ `MarketData:Finnhub:ApiKey` あり | `FinnhubMarketDataSource`（`IsEnabled=true`・実価格） |
| `finnhub` ＋ キー無し／未知の provider | 警告ログ → no-op（起動は失敗させない） |

鮮度期限は `MarketData:MaxQuoteStalenessSeconds`（既定 300s・IADR-0066）を流用する（現在値を使う全サービスで共通概念）。
`MarketData` は base `appsettings.json` に置かない（IADR-0048・`FORBIDDEN_BASE_KEYS` 既登録）。設定点は
`appsettings.Development.json`／環境変数／経路B(values-local/overlay)。**実弾 triple-latch（IADR-0060）には一切触れない**。

## 根拠

- **(b) 市場データ源を判断へ注入する理由**: 現在値の唯一の権威ポート `IMarketDataSource` は IADR-0068 で共有化済みで、
  リスク管理・報告書・市場監視が既に消費している。判断だけがこれを使わない状態を解消するのが最小の変更で、
  decimal 価格＋`AsOf`（鮮度）を構造化して得られる唯一の経路でもある。RAG（案 a）は意味検索であり最新値の権威源に
  不適（decimal＋鮮度が取れない）。PriceMovement トリガー（案 c）はイベント経路のみで、定時 dogfood には
  market-monitor のしきい値超過に依存し定常発火しない。
- **アンカリングする理由**: 参照価格はサイジング・損切り・採算のすべての入力である。LLM の幻覚しうる数値に
  1取引%・最大DD・損切りを効かせると、リスク統制が実相場と乖離する。権威ある現在値へ寄せることで
  「価格文脈を与える」目的と「サイジング/統制の整合」を同時に満たす。
- **`IsEnabled` をポートに置く理由**: 「既定（現状維持）」と「有効化したが取得不能（発注抑止）」を分けるのは
  安全設計上の要請で、Application 層が MarketData の構成詳細（provider 文字列・キー有無）を知らずに判定できるよう、
  結線状態だけをポートが表明する。

## 影響・トレードオフ

- **既定では何も変わらない**（`Provider` 未設定＝no-op・`IsEnabled=false`・プロンプト不変・参照価格＝LLM 値）。
  既存 `TradeDecisionServiceTests`・SIMULATE 統合系は不変で緑。
- **有効化すると定時サイクルでも売買が成立し得る**（プロンプトに現在値・LLM が Buy/Sell 可能）。ただし実際に値が
  流れることの確認は手動 opt-in の live 検証（実 Finnhub・CI 対象外・IADR-0049）に依存し、本 PR の範囲外。
- **日本株の現在値は依然取得不可**（Finnhub 無料枠は US のみ・IADR-0068 決定5）。日本株の定時サイクルは有効化時も
  現在値 null → `IsEnabled=true` なら発注抑止（Hold）に倒れる。これは古い/無い価格で発注しない安全側だが、
  日本株が dogfood 対象なら OpenD 市況 or 有料ソース待ち（IADR-0068 残課題）。
- **PriceMovement 経路のサイジングも有効化時に変わる**（従来 LLM 参照価格→権威価格）。これは改善だが opt-in の
  背後にあり、既定では従来どおり `trigger.Price` はプロンプトのみで参照価格は LLM 値。
- 判断サービスが同じ Finnhub の枠を**独立に**消費する（IADR-0068 決定4 の予算内訳＋1 経路）。`RequestsPerMinute`
  （既定 10）で配る。合計が無料枠（60/分）を超えないよう運用で守る（IADR-0068 と同じ規約）。

## 却下した代替案

- **(a) KnowledgeBase/RAG 検索で収集済みクオートを文脈へ**: 却下。RAG は意味検索でありクオートを文書として引くと
  古く不正確で、decimal 価格＋鮮度タイムスタンプが取れずサイジングの権威価格に使えない。
- **(c) market-monitor の PriceMovementDetected 経路に寄せる**: 却下。イベント経路のみで、定時 dogfood には
  しきい値超過に依存し定常発火しない。トリガー位相を変える結合も重い。
- **アンカリングせずプロンプト注入だけにする**: 却下。LLM が現在値を見て `referencePrice` に忠実に反映する保証が
  なく、サイジング/統制が幻覚価格に対して効くリスクが残る（本 issue の「整合を壊さない」を満たさない）。
- **既定でも現在値 null を Hold に倒す**: 却下。既存 SIMULATE 検証が壊れ、「未有効化」と「有効化したが取得不能」の
  安全上の区別が消える。
- **`Quote` を判断側で直接使う（ポートを設けない）**: 却下。Application/Domain を `Shared.Infrastructure` の DTO へ
  直結させると層構成（IADR-0072 と同じ分離）に反し、`IsEnabled` の表明箇所も失う。

## 残課題（後続）

- **手動 opt-in の live 検証**（実 Finnhub API・429 の確認・CI 対象外）→ #158 に残る（本 PR では行わない）。
- **日本株の現在値**（Finnhub 無料枠は US のみ）→ OpenD 市況 or 有料ソース（ADR-0005）で後続（IADR-0068 残課題）。
- 実 LLM の多数決/二段（IADR-0039）の一次スクリーニングへ現在値を載せるか否かは、本 PR では**載せない**（費用統制の
  軽量スクリーニングは絞り込みのみで価格を要さない・IADR-0072 決定2 と同じ切り分け）。必要になれば後続で判断する。
