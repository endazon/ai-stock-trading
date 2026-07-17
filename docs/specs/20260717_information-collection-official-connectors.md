---
title: 情報収集サービス Slice B（案A+ 公式ソース本コネクタ・多ソース合成・レート制限順守）
type: spec
status: review
related_ids: [FR-01, UC-01, FR-08, FR-13, ADR-0003, ADR-0004, ADR-0005]
author: endazon (with Claude Code)
created: 2026-07-17
updated: 2026-07-17
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/06_technical/02_datasource-candidates.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
---

# 仕様書: 情報収集サービス Slice B（公式ソース本コネクタ）

> Issue [#9](https://github.com/endazon/ai-stock-trading/issues/9)（FR-01・Must）の Slice B。Slice A（[20260710](20260710_information-collection.md)・
> [IADR-0022](../adr/IADR-0022_information-collection-safe-sourcing.md)）が「対象外（後続）」として残した**各公式ソースの本コネクタ**
> （SEC EDGAR / EDINET / 日銀 / FRED）と、その前提となる**多ソース合成**・**レート制限順守**を実装する。
> 実 KB 保存（FR-08・[#18](https://github.com/endazon/ai-stock-trading/issues/18)）は本スライスの対象外（`LoggingKnowledgeBaseSink` のまま）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-01（定時収集・正規化・KB 保存。Must）。関連 FR-08（KB/RAG）・FR-13（収集間隔設定）
- ユースケース（UC）: UC-01（定時取引サイクルの起点）
- ADR: **ADR-0004**（情報源=案A+。開示=EDINET＋SEC EDGAR、マクロ=FRED＋日銀API＋e-Stat、費用0円）、
  ADR-0005（無料優先）、ADR-0003（プロンプト注入防御）
- 技術検討: `06_technical/02_datasource-candidates.md`（各ソースの無料枠・レート制限・規約。「レート制限から逆算して監視銘柄数を決める」
  「フォールバックと欠測検知を最初から組み込む」「取得データの外部再配信はしない」）
- 関連 IADR: 本作業で新規 [IADR-0065](../adr/IADR-0065_official-source-connectors.md)。安全既定は [IADR-0022](../adr/IADR-0022_information-collection-safe-sourcing.md) を踏襲
- 対象 Issue: #9（Slice B）

## 目的・背景

Slice A で収集の骨格（取得→許可リスト選別→正規化→プロンプト安全化→KB 保存→`InformationCollected` 発行）と安全既定（no-op）は
整った。しかし実装済みコネクタは `FinnhubInformationSource`（米国株の現在値）のみで、ADR-0004 が定める案A+ の**開示（EDINET/SEC EDGAR）**・
**マクロ（FRED/日銀）**が未取得のままである。また現行の `InformationSourceFactory` は `Collection:Source:Provider` で**単一の**情報源しか
選べず、複数ソースを束ねる案A+ の構成を表現できない。さらにレート制限順守は「既定で外部接続しない」ことに依存しており、実接続を
有効化した時点で守る仕組みがない（ADR-0004 の受け入れ基準「レート制限違反がない」に対して構造が不足）。

本スライスは、(1) 多ソース合成、(2) ソース単位のレート制限、(3) 4 つの公式コネクタ、を追加してこの差分を埋める。

## 対象範囲

### 1. レート制限（Domain・純関数）

- `TokenBucket`（`Domain/RateLimiting/TokenBucket.cs`）: 容量・補充間隔を持つトークンバケットの**純粋な状態機械**。
  `TryConsume(now, out retryAfter)` が消費可否と待機時間を返す。時計を持たない（`now` を引数で受ける）ため決定的に単体検証できる。
- Worker の `DelayingRateLimiter` が `TokenBucket` と `IClock`（本リポジトリの既存慣行の時刻ポート）を組み合わせ、
  `WaitAsync` で待機する（ポート `IRateLimiter`）。待機（`Task.Delay`）は関数として注入し、テストでは実時間を使わない。
  各コネクタは HTTP 要求の直前に `WaitAsync` する。既定値は各ソースの公表上限より**保守側**に置く（下表）。

| ソース | 公表上限（技術検討 §1〜§4） | 本実装の既定 | 根拠 |
| --- | --- | --- | --- |
| finnhub | 60 回/分 | 30 回/分 | 公表上限の 1/2。他プロセスとの併用余地を残す |
| sec-edgar | 10 回/秒/IP | 5 回/秒 | 公表上限の 1/2。SEC は超過時 IP ブロック |
| edinet | 非公表（1 分 1 回程度が無難） | 1 回/分 | 技術検討の推奨に合わせる |
| boj | 非公表（「短時間における連続したアクセスは禁止」） | 1 回/分 | 系列コードは 1 要求に束ねるため 1 巡回 1 回で足りる |
| fred | 120 回/分 | 60 回/分 | 公表上限の 1/2 |

### 2. 多ソース合成（Worker）

- `Collection:Source:Provider` を**カンマ区切りの複数指定**に拡張する（`finnhub,sec-edgar,fred`）。既存の単一指定・`none`・未設定は
  そのまま動く（後方互換）。
- `CompositeInformationSource`: 有効化された各ソースを順に取得し、結果を連結する。**1 ソースの例外・失敗が他ソースと巡回を巻き込まない**
  （個別に握りつぶしてログ＝欠測検知。技術検討「フォールバックと欠測検知を最初から組み込む」）。
- `InformationSourceFactory`: 指定ソースを 1 つずつ検証し、**必須構成を欠くソースだけを警告つきで除外**する（他のソースは有効なまま）。
  有効なソースが 0 件なら `NoOpInformationSource`（安全既定）。未知の provider も除外＋警告。

### 3. 公式ソース本コネクタ（Worker・すべて明示構成時のみ実接続）

| コネクタ | 種別 | エンドポイント | 必須構成 | 写像 |
| --- | --- | --- | --- | --- |
| `SecEdgarInformationSource` | Disclosure | `https://data.sec.gov/submissions/CIK##########.json` | `SecEdgar:UserAgent`（SEC 規約の連絡先）・`SecEdgar:Ciks` | `filings.recent` の直近 N 件 → 提出書式・説明・提出日・書類 URL |
| `EdinetInformationSource` | Disclosure | `https://api.edinet-fsa.go.jp/api/v2/documents.json`（`type=2`） | `Edinet:SubscriptionKey` | `results[]` → 提出者名・書類概要・提出時刻・書類 URL |
| `BojInformationSource` | MacroIndicator | `https://www.stat-search.boj.or.jp/api/v1/getDataCode` | `Boj:Db`・`Boj:SeriesCodes` | `RESULTSET[]` の**最新観測値** → 系列名・単位・値・観測期 |
| `FredInformationSource` | MacroIndicator | `https://api.stlouisfed.org/fred/series/observations` | `Fred:ApiKey`・`Fred:SeriesIds` | `observations[]` の**最新観測値** → 系列 ID・値・観測日 |

- 共通の作法（既存 `FinnhubInformationSource` と同型）:
  - 取得失敗（レート制限・一時エラー・非 2xx）は**当該対象だけスキップ**してログし、1 巡回を止めない。
  - 取得アイテムの `Source` は許可リスト（`SourceAllowlist.Default`）の名称（`sec-edgar`/`edinet`/`boj`/`fred`）に一致させる。
  - 本文は `PromptSafetySanitizer` で収集サービス側がデータ分離するため、コネクタは**素の値のみ**を組み立てる（命令文を作らない）。
- 日銀 API の**クレジット表記義務**（「このサービスは、日本銀行時系列統計データ検索サイトのAPI機能を使用しています」）を満たすため、
  BOJ 由来アイテムの本文に当該クレジットを含める（KB・報告書へそのまま伝播する）。
- API キーを**クエリ文字列で渡す仕様のソース**（EDINET・FRED）は、OTel の HttpClient 計装が URL（クエリ込み）をトレースへ出力して
  キーが漏えいするため、当該要求のみ計装を抑止する（`SuppressInstrumentationScope`）。ヘッダーで渡せるソース（Finnhub）は従来どおりヘッダー。

### 4. 構成キー（PR 末尾の単一コミットに閉じる）

`Collection:Source:*` に上表の必須構成を追加する（appsettings / docker-compose / helm values / `.env.example`）。**すべて既定は空**で、
空なら当該ソースは無効（no-op）＝現行挙動を保持する。

## 受け入れ基準

CI で緑にする範囲（ユニット＋fake `HttpMessageHandler`＋フェイク時計。実ネットワーク・実時間不使用）:
- [ ] `TokenBucket`: 容量内は連続消費でき、超過時は `retryAfter` を返し、時間経過で補充される（境界値）。
- [ ] `DelayingRateLimiter`: 超過時に `retryAfter` だけ待ってから通す（フェイク時計＋フェイク待機で決定的に検証）。
- [ ] 各コネクタ: 実応答形の JSON を `RawInformationItem` に写像する（種別・ソース名・タイトル・本文・公開時刻・URL）。
- [ ] 各コネクタ: 非 2xx・空応答は当該対象をスキップし、他対象の取得を継続する（フェイルセーフ）。
- [ ] `SecEdgarInformationSource`: SEC 規約の User-Agent（連絡先）を要求ヘッダーに付与する。
- [ ] `EdinetInformationSource`・`FredInformationSource`: API キーがトレースへ漏れない経路で渡る（計装抑止）。
- [ ] `BojInformationSource`: 応答の最新観測値を採り、クレジット表記を本文に含める。
- [ ] `InformationSourceFactory`: 複数指定で `CompositeInformationSource` を組み、**構成を欠くソースのみ**除外する（他は有効）。
      全滅・未設定・未知 provider は `NoOpInformationSource`（安全既定・IADR-0022 を踏襲）。
- [ ] `CompositeInformationSource`: 1 ソースが例外を投げても他ソースの結果を返す（巡回を止めない）。
- [ ] 既存テスト（Slice A の許可リスト・サニタイズ・イベント発行・ヘルス）を緑に保つ。

実基盤・実 API 前提（CI 既定では実行しない・後続/E2E に分離）:
- [ ] 実 SEC EDGAR / EDINET / 日銀 / FRED への実接続、レート制限順守の実測、監視銘柄数の逆算（ADR-0004 フォローアップ）。
- [ ] 実 platform KB への保存・RAG 索引化（FR-08・#18）。

## 対象外（後続）

- **実 KB 保存（FR-08・#18）**: 本スライスは `LoggingKnowledgeBaseSink` のまま。#18 と交差するため触れない。
- **moomoo 市況コネクタ**: OpenD 経由（ADR-0002・[#132](https://github.com/endazon/ai-stock-trading/issues/132)・IADR-0053/0056）で別系統。
- **非公式・SLA なしのソース**（やのしんTDnet・Google News RSS・GDELT）: 予告なき停止が前提のため、欠測検知・フォールバック方針と
  合わせて別スライス（ADR-0004 フォローアップ「やのしんTDnet の欠測検知とフォールバックの実装」）。
- **需給（JPX 統計・FINRA）・e-Stat**: 取得形式が Excel/CSV・統計表 ID 特定が必要で、コネクタ規約が上記 4 ソースと異なるため別スライス。
- **検証・学習用（J-Quants Free・Stooq）**: ライブ判断用と分離する方針（技術検討「設計への含意」）のため、バックテスト基盤（#16）側で扱う。
- **用途別スケジュール**（取引判断30分／報告書日次）の厳密化: #21・IADR-0054（run-once／External トリガ）で扱う。
- **複数ソース裏取り**（単一ソース由来の急シグナルの発注保留）: 06_daytrading-review §3.4・取引判断側（#11）。

## テスト方針

- `TokenBucket` は純関数として境界値を単体検証（時計を注入しない）。
- `DelayingRateLimiter` はフェイク `IClock`＋フェイク待機（待機した時間だけ時計を進める）で決定的に検証する（実時間を
  待たない）。時刻抽象は `TimeProvider` ではなく本リポジトリの既存慣行 `IClock` に合わせる（IADR-0065 決定 2・選択肢 4）。
- 各コネクタは fake `HttpMessageHandler` で**実応答形の JSON**（実 API の応答を一次確認したうえで固定）を与えて写像・スキップを検証。
- `InformationSourceFactory`・`CompositeInformationSource` は構成選択・部分無効化・例外隔離を検証。
- 実 API 接続は CI で行わない（費用0円・レート制限順守の担保。IADR-0022 の方針を踏襲）。

## 関連仕様

- Slice A: [20260710_information-collection](20260710_information-collection.md)
- 実装ADR: [IADR-0065](../adr/IADR-0065_official-source-connectors.md)（本スライス）、[IADR-0022](../adr/IADR-0022_information-collection-safe-sourcing.md)（安全既定・データ分離）

## 未決事項

- 各ソースの実レート・監視銘柄数の実測は運用開始後に見直す（ADR-0004 フォローアップ）。本実装の既定は公表上限の保守側固定値。
- 日本の適時開示の即時取得（TDnet）は無料では非公式依存のまま（ADR-0004 のリスクとして継続）。
