---
title: 定時の判断に各銘柄の直近の値動き（前日比・当日始値比・日中高安）を材料として渡し、出来高は「未提供」と明示する（#1035）
type: spec
status: accepted
related_ids: [FR-02, FR-04, FR-01, UC-01, ADR-0003, ADR-0044, ADR-0020, ADR-0033, ADR-0036, IADR-0099, IADR-0068, IADR-0313, IADR-0247, IADR-0351, IADR-0318, IADR-0387, IADR-0435, IADR-0451]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02・FR-04)
  - planning:projects/ai-stock-trading/07_adr/ADR-0044_watchlist-in-decision-prompt-and-stage0-asof.md (決定 1「価格変動トリガーの現在値は収集情報〔市況〕に含まれる」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (判断入力の限定)
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md (欠測の明示)
---

# 定時の判断に各銘柄の直近の値動きを材料として渡す（#1035）

## 背景

#1035（#1015 のやること 2 の切り出し）。2026-09-26 の稼働 PoC で、定時の判断・一次スクリーニングは全件が
「上昇の勢い（価格上昇・出来高増加）の具体的な情報が提示されていない」として Hold に倒れ、注文が出なかった。

- 定時の判断へ渡る市況は `TradeDecisionPromptBuilder` の定時の節の「- 現在値」1 行だけである（origin/develop `9ee4b0ca`）。
- 共有 `FinnhubQuoteClient` は `/quote` の `c/h/l/pc/t` を読み `FinnhubQuoteSnapshot` が高値・安値・前日終値を持つが、
  `FinnhubMarketDataSource` が `Quote(symbol, market, Current, AsOf)` に詰め替えて捨てている。始値 `o` は読んでいない。
- 値動きの材料は、急変（PriceMovementDetected）が起きたときの「基準値・変動率」しか判断へ渡らない。

## 計画上の判定（裁定を要しない範囲）

- 前日終値・当日始値・日中高安は、**現在値と同じ Finnhub `/quote` 応答の別項目**である。計画 ADR-0044 決定 1 は
  「価格変動トリガーの現在値は、収集情報（市況）に含まれる。外部由来であり、既存の列挙の内側である」と定める。
  同じ応答の別項目は同じ情報源・同じ分類（市況）であり、**新しい外部の情報源を判断へ加えるもの（ADR-0003 の改定を要する）ではない**。
  したがって実装 IADR で足りる（IADR-0451）。
- **出来高**は `/quote` に無く、別の経路（別エンドポイント・別の情報源）が要る。新しい情報源の追加は計画の判断に当たるため、
  planning#702 で裁定中である。本 PR では**プロンプトに「出来高: 未提供」と明示する**（ADR-0020 の欠測の明示。無言で省かない）。
- 前日比・当日始値比の計算は**コードで行う**（ADR-0003 / FR-16 の趣旨。LLM に計算させない）。
- #1035 のやること 3（経路 B のニュース源）は本 PR の射程外（残余として #1035 に残す。PR は `Closes` するため別 issue 化を提案する）。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC-1 | Finnhub `/quote` の `o`（始値）を読み、`Quote` が前日終値・始値・高値・安値を省略可能な値として運ぶ。0（場前・未知）は null（不明） | `FinnhubMarketDataSourceTests` |
| AC-2 | 既存の `Quote` 呼び出し（4 引数）はそのまま通る（追加項目は既定 null） | ビルド・既存試験 |
| AC-3 | 価格供給（`ICurrentPriceProvider`）は価格と日中文脈をまとめたレコードを返す。NoOp・鮮度切れ・取得不可は従来どおり null | `MarketDataCurrentPriceProviderTests`・`CurrentPriceProviderSelectionTests` |
| AC-4 | 前日比・当日始値比をコードで計算する（符号つき・小数 2 桁の %）。基準が無い・0 以下なら「不明」 | `PriceContextInPromptTests`（`IntradayPriceContext` の計算を含む） |
| AC-5 | 本判断の定時の節・急変の節、一次スクリーニングに「前日終値 / 前日比 / 当日始値 / 当日始値比 / 日中高値・安値」が出る。値が無ければ「不明」と明示し、0 を出さない | `PriceContextInPromptTests` |
| AC-6 | 「出来高: 未提供」を明示する（値動きの行を出すすべての節） | `PriceContextInPromptTests` |
| AC-7 | 縮退の見積り（`ScreeningContextAssembler`）の銘柄ごとの保護分が、値動きの行の最悪長を含む。値動きは市況として保護分に入り、縮退で削られない | `ScreeningContextAssemblerTests`・`ScreeningContextDegradationTests` |
| AC-8 | Stage 0: 前日終値（日足）が渡されれば前日比を出し、当日始値・日中高安（当日の変化率）は「不明」とする。前日終値の日付は判断時点より前でなければ例外（未来の値で判断させない） | `Stage0DecisionRecorderTests`（追加） |
| AC-9 | 本番の判断フロー（`TradeDecisionAppService`）で、価格供給の日中文脈が本判断・一次の両プロンプトへ届く | `PriceContextInPromptTests.判断サービスは価格供給の日中文脈を一次と本判断の両方へ渡す` |

## 設計

- `Quote`（共有契約）へ `PreviousClose? / Open? / High? / Low?` を既定 null で足す（位置引数の末尾。既存の 4 引数の構築は不変）。
- `FinnhubQuoteClient`: 応答の `o` を読み、`FinnhubQuoteSnapshot` に `Open` を足す。`FinnhubMarketDataSource`: 0 以下を null にして `Quote` へ写す。
- 取引判断サービス: `IntradayPriceContext(PreviousClose?, Open?, High?, Low?)` と `CurrentPriceReading(Price, Intraday)` を新設。
  `ICurrentPriceProvider.GetCurrentPriceAsync` は `CurrentPriceReading?` を返す（NoOp・テストダブルも追随）。
- `TradeDecisionAppService`: 価格供給の呼び出し部分（取得・安全ラッパの戻り値・プロンプト構築への受け渡し）だけを変える。
  見送りゲート・参照価格アンカリングは `reading?.Price` で従来と同値。
- `TradeDecisionPromptBuilder`: `PriceContextLines(price, intraday, priceUnit)`（公開・縮退の見積りと試験が同じ文字列を測る）。
  変化率は**同じ節に出した現在値**から計算する（急変の節は `trigger.Price`、定時の節・一次は供給の現在値）。
  現在値を出さない節（定時で現在値なし）には出さない（既定 NoOp の構成。IADR-0099 決定 1 の現行動作を保つ）。
- `ScreeningContextAssembler.PerSymbolLineChars`: 400 → 400 ＋ `PriceContextReserveChars`（300）＝ 700。値動きの行の最悪長が予約を超えないことを試験で固定する。
- Stage 0: `AsOfDecisionInput` に省略可能な `previousClose`（`DatedPrice`）を足す。前日比だけを出し、始値・高安は不明。

## 母集合（規則 9: 誤りの側で走査した。`origin/develop` `9ee4b0ca`）

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| `git grep -n "new Quote("` | 7 件（本番 1＝`FinnhubMarketDataSource`、試験 6） | 本番 1 を写像の拡張。試験 6 は 4 引数のまま通る（既定 null） |
| `git grep -ln ICurrentPriceProvider` | 本番 5（インタフェース・AppService・MarketData／NoOp 実装・Program.cs）＋ IHeldPositionProvider のコメント 1、試験 3、`.ai-context/` 10、`deploy/helm` 2 | 本番・試験を追随。`.ai-context/` は凍結記録で書き換えない。`deploy/helm` は構成キーの説明で戻り値の型に触れない（変更不要を目視確認） |
| `git grep -n GetCurrentPriceAsync` | 本番 4・試験 8 | すべて追随 |
| `git grep -n PerSymbolLineChars` と試験の「銘柄行 400」 | 本番 1・試験のコメント 5 箇所（予算の内訳） | 予算の数値を +300 シフトして追随 |
| `FinnhubQuoteSnapshot(` | 本番 1（構築）・型定義 1 | `Open` を足す。情報収集（`FinnhubInformationSource`）は名前で読むため影響なし（ビルドで確認） |

除外: `MarketMonitorService` と `TradeDecisionMadeBaselineHandler`（#1077 が並行して触る。本 PR は `Quote` の追加項目を読まない＝市場監視の挙動は不変）。

## 残余

- 出来高: planning#702 の裁定待ち。裁定後に別 PR で供給する（プロンプトの「未提供」の行を差し替える）。
- #1035 のやること 3（経路 B のニュース源）: 本 PR の射程外。
- Stage 0 の実供給（日足からの前日終値）: 既定の as-of 入力供給口は常に null を返す（IADR-0318）。本 PR は入れ物と規律だけを用意する。
