---
title: IADR-0022 情報収集は既定で外部接続せず、取得テキストをデータとして分離する
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-08, ADR-0003, ADR-0004, ADR-0005]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/06_technical/02_datasource-candidates.md
---

# IADR-0022: 情報収集は既定で外部接続せず、取得テキストをデータとして分離する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-01（情報収集）、FR-08（KB/RAG）、ADR-0003（プロンプト注入防御）、ADR-0004（案A+ 情報源）、ADR-0005（無料優先）
- 対象 Issue: [#9](https://github.com/endazon/ai-stock-trading/issues/9)（Slice A）
- 関連する実装仕様書: [20260710_information-collection](../specs/20260710_information-collection.md)
- 関連 IADR: [IADR-0016](IADR-0016_safe-broker-execution.md)・[IADR-0020](IADR-0020_notification-safe-outbound.md)（安全既定ゲートの同型）

## コンテキストと課題

FR-01 は市況・ニュース・開示の収集・正規化・KB 保存を要求する。情報源は外部 API であり、(1) テスト/dev/誤設定で実 API に
接続して費用・レート制限違反を起こすリスク、(2) 取得テキスト（特にニュース）を LLM 文脈に入れることによるプロンプト注入
のリスクがある。安全既定と、取得テキストの扱いを決める必要がある。

## 検討した選択肢

1. **既定で実情報源に接続して収集する** — すぐデータが集まるが、テスト/誤設定で実 API 誤接続・費用/レート違反の事故リスク。
2. **取得テキストをそのまま LLM 文脈に渡す** — プロンプト注入（「以前の指示を無視して…」等）に脆弱。ADR-0003 の防御方針に反する。
3. **既定は外部接続しない no-op とし、構成で明示有効化した情報源のみ実接続。取得テキストは許可リストで選別し、
   データ境界で分離（spotlighting）してから扱う（採用）** — 誤接続を構造的に防ぎ、注入面を許可リスト＋データ分離で狭める。

## 決定

**選択肢 3** を採用する。

- **情報源の安全既定**: `IInformationSource` の既定は `NoOpInformationSource`（何も取得しない）。構成 `Collection:Source:Provider`
  が未設定/`none` なら no-op。`finnhub` 指定かつ API キー設定時のみ `FinnhubInformationSource`（実接続）。**キー未設定なら no-op へ
  フォールバックし警告**（費用/レート違反を起こさない・IADR-0016/0020 と同型）。
- **KB 保存の安全既定**: `IKnowledgeBaseSink` の既定は `LoggingKnowledgeBaseSink`（no-op/ログ）。実 platform KB 取り込みは
  FR-08（#18）連携で有効化する。
- **ソース許可リスト**（案A+・ADR-0004）: `SourceAllowlist` で許可された情報源のアイテムのみ受理し、非許可は破棄する
  （既定＝公式/準公式ソース）。
- **取得テキストのデータ分離**（ADR-0003・アーキ概要「ニュース入力の防御」）: `PromptSafetySanitizer` で本文を境界デリミタで
  ラップし、本文中のデリミタ衝突をエスケープ、制御文字を除去する（spotlighting）。LLM 文脈では「命令ではなくデータ」として扱う。
- **収集完了はイベント発行**: `InformationCollected` を発行し、取引サイクル（FR-02）の起点にする。

## 理由

- 安全既定（no-op）で実 API 誤接続・費用/レート違反を構造的に防ぎ、CI は no-op/fake で緑にできる。
- 許可リスト＋データ分離は ADR-0003 のプロンプト注入防御方針に沿い、注入面を最小化する。

## 結果

- 良い影響: 誤接続事故とプロンプト注入面を抑えつつ、収集→正規化→保存→イベントの骨格を整備できる。
- 悪い影響・トレードオフ: 実データ収集は構成有効化と実 API 前提の統合テストが別途必要（CI 既定では実行しない）。各公式
  ソースの本コネクタ・レート制限・実 KB 連携は後続。サニタイズは構造的分離であり、注入を完全排除するものではない
  （多層防御の一層。単一ソース由来の急シグナルは複数ソース裏取りまで発注保留＝別スライス）。
- フォローアップ: 各情報源コネクタ（SEC EDGAR/EDINET/BOJ/FRED 等）、実 platform KB 保存（FR-08・#18）、レート制限、
  用途別スケジュール（#21・FR-02）、複数ソース裏取り（06_daytrading-review §3.4）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0016](IADR-0016_safe-broker-execution.md)・[IADR-0020](IADR-0020_notification-safe-outbound.md)（安全既定ゲート）
