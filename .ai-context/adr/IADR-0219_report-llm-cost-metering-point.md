---
title: IADR-0219 報告書生成の LLM 費用に計測点を設ける（対象外だが計上する）
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-16, NFR]
author: 実装エージェント（w1b）
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
---

# IADR-0219: 報告書生成の LLM 費用に計測点を設ける（対象外だが計上する）

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: 実装エージェント（w1b）／#347

## 起点・関連

- 関連する計画書 ID: 非機能要件（費用）・FR-06・FR-16 / 05_trading-assumptions §6.1
- 関連する実装仕様書: [20260828 作業仕様書](../specs/20260828_335_347_llm-allocation-and-cost-governance.md)・[IADR-0218](IADR-0218_llm-cost-scope-by-purpose.md)

## コンテキストと課題

**報告書生成の LLM 費用は、どこにも計上されていなかった。** `HttpReportNarrativeDrafter` に計測点が無く、
応答の `InputTokens` / `OutputTokens` を受け取ってすらいなかった（実測）。

これは #282 が指摘した「報告書散文費用の計上漏れ→過少申告」そのものである。#347 は同件について
「対象外費用も月報に実績記載する」の受け入れ基準でカバーすると明記している。

同時に、計画 §6.1 は報告書生成を月次上限の**対象外**とする。ここで
**「対象外だから計上しない」という誤読が最も起こりやすい** —— §6.1 の表は対象外の行にも
「月報に用途別の実測値を記載する」と明記しており、記録は必要である。

## 検討した選択肢

計測の実装をどこに置くか（サービス間の直接参照は禁止＝各サービスが自前のポートを持つ規約下で）。

| # | 案 | 難点 |
| --- | --- | --- |
| 1 | 共有プロジェクトに計測ポートと発行実装を置き、両サービスで使う | `Shared.Infrastructure` に Wolverine 依存が入る（広く参照されるプロジェクト） |
| 2 | `HttpReportNarrativeDrafter` に `IMessageBus` を直接注入する | 同ドラフタは singleton、`IMessageBus` は scoped（起動時にスコープ検証で落ちる） |
| 3 | **ReportService 自身のポート（`ILlmUsageReporter`）を置き、単価解決だけ共有物を使う** | ポートの型名が取引判断側と重複する（名前空間で分かれる） |

## 決定

**選択肢 3 を採る。**

### 決定 1: ReportService に自前の計測ポートを置く

`AiStockTrading.Report.Application.Ports.ILlmUsageReporter`（＋ `LlmUsage`・`NoOpLlmUsageReporter`）を新設する。
取引判断サービスの同名ポートと**同型・別名前空間**である。サービス間の直接参照は禁止という規約に素直に従う形であり、
「共有物を増やして結合を作る」より各サービスがポートを持つほうが既存の構成と一貫する。

**共有するのは単価解決だけ**（`Shared.Infrastructure` の `LlmPriceTable` / `LlmPricing`）である。
単価のロジックを複製すると、導入価格の終了（ADR-0017 決定5 が「最も起こりやすい失敗」と呼ぶもの）への
追随が 2 か所に分かれる。

### 決定 2: `LlmUsage` は用途（`Purpose`）を必須にする

取引判断側の `LlmUsage` は用途を持たない（発行側が 1 用途に固定されているため）が、
報告書側は 3 用途（月報・週報・日報）を持つ。**用途を載せ忘れると IADR-0218 決定3 により
上限側へ積まれ、計画が禁じた連鎖に戻る。** 必須引数にして構造的に塞ぐ。

### 決定 3: 発行は singleton の `IWolverineRuntime` から `MessageBus` を作る

`HttpReportNarrativeDrafter` が singleton であるため、scoped の `IMessageBus` は注入できない。
`MessageBusReportDraftPresentedNotifier` と同じ形を採る（IADR-0129）。

### 決定 4: 計測点は「送信が成立した直後・本文を読む前」

`Sent=true` なら本文の扱い（拒否・空・上限到達で破棄するか否か）とは独立に**課金は発生している**。
IADR-0104 決定4 が取引判断側で確立した位置と同じである。計測は best-effort＝失敗しても報告書生成を壊さない。

## 理由

- #282 の過少申告は「計測点が無い」という単純な欠落だった。**対象範囲の議論（IADR-0218）と計測の有無は別問題**であり、
  混同すると「対象外なので計上しない」という誤った実装が正当化されてしまう。本 ADR を分けたのはそのためである。
- 選択肢 1 を退けたのは、`Shared.Infrastructure` が多数のサービスから参照されており、
  そこへメッセージング依存を持ち込むと**メッセージングを使わないサービスにも依存が波及する**からである。

## 結果

- 良い影響: 報告書生成の費用が台帳に残り、月報へ供給できる。**上限には積まれない**ため、
  費用統制が報告書生成を止めることはない。
- 悪い影響・トレードオフ:
  - `ILlmUsageReporter` という型名が 2 つの名前空間に存在する（取引判断・報告書）。
    片方を直したときにもう片方の追随を忘れる余地がある —— ただし**両者は用途が違い、共有すべきものでもない**
    （報告書側だけが用途を必須にする）。共有物は単価解決に閉じている。
  - `ReportService.Infrastructure` が `Shared.Infrastructure` を参照するようになった（単価表のため）。
- フォローアップ:
  - 単価の構成（`LlmPricing:PerModel:<model-id>:*`）は報告書サービスの構成にも要る。
    未設定なら 0 円計上ではなく**表の最大単価**へ倒れる（IADR-0122 決定3。過小計上を作らない）。

## 関連

- Supersedes: なし
- Superseded by: なし
