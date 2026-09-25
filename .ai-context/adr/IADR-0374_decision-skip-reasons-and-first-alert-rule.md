---
title: IADR-0374 見送りの理由は既存カウンタと並置した専用カウンタで数え、アラートルールの 1 件目とその規約を置く
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-10, NFR-07, UC-01, UC-02, ADR-0003, IADR-0119, IADR-0255, IADR-0307, IADR-0351, IADR-0358]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04, FR-10, NFR-07)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_llm-trade-decision.md (不確実なら Hold)
---

# IADR-0374: 見送りの理由の観測と、アラートルールの 1 件目

- 状態: Accepted
- 日付: 2026-09-23
- 決定者: claude (Claude Code) / #891

## 起点・関連

- 関連する計画書 ID: **FR-04**（取引判断）・**FR-10**（統制）・**NFR-07**（可観測性）・UC-01 / UC-02
- 関連 IADR: [IADR-0358](IADR-0358_skip-open-when-holdings-unknown.md)（保有不明での新規建て見送り。
  **その「残る制約」を本 IADR が受ける**）、[IADR-0255](IADR-0255_business-metrics-and-dashboards.md)
  （業務メトリクスとダッシュボード。**系列の作法はこちらが正本**）、
  [IADR-0119](IADR-0119_decision-derived-close.md) 決定 2（裸の新規売りの見送り。同じくログのみだった）
- 関連する実装仕様書:
  [20260923_891_decision-skip-reasons-and-first-alert](../specs/20260923_891_decision-skip-reasons-and-first-alert.md)
