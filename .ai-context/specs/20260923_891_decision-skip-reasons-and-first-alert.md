---
title: 見送りの理由を区別して観測できるようにし、アラートルールの 1 件目を置く
type: spec
status: accepted
related_ids: [FR-04, FR-10, NFR-07, UC-01, UC-02, ADR-0003, IADR-0119, IADR-0255, IADR-0351, IADR-0358, IADR-0374]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04, FR-10, NFR-07)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_llm-trade-decision.md (不確実なら Hold)
---

# 仕様書: 見送りの理由を区別して観測し、アラートルールの 1 件目を置く（#891）

## 起点

- #891。#865 / PR #877（IADR-0358「保有が不明なら新規建てを見送る」）の監査が挙げた非ブロッキングの指摘。
- 症状は 2 つある。
  1. **見送りの理由が区別されない。** `DecideAsync` が `null` を返し、呼び出し元が
     `RecordTradeDecision(trigger, side: null)` を呼ぶため、**方針なし・Hold・鮮度切れ・数量 0・採算不成立・
     裸の新規売り・保有不明のすべてが `action=no-trade` の 1 値**に落ちる。事実は構造化 WARN ログにしか残らない。
  2. **アラートルールが 1 件も無い。** `deploy/observability/` にはダッシュボードしかなく、
     **人が見ているときにしか働かない**。
- 帰結: `RiskManagement:BaseUrl` の誤設定や保有照会の恒久的な失敗で**新規建てだけが静かに止まり続けても、
  WARN ログを読みに行かない限り誰も気付かない**（手仕舞いは通るので「全部止まった」形にはならない）。

## 🔴 母集合（走査したファイルと除外理由）

**記憶で挙げず、関連の側の文字列で全追跡ファイルを走査した**（`.claude/rules/traceability.repo.md` 規則 9・10）。

