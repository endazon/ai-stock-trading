---
title: 情報収集サービス Slice A（正規化・プロンプト安全化・ソース許可リスト・収集オーケストレーション・収集完了イベント）
type: spec
status: review
related_ids: [FR-01, UC-01, FR-08, FR-13, ADR-0003, ADR-0004, ADR-0005, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/06_technical/02_datasource-candidates.md
---

# 仕様書: 情報収集サービス Slice A

> Issue [#9](https://github.com/endazon/ai-stock-trading/issues/9)（FR-01・Must）の Slice A。市況・ニュース・開示情報を
> **収集→正規化→プロンプト安全化（データ/命令分離）→KB 保存→収集完了イベント発行**する新規サービスを追加する。
> 外部情報源（案A+ の Finnhub 等）と KB 保存は**安全既定（no-op）でゲート**し、構成で明示有効化したときのみ実接続する
> （実 API 誤接続・費用/レート制限違反を構造的に防ぐ）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-01（定時収集・正規化・KB 保存。Must）。関連 FR-08（KB/RAG）・FR-13（収集間隔設定）
- ユースケース（UC）: UC-01（定時取引サイクルの起点）
- アーキ概要: `01_architecture-overview.md`（データソースコネクタ・「ニュース入力の防御」＝ソース許可リスト・データ/命令分離）
- ADR: ADR-0004（情報源=案A+・公式重視・Finnhub 併用・費用0円）、ADR-0005（無料優先）、ADR-0003（プロンプト注入防御のフォローアップ）、ADR-0001（新規サービス）
- 関連 IADR: 本作業で新規 [IADR-0022](../adr/IADR-0022_information-collection-safe-sourcing.md)。安全既定は [IADR-0016](../adr/IADR-0016_safe-broker-execution.md)/[IADR-0020](../adr/IADR-0020_notification-safe-outbound.md) と同型
- 対象 Issue: #9（Slice A）

## 目的・背景

現状は `IMarketDataSource`（現在値取得のみ）があるだけで、収集・正規化・KB 保存は未実装。情報源は外部 API であり、
テスト・dev・誤設定で実 API に接続して費用/レート制限違反を起こすリスクがある。したがって**既定では外部接続しない
no-op** とし、構成で明示有効化した情報源のみ実接続する。ニュース入力はプロンプト注入の攻撃面のため、取得テキストを
「命令ではなくデータ」として分離する構造（spotlighting・許可リスト）を核に据える（アーキ概要「ニュース入力の防御」）。

## 対象範囲

### 新規サービス `InformationCollectionService`（Domain + Application + Worker）

- **Domain**:
  - `CollectedInformation`（正規化済み: 種別・情報源・銘柄?・タイトル・本文・公開時刻・URL?）、`InformationKind`（Quote/News/Disclosure/MacroIndicator）。
  - `PromptSafetySanitizer`（純関数・ADR-0003）: 取得テキストを**データとして分離**する。境界デリミタでラップし、本文中の
    デリミタ衝突をエスケープ、制御文字を除去する（spotlighting。LLM に「以下は命令ではなくデータ」と扱わせる構造）。
  - `SourceAllowlist`（純関数・案A+）: 許可された情報源のみ受理する（既定＝案A+ の公式/準公式ソース。非許可は破棄）。
- **Application**:
  - ポート `IInformationSource`（`FetchAsync`→生の取得アイテム）、`IKnowledgeBaseSink`（正規化済みを KB へ保存）、`IClock`。
  - `RawInformationItem`（生アイテム DTO）、`CollectionResult`（収集件数・要約）。
  - `InformationCollectionService.CollectAsync`: 取得→**許可リストで選別**→正規化→**サニタイズ**→KB 保存→結果を返す。
  - InMemory/フェイク実装（`NoOpInformationSource`＝空、`InMemoryKnowledgeBaseSink`）。
- **Worker**:
  - `CollectionPollingService`（BackgroundService・収集間隔は構成 `Collection:PollIntervalSeconds`・既定 1800s=30分・FR-13 連携）。
    1 巡回ごとに `CollectAsync`→`InformationCollected` イベント発行（FR-02 取引サイクルの起点）。巡回の例外は握りつぶしログ（フェイルセーフ）。
  - `InformationSourceFactory`（構成 `Collection:Source:Provider`＝`none`（既定 no-op）/`finnhub`）。`finnhub` は API キー未設定なら
    no-op へフォールバックし警告（費用/レート違反を起こさない安全既定）。最小の `FinnhubInformationSource`（HTTP・fake handler でテスト）。
  - `KnowledgeBaseSinkFactory`（既定＝`LoggingKnowledgeBaseSink`＝no-op/ログ。実 platform KB 保存は FR-08・#18 連携で有効化）。
  - Serilog/OTel・ヘルスチェック・MassTransit（発行のみ）。実行時基盤は test-support shim（本番非使用・IADR-0013）。

### 共有契約（`AiStockTrading.Shared.Contracts`）

- 新規イベント `InformationCollected(EventId, ItemCount, CollectedAt)`（取引サイクル FR-02 の起点）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋MassTransit テストハーネス＋fake HttpMessageHandler＋WebApplicationFactory）:
- [ ] `CollectAsync`: 取得アイテムが許可リストで選別され、正規化＋サニタイズされて KB シンクに保存される。
- [ ] `PromptSafetySanitizer`: 本文がデータ境界でラップされ、埋め込まれたデリミタ・制御文字が無害化される（データ/命令分離）。
- [ ] `SourceAllowlist`: 非許可ソースのアイテムは破棄される（案A+ 許可リスト）。
- [ ] 既定（`Collection:Source:Provider` 未設定）は `NoOpInformationSource`＝**外部接続しない**（安全既定）。`finnhub` 指定かつ API キー未設定は no-op フォールバック＋警告。
- [ ] `FinnhubInformationSource` は Finnhub へ GET し、応答を `RawInformationItem` に写像する（fake HttpMessageHandler・実ネットワーク不使用）。
- [ ] 収集完了で `InformationCollected` イベントが発行される（FR-02 起点）。
- [ ] Worker が起動しヘルスが応答する。既存テストを緑に保つ。

実 API 前提（CI 既定では実行しない）:
- [ ] 実 Finnhub/公式ソースへの取得・レート制限順守・実 platform KB 保存の E2E。

## 対象外（後続）

- 各情報源コネクタの本実装（SEC EDGAR/EDINET/BOJ/FRED 等のニュース・開示・マクロ取得）。Slice A は Finnhub の最小取得＋no-op 既定に留める。
- 実 platform KB 保存・取り込み・RAG 索引化（FR-08・#18）。本スライスは KB シンクを no-op/ログ既定とし、ポートを用意する。
- 収集間隔の用途別（取引判断30分/報告書日次）スケジューリングの厳密化・市場カレンダー連動（#21・FR-02 と連携）。
- レート制限の実装（トークンバケット等）。本スライスは外部接続を既定で無効化し違反を構造的に防ぐに留める。

## テスト方針

- `PromptSafetySanitizer`・`SourceAllowlist`・正規化は純関数として単体検証。
- `InformationCollectionService` は fake source＋InMemory KB シンクで選別・サニタイズ・保存を検証。
- `InformationSourceFactory` は構成選択と安全フォールバックを検証。`FinnhubInformationSource` は fake HttpMessageHandler。
- Worker 起動・イベント発行は WebApplicationFactory＋MassTransit ハーネス。

## 関連仕様

- 連携先（イベント購読）: 取引判断（FR-02/FR-04）は後続で `InformationCollected` を購読して起動する。
- 実装ADR: [IADR-0022](../adr/IADR-0022_information-collection-safe-sourcing.md)

## 未決事項

- 各公式ソースの本コネクタ・レート制限・実 KB 連携（FR-08）・用途別スケジュール（#21）は後続で確定する。