- 起票: [#891](https://github.com/endazon/ai-stock-trading/issues/891)（PR #877 の監査が挙げた非ブロッキングの指摘）

## コンテキストと課題

1. **見送りの理由が区別されない。** `TradeDecisionAppService.DecideAsync` は `null` を返し、
   呼び出し元が `RecordTradeDecision(trigger, side: null)` を呼ぶため、
   **方針なし・Hold・鮮度切れ・数量 0・採算不成立・裸の新規売り・保有不明**のすべてが
   `ast.trade_cycle.decisions{action=no-trade}` の**同じ 1 値**へ落ちる。事実は構造化 WARN ログにしか残らない。
2. **アラートルールが 1 件も無い。** `deploy/observability/` にはダッシュボードしか無く、
   **人が見ているときにしか働かない**。

🔴 **帰結が悪い形をしている。** `RiskManagement:BaseUrl` の誤設定や保有照会の恒久的な失敗が起きると、
**手仕舞いは通ったまま新規建てだけが静かに止まり続ける**。「取引が全部止まった」という分かりやすい形に
ならないため、WARN ログを読みに行かない限り誰も気付かない。

## 検討した選択肢

| 案 | 判定 |
| --- | --- |
| A. 既存 `decisions` に `reason` タグを足す | **不採用**。既存ダッシュボードの `sum by (action)` の集計が割れ、パネルが無言で空になる（#891 受け入れ基準 3 に反する） |
| B. 見送り専用の別カウンタを並置する | **採用**（決定 1） |
| C. 監査台帳・Discord へ見送りイベントを足す | **見送り**（本 IADR の射程外）。IADR-0358 決定 4 が既存イベントの流用を「誤帰属になる」として棄却した理由は今も有効で、新イベントを増やすかは観測の結論を見てから決める |

## 決定

### 決定 1: 理由つきの**別カウンタ**を並置する（既存カウンタは触らない）

- `ast.trade_cycle.decision_skips{reason,trigger}` を新設する。
  `ast.trade_cycle.decisions{action,trigger}` は**そのまま**である。
- **1 回の見送りで両方が 1 ずつ増える。** 合計が一致するので、片方の計上漏れを突き合わせで検出できる。
- タグは理由と起動契機だけ（銘柄は入れない。IADR-0255 のカーディナリティ規律）。

### 決定 2: 理由の語彙は**一度に全件洗い出して**12 値とする

`TradeDecisionAppService` の `return null` を全件列挙して作った（記憶で数えていない）。
16 か所のうち 12 か所が判断の見送りで、4 か所は fail-safe ラッパの縮退（判断の出口ではない）。

| 理由 | 地点 |
| --- | --- |
| `DailyPolicyUnconfirmed` | 確定済み日報の方針が無い |
| `CurrentPriceUnavailable` | 現在値ソースが有効なのに現在値が取得不能・鮮度切れ |
| `FxRateUnresolved` | 換算レートがまったく解決できない |
| `FxRateStaleNoHolding` | 換算レート鮮度切れ＋保有なし／不明（LLM を呼ぶ前） |
| `LlmHold` | LLM が Hold を返した（**平常時に最も多い**） |
| **`HoldingsUnknownOpen`** | 🔴 実結線の照会で保有が不明なのに新規建て（本 issue の対象） |
| `NakedShortOpen` | 保有なし・不明での売り（裸の新規ショート建て） |
| `ReferencePriceInvalid` | 参照価格が 0 以下 |
| `FxRateStaleOpen` | 換算レート鮮度切れで建玉効果が新規建て |
| `StopLossDistanceInvalid` | 損切り幅が 0 以下・参照価格以上 |
| `SizingZeroQuantity` | サイジングで数量 0 |
| `ProfitabilityNotViable` | 採算不成立・費用見積り不能 |

🔴 **1 件だけ特別扱いしない**（#891 やること 1）。「その他」が何を含むかが読み手ごとに割れ、集計が意味を失うためである。

### 決定 3: 報告は**出力ポート**（`IDecisionSkipReporter`）で受け、見送りの出口を 1 か所へ集約する

- ポートは**省略可能・既定 NoOp・Worker が実装を配線**——既存の `IScreeningReductionReporter` /
  `IDailyPolicyUnconfirmedNotifier` と同じ作法である。`BusinessMetrics` を必須引数で足す案は採らない
  （本サービスの構築点はテストに 30 か所あり、観測可能性の追加にしては変更が大きすぎる）。
- ポートは**同期・戻り値なし**とする。実装は `Counter.Add` だけで I/O も例外も持たない。非同期にすると
  見送り 12 地点すべてに `await` と fail-safe が要り、**判断の見送りに新しい失敗経路を持ち込む**。
- 🔴 **見送りの出口を `private TradeDecisionMade? Skip(trigger, reason)` の 1 つに集約する。**
  計上点を 12 か所へ散らすと、次に見送りを足した人が計上を忘れても誰も気付かず、その見送りは再び
  「ログにしか残らない」状態へ戻る（`RecordCycleLatency` が「分岐はここ 1 か所」とした規律と同じ）。

### 決定 4: アラート資産は `deploy/observability/alerts/` に `PrometheusRule` で置く（規約をここで決める）

前例がゼロ件のため、本件で規約を決めた（#891 やること 2）。

| 項目 | 規約 |
| --- | --- |
| 置き場所 | `deploy/observability/alerts/ai-stock-trading-alerts.yaml`（1 ファイル・複数 group 可） |
| 種別 | `monitoring.coreos.com/v1` の `PrometheusRule`（kube-prometheus-stack が拾う形） |
| group 名 | `ai-stock-trading.<領域>` |
| alert 名 | **PascalCase の英字**（アラート名は識別子であり日本語を入れない） |
| 重大度 | `severity: warning` から始める。**実測が無いまま `critical` を置かない** |
| 本文 | `summary`（1 行）と `description`（何が起きているか・最初に見る場所）を日本語で。`runbook_url` は対応手順を書いてから足す |

- 実 stand-up（Prometheus / Alertmanager）は MSP 側の共有 overlay であり、
  本リポジトリは**資産を置くところまで**を持つ（ダッシュボードと同じ扱い）。
- 🔴 **系列名のずれを機械で止める。** `scripts/check-observability-assets.js` の射程をアラートへ広げた
  （検査 A1＝形・A2＝系列の実在）。**新しい検査器は作っていない** —— ダッシュボードに対する R1 と同じ規則を
  同じ関数で適用しただけである（「検査器の追加は同型の事故が 2 回起きたら」の規律に触れない）。

### 決定 5: 1 件目のルールは「保有不明による新規建ての見送りが 30 分続いている」

```promql
sum(increase(ast_trade_cycle_decision_skips_total{reason="HoldingsUnknownOpen"}[15m])) > 0
for: 30m
```

- 🔴 **この閾値は実測を要しない。** `HoldingsUnknownOpen` は保有照会が**実結線のときにしか立たず**
  （未結線の既定構成では `IHeldPositionProvider.IsEnabled` が false でこの見送りは発生しない）、
  **平常時の期待値が 0 件**である。「N 分間に M 件」という実測が要る形の閾値ではない ——
  可観測性仕様書の「閾値は実測してから決める」と矛盾しない。
- `for: 30m` は一過性の照会失敗（再試行で回復するもの）を鳴らさないための猶予であり、
  30 分続くのは「恒久的に新規建てが止まっている」側の事象である。
- **他の理由にはルールを置かない。** `LlmHold` のように平常時に多発するものは、閾値に実測が要る。

## 理由

- **理由を区別できないことの害は「気付けない」ことに集中している。** 判定は正しく動いており、
  出力（`null`）も正しい。欠けているのは**外から見える差**だけである。したがって是正も
  観測の追加に閉じるのが正しく、判定・イベント・通知へ手を広げない。
- **並置（決定 1）は「壊さない」ための選択である。** 既存の集計を割ると、直した当人には見えないところで
  ダッシュボードが空になる ——「監視しているつもりで何も見ていない」という、この領域で最も高くつく失敗である。

## 結果

### できるようになったこと

- 見送りを理由別に数えられる（12 種）。**保有不明による新規建ての見送り**が他と区別して読める。
- その状態が 30 分続いたときに**人が見ていなくても**アラートが上がる（配備は基盤側）。
- アラートが引く系列名の乖離を CI が止める。

### 🔴 残る制約

- **アラートの発火そのものは実バックエンドが要るため未確認である。** 本リポジトリで固定したのは
  「ルールが実在し、形が正しく、引く系列がコード側に実在する」までである。
  `for: 30m` が実運用で妥当かは、Prometheus / Alertmanager が立ってからでないと測れない。
- **12 値のうち振る舞いで固定したのは 9 値**である（`DecisionSkipReasonTests`）。
  残る 3 値（`ReferencePriceInvalid` / `StopLossDistanceInvalid` / `ProfitabilityNotViable`）は、
  到達に LLM 出力の不正か採算ゲートの構成が要るため、**語彙の側だけを固定**した。
- **監査台帳・Discord には載せていない**（決定の射程外）。見送りの事実は依然としてメトリクスとログにある。
- `IHeldPositionProvider.IsEnabled` は「配線されている」ことしか意味せず、照会先の健全性ではない
  （IADR-0358 の残る制約は解消していない）。本アラートは**その結果**を見ている。

### フォローアップ

1. 実バックエンドが立ったら `for: 30m` と 15 分窓を実測で見直す。
2. 見送りの内訳を数週間観察し、`LlmHold` 以外の理由が想定より多く出るなら、
   その理由についてのルール追加（閾値は実測に基づく）を検討する。

> ［2026-09-24 追記 / PR #919 監査］フレッシュな文脈の監査の指摘を受けて、次を記録する（上の本文は書き換えない）。
>
> - 🔴 **`LlmHold` は現状 3 つの帰結を 1 値に畳んでいる。** 判断オーケストレータが返す `Hold` は、
>   ①本判断の LLM が実際に Hold を返した ②一次スクリーニングで門前払いされた（`screenedOut`）
>   ③票が解析不能で安全側へ倒れた（`unparseableVotes` / `screeningUnparseable`）のいずれでもあり、
>   `DecideAsync` はこれらを区別せずに `LlmHold` で計上する。決定 2 の表の「LLM が Hold を返した」は
>   ①だけを言っているように読めるが、**計上上は ②③ も含む**。
>   **区別は構造化ログ（`LLM 判断:` 行の `screenedOut` / `unparseableVotes` / `screeningUnparseable`）には
>   従来どおり残っている**ため、事実は失われていない。本 PR では**語彙を増やさない**
>   （値を割ると決定 2 の網羅表・ダッシュボード・T-10-667 の同時改定が要り、本 issue の射程〔保有不明の検知〕を越える）。
>   `LlmHold` の急増を読むときは、ログで①〜③のどれかを確かめてから解釈すること。
>   値を割るかは、フォローアップ 2 の観察で `LlmHold` の内訳を知る必要が実際に出てから決める。
> - 🔴 **`Skip()` は計上の例外を握る**（決定 3 の「実装は例外を持たない」は現行実装の性質であって、
>   ポートの契約ではない）。例外が `DecideAsync` から漏れると呼び出し元が `decisions{action=no-trade}` を計上せず、
>   見送りが判断の失敗へ化けるため、兄弟ポートの `...SafeAsync` と同じく WARN ログへ縮退する（T-10-672）。
> - 🔴 **配線の消失をテストで止める**（T-10-671）。判断サービスの依存が省略可能（既定 NoOp）であるため、
>   `Program.cs` の登録を消しても既存テストは全緑のままカウンタ・パネル・アラートが無言になっていた
>   （IADR-0163 決定2 が禁じる形）。登録がちょうど 1 つで計上実装であること、DI が組んだ判断サービスがそれを
>   保持することを固定した。変異注入（登録行の削除）で 3 件が赤、他の 716 件は緑であることを実測した。
> - ダッシュボードの新パネルは `id: 16`・`gridPos.y: 42` とした。並行する PR #925 が `id: 15`・`y: 35` を足しており、
>   同じ値だと後からマージした側のパネルが無言で失われ得るためである。重複 id・重なる配置を検査器で止める件は
>   フォローアップ issue（#939。`for:` の欠落・折り畳みスカラーの `expr` を通す件、`absent()` 不在と日次のフラッピングの記録を含む）へ切り出した。
>
> ［2026-09-25 追記 / #939］検査器 `scripts/check-observability-assets.js` の射程を広げ、決定 4 のアラート規約に `for` の 1 項目を足す（上の本文は書き換えない。作業仕様書 `20260925_939_observability-asset-checks`）。
>
> - **D4・D5（新設）**: 同一ダッシュボード内の**パネル id の重複**と **gridPos の矩形の重なり**を赤にする。読めない id・gridPos も赤（読めないものを「重なりなし」と扱わない）。上の PR #919 / #925 の衝突は、改番で避けただけで検査は OK を出していた。
> - 🔴 **`for` の規約（A1 を拡張）**: アラートは `for:` を**正の期間**で持つ。**下限は置かない**（終端事象を見るルールには短い猶予が正しい。値の妥当性は事象ごとに人が決める）。
>   意図して置かないときは、**ルール直前のコメントに理由を書き、その中に印「for は置かない」を含める**。印の無い欠落は赤。
>   印を既存のコメントの語句にしたのは、`AstStopLossPositionRowsDegraded` が理由をその語句で既に書いており、**アセットを変えずに意図どおり通る**ため
>   （専用の機械記法を足すと、同じファイルへ 3 件目を足す並行 PR との衝突面を増やす）。理由を書かずに印だけを置くことは機械では止めない。
> - **A1（拡張）**: `expr` のブロック／折り畳みスカラー（`|` / `>` とその修飾）は本文まで読み、A2 を 0 系列で素通りさせない。`description` 等の本文は読み飛ばし、本文中の `for:` をキーと誤読しない。
> - `absent()` の不在・日次のフラッピングは扱っていない（閾値・式の設計であり、フォローアップ 1 の実測が要る）。

## 関連

- [IADR-0358](IADR-0358_skip-open-when-holdings-unknown.md)（決定 4・残る制約）
- [IADR-0255](IADR-0255_business-metrics-and-dashboards.md)（業務メトリクスの作法）
- 試験の写像: `docs/tests/FR-10_risk-controls-tests.md`（T-10-663〜667）
- 運用: `deploy/observability/README.md` / `docs/operations/operations.md` §監視・アラート