走査に使った語: `RecordTradeDecision` / `ActionNoTrade` / `ast.trade_cycle.decisions` /
`ast_trade_cycle_decisions` / `PrometheusRule` / `alerting` / `アラート`（`.git` を除く全ファイル。27 件ヒット）。
さらに見送り地点の母集合は **`TradeDecisionAppService` の `return null` を全件列挙**して引いた（記憶で数えない）。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/TradeDecisionAppService.cs` | **直す**（見送り 12 地点を 1 つの出口へ集約し理由を付ける） |
| `backend/Shared/AiStockTrading.Shared.Contracts/Observability/BusinessMetricNames.cs` / `BusinessMetrics.cs` | **直す**（新カウンタ 1 本・理由の語彙） |
| `backend/Services/TradeDecisionService/Features/TradeDecision/IDecisionSkipReporter.cs` ほか実装 2 本 | **新設**（報告ポート。既存の `IScreeningReductionReporter` と同じ作法） |
| `backend/Services/TradeDecisionService/Program.cs` | **直す**（実装の配線） |
| `deploy/observability/dashboards/ai-stock-trading-business.json` | **直す**（新カウンタのパネル。**置かないと `check-observability-assets` の R2 が落ちる**） |
| `deploy/observability/alerts/ai-stock-trading-alerts.yaml` | **新設**（アラートルールの 1 件目） |
| `deploy/observability/README.md` | **直す**（新系列・アラートの配備手順と規約） |
| `scripts/check-observability-assets.js` | **直す**（アラートが引く系列もレジストリへ突き合わせる。**新設ではなく既存検査器の射程拡張**） |
| `docs/operations/operations.md` §監視・アラート | **直す**（空の表を埋める。1 件目の運用規約） |
| `docs/observability/observability.md` | **直す**（「閾値は実測してから決める」の方針と本件の整合を書く） |
| `backend/Shared/AiStockTrading.Shared.Contracts.Tests/BusinessMetricsTests.cs` | **直す**（新カウンタの計上・既存 `decisions` を壊さないこと） |
| `backend/Services/TradeDecisionService/Tests/.../TradeDecisionServiceTests.cs` | **直す**（見送り理由の写像） |
| `.ai-context/adr/IADR-0374_*.md` / `.ai-context/adr/README.md` | **新設 / 直す** |
| `.ai-context/adr/IADR-0358_skip-open-when-holdings-unknown.md` | **直さない**。凍結記録であり、本件は「残る制約」を別 IADR で受けた形にする |
| `.ai-context/specs/20260919_865_*` ほかの仕様書 | **直さない**（point-in-time の凍結記録） |
| `deploy/helm/**`（`alert` の語がヒット） | **直さない**。Discord の通知先設定であり、Prometheus のアラートとは別物 |
| `CHANGELOG.md` | **直さない**（生成物） |

## 🔴 実測（見送り地点の全件列挙）

`TradeDecisionAppService` の `return null` は 16 か所ある。うち **12 か所が判断の見送り**で、
4 か所は fail-safe ラッパ（`GetHeldPositionSafeAsync` ほか）の縮退であり判断の出口ではない。

| # | 地点 | 理由（本件で与える語） |
| --- | --- | --- |
| 1 | 確定済み日報の方針が無い | `DailyPolicyUnconfirmed` |
| 2 | 現在値が取得不能・鮮度切れ（供給が有効なとき） | `CurrentPriceUnavailable` |
| 3 | 換算レートがまったく解決できない | `FxRateUnresolved` |
| 4 | 換算レート鮮度切れ＋保有なし／不明（LLM を呼ぶ前） | `FxRateStaleNoHolding` |
| 5 | LLM が Hold を返した | `LlmHold` |
| 6 | 🔴 **実結線の照会で保有が不明なのに新規建て（買い）** | **`HoldingsUnknownOpen`**（本 issue の対象） |
| 7 | 保有なし・不明での売り（裸の新規売り） | `NakedShortOpen` |
| 8 | 参照価格が不正（0 以下） | `ReferencePriceInvalid` |
| 9 | 換算レート鮮度切れで建玉効果が新規建て | `FxRateStaleOpen` |
| 10 | 損切り幅が不正（0 以下・参照価格以上） | `StopLossDistanceInvalid` |
| 11 | サイジングで数量 0 | `SizingZeroQuantity` |
| 12 | 採算不成立・見積り不能 | `ProfitabilityNotViable` |

**issue が「一度に洗い出してから決める」と要求した語彙はこの 12 である。**
1 件だけ特別扱いすると読み方が割れる（#891 やること 1）。

## 射程

| 含む | 含まない（理由） |
| --- | --- |
| 見送り理由の語彙（12 値）と新カウンタ `ast.trade_cycle.decision_skips` | 既存 `ast.trade_cycle.decisions` の変更。**壊さない**（ダッシュボードが無言で空になる） |
| 見送り地点を 1 つの出口（`Skip(...)`）へ集約する | 見送りの**判定そのもの**の変更。挙動は 1 ミリも変えない |
| アラートルールの 1 件目（保有不明による新規建ての見送りが続いている） | 他の理由のアラート。**閾値は実測してから決める**（`docs/observability/observability.md`） |
| `check-observability-assets.js` の射程をアラートへ広げる | 新しい検査器の新設（既存の R1 の規則をそのまま適用する） |
| 監査台帳・Discord 通知への追加 | **含まない。** IADR-0358 決定 4 が `TradeDecisionSkipped` / `OrderDispatchForgone` の流用を棄却した理由（誤帰属）は今も有効であり、新イベントを増やすかは本件の結論を見てから決める（#891 やること 3） |

## 決めたこと

### 決定 A: 既存カウンタは触らず、**理由つきの別カウンタ**を足す

- `ast.trade_cycle.decisions{action,trigger}` は**そのまま**。`action=no-trade` の集計を壊すと、
  既存ダッシュボードのパネルが無言で空になる（#891 受け入れ基準 3）。
- 新設 `ast.trade_cycle.decision_skips{reason,trigger}` を足す。**タグにするのは理由と起動契機だけ**で、
  銘柄は入れない（`BusinessMetrics` のカーディナリティ規律）。
- **見送り 1 回につき、両方が 1 ずつ増える**（`decisions{action=no-trade}` と `decision_skips{reason=…}`）。
  合計は一致するので、片方が欠けていれば突き合わせで分かる。

### 決定 B: 報告は**出力ポート**（`IDecisionSkipReporter`）で受ける

- `TradeDecisionAppService` は既に `IScreeningReductionReporter` / `IDailyPolicyUnconfirmedNotifier` を
  **省略可能・既定 NoOp・Worker が実装を配線**という形で持っている。**同じ作法に揃える。**
- `BusinessMetrics` を必須引数で足す案は採らない —— 本サービスの構築点は**テストに 30 か所**あり、
  そのすべてを触る変更は本件の射程（観測可能性）に対して大きすぎる。
- ポートは**同期・戻り値なし**とする。計上は `Counter.Add` だけで、I/O も例外も持たない
  （非同期にすると呼び出し側 12 か所に `await` と fail-safe を足すことになり、見送りの判断に
  新しい失敗経路を持ち込む）。

### 決定 C: 見送りの出口を**1 つの private メソッドへ集約**する

- `private TradeDecisionMade? Skip(DecisionTrigger trigger, DecisionSkipReason reason)` を置き、
  12 地点を `return Skip(trigger, …);` にする。ログは各地点のまま（文脈が違う）。
- 🔴 **理由なしで見送る経路を構造的に作りにくくするためである。** 計上点を 12 か所に散らすと、
  次に見送りを足した人が計上を忘れても誰も気付かない（`RecordCycleLatency` が
  「分岐はここ 1 か所に持つ」とした規律と同じ）。

### 決定 D: アラートは `deploy/observability/alerts/` に **`PrometheusRule`** で置く

前例がゼロ件なので、本件で規約を決める（#891 やること 2）。

- **置き場所**: `deploy/observability/alerts/ai-stock-trading-alerts.yaml`（1 ファイル・複数 group 可）。
- **種別**: `monitoring.coreos.com/v1` の `PrometheusRule`（kube-prometheus-stack が拾う形）。
  実 stand-up は MSP 側の共有 overlay であり、本リポジトリは**資産を置くところまで**を持つ
  （ダッシュボードと同じ扱い）。
- **命名**: group は `ai-stock-trading.<領域>`、alert 名は **PascalCase の英語**
  （`AstEntriesBlockedByUnknownHoldings`）。Prometheus のアラート名は識別子であり日本語を入れない。
- **重大度**: `severity` ラベルに `warning` / `critical`。**取引が止まる方向の異常は `warning` から始める**
  （実測が無いまま `critical` を置くと、最初の 1 件で狼少年になる。`docs/observability/observability.md`）。
- **本文**: `summary`（1 行）と `description`（何が起きているか・最初に見る場所）を日本語で書く。
  `runbook_url` は置かない（対応手順がまだ 1 件も無いのに URL を書くと、存在しない文書へ誘導する）。

### 決定 E: 1 件目のルールは「保有不明による新規建ての見送りが続いている」

```promql
sum(increase(ast_trade_cycle_decision_skips_total{reason="HoldingsUnknownOpen"}[15m])) > 0
for: 30m
```

- **なぜこれが「実測なしで置ける閾値」なのか**: `HoldingsUnknownOpen` は**実結線の照会が失敗したときにしか
  立たない**（未結線の既定構成では `IHeldPositionProvider.IsEnabled` が false で、この見送りは発生しない）。
  つまり **平常時の期待値が 0 件**であり、「N 分間に M 件」という**実測が要る形の閾値ではない**。
- `for: 30m` は一過性の照会失敗（再試行で回復するもの）を鳴らさないための猶予である。
  **30 分続くのは「恒久的に新規建てが止まっている」側の事象**である。
- 他の理由（`LlmHold` など平常時に多発するもの）にはルールを置かない —— 閾値に実測が要るためである。

## 受け入れ基準 → テスト

| # | 受け入れ基準（#891） | テスト |
| --- | --- | --- |
| 1 | 保有不明による新規建ての見送りが、ログ以外の経路で他の見送りと区別して数えられる | **T-10-663**（理由タグつきで 1 件計上される）／**T-10-664**（12 の理由が別々の値として計上される） |
| 2 | その状態が続いたときに能動的に通知される（アラートルールが存在し、条件が検証されている） | **T-10-665**（アラート資産の構造と、引く系列がレジストリに実在することを検査器が固定する） |
| 3 | 既存の `action=no-trade` の集計を壊さない | **T-10-666**（1 回の見送りで `decisions{action=no-trade}` も従来どおり 1 件増える・否定形） |
| — | 理由なしの見送りを作りにくい（出口の集約） | **T-10-667**（見送り 12 地点すべてが理由を報告する・網羅） |

## 🔴 変異注入（示すこと）

| 変異 | 期待 |
| --- | --- |
| ① 見送り 1 地点の `Skip(...)` を素の `return null` へ戻す | **T-10-667 が赤**（その理由の計上が消える） |
| ② 新カウンタの計上で既存の `decisions` 計上を置き換える | **T-10-666 が赤**（既存ダッシュボードが無言で空になる形） |
| ③ アラートの系列名を存在しないものへ変える | **T-10-665 が赤**（`check-observability-assets`） |

## 検証

`dotnet build backend/backend.slnx` / `dotnet test`（TradeDecisionService・Shared.Contracts）/
`dotnet format --verify-no-changes` / `node scripts/check-observability-assets.js`（`--self-test` も）/
`node scripts/check-*.js`。
