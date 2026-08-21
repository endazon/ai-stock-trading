---
title: IADR-0140 発注先（Broker Provider）を独立した軸として導入し、TradeMode を廃止する
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-12, FR-13, FR-10, UC-06, SC-02, SC-03, ADR-0008, IADR-0111, IADR-0127, IADR-0134, IADR-0136]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - planning:projects/ai-stock-trading/INDEX.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
---

# IADR-0140: 発注先（Broker Provider）を独立した軸として導入し、`TradeMode` を廃止する

- 状態: Accepted
- 日付: 2026-08-05
- 決定者: 実装（Claude Code）／ 起点 issue [#334](https://github.com/endazon/ai-stock-trading/issues/334)（親 [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 作業仕様書: [20260805_334_broker-provider-axis](../specs/20260805_334_broker-provider-axis.md)

## コンテキストと課題

利用者裁定（質問票 第 11 回 Q1・2026-08-02。INDEX 決定 46（計画リポ））により、
計画は次を確定した。

> 「運用段階（Stage）」と「**発注先**（`moomoo REAL` / `moomoo SIMULATE` / 内蔵 `paper`）」を**独立した 2 軸**
> として扱う。… **段階が定める動作モードは既定の組み合わせを示すにとどまる**（FR-20（計画リポ））

着手前の実装はこれと 3 点で食い違っていた。

1. 発注先という軸そのものが無い。`TradeMode`（`Paper` / `Live`）が「実資金かどうか」だけを 2 値で表しており、
   **moomoo `SIMULATE`（デモ環境へ OpenD 経由で実際に発注する）と内蔵 `paper`（外部へ一度も発注しない）が
   同じ値に潰れていた**。
2. その 2 値が段階に従属していた（`StageSettings.Mode`）。段階を変える以外に発注先を変える経路が無い。
3. Stage 1 の既定が `TradeMode.Paper` であり、計画の「Stage 1 ＝ moomoo `SIMULATE`」と一致しない
   （計画適合レジストリ `Stage.Stage1BrokerProvider` の登録済み逸脱。[IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)）。

2 と 3 が重なると計画が名指しする最悪の失敗が成立する——**内蔵 `paper` で稼働しているのに Stage 1 のつもりでいると、
60 営業日・100 件という合格証跡が擬似約定で積み上がる**。

## 検討した選択肢

1. **`TradeMode` を残し、`BrokerProvider` を別に足す** — 変更は小さいが、「実資金で執行されるか」の情報源が
   2 つになる。2 つある情報は必ず食い違い、**食い違ったときに実弾を素通しさせる側**（`TradeMode.Paper` かつ
   `BrokerProvider.MoomooReal`）が存在してしまう。統制の判定材料を二重化するのは、本 issue が消そうとしている
   混同を別の形で再生産することである。
2. **`TradeMode` を `BrokerProvider` へ置き換える（採用）** — 「どこへ発注するか」という 1 つの事実だけを持つ。
   実資金かどうかは `MoomooReal` かどうかで一意に決まる。
3. 発注先を文字列（`"paper"` / `"moomoo-sim"` / `"moomoo-real"`）で持つ — 構成値（[IADR-0111](IADR-0111_broker-tier-selection.md)
   の `Broker:Provider` / `Broker:Environment`）とは揃うが、既存の永続列・イベント本文が整数であり、
   移行に列の型変更を伴う。3 値の閉じた集合に文字列を使う利点も無い。

## 決定

### 決定1: `TradeMode` を廃止し、`BrokerProvider`（3 値）へ置き換える

`AiStockTrading.Shared.Contracts.Trading.BrokerProvider` を新設し、`TradeMode` を削除した。
「実資金で執行されるか」の判定は `== BrokerProvider.MoomooReal` の 1 か所に収束する。

### 決定2: 序数 0 / 1 に旧 `TradeMode` の意味を割り当て、新値は末尾へ足す

| 値 | 序数 | 旧 `TradeMode` |
| --- | --- | --- |
| `InternalPaper` | 0 | `Paper`（0） |
| `MoomooReal` | 1 | `Live`（1） |
| `MoomooSimulate` | 2 | （新設） |

本 enum は HTTP 応答・イベント本文（`TradeDecisionMade` / `OrderApproved` / `OrderRejected` が運ぶ `OrderIntent`）・
台帳列（`approved_orders.Mode`）が**整数として**往来させている。既存メンバの序数を動かすと過去の記録の意味が変わる
（[IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定2 と同じ規律。
[#333](https://github.com/endazon/ai-stock-trading/issues/333) は廃止メンバの序数 1 を空けたまま再利用しない形を採った）。
本件は**廃止ではなく意味の保存された置換**であるため、0 / 1 をそのまま引き継ぐのが正しい。列の型は
どちらも整数のため、EF の**スキーマ変更は生じない**（マイグレーション不要）。

### 決定3: プロパティ名 `Mode` は据え置く（型だけを入れ替える）

`OrderIntent.Mode` / `StageSettings.Mode` / `SizingContextView.Mode` / `ApprovedOrderRow.Mode` の**名前は変えない**。

名前を変えると、旧いキー（`"mode"`）を積んだ在庫中のイベント本文・設定ストアの JSON 行・台帳列が
新しい名前に束縛されず、**黙って enum の既定値 0 へ落ちる**。0 は `InternalPaper` であり、
`RiskEvaluator` の実弾判定（`intent.Mode == MoomooReal`）を**通してしまう**側の値である。
すなわち名前の変更はフェイルオープン方向の移行になる。名前の美しさと引き換えにする価値はない。

計画自身が段階側の値を「段階が定める**動作モード**」と呼んでいるため、`StageSettings.Mode` の呼称は
計画の字面とも整合する。

### 決定4: 現在の発注先は `RiskManagementSettings.BrokerProvider` が段階と独立に保持し、初期値は `InternalPaper`

段階の `Mode` は「その段階で通常選ぶ既定の組み合わせ」であり、現在値ではない。現在値は設定の一部として持ち、
SC-02 から変更する（[IADR-0141](IADR-0141_live-switch-explicit-confirmation.md)）。

**計画はシステム初期状態の発注先を述べていない**（段階ごとの既定は定めるが、初期値そのものの記述が無い）。
実装は `InternalPaper` を選んだ——外部へ一度も発注しない唯一の値であり、初期段階が Stage 0（検証）であることとも
整合する。設定ストアの旧行（本プロパティを持たない JSON）も同じ既定へ落ちる。**「読めない行は実弾」に倒れないこと**が
要点である（[IADR-0131](IADR-0131_short-selling-controls-fail-closed.md) の空売り既定と同じ規律）。

### 決定5: 発注先の設定は段階と独立に保存できるが、実弾の**注文**は段階が許すまで止める

計画は保存について「運用段階との組み合わせは**保存を妨げない**が、段階が想定する発注先と異なる場合は
警告を表示する（例: Stage 1 のまま `moomoo REAL`）」（05_screens（計画リポ） SC-02 入力表）と定める。
**発注そのものの可否には触れていない。**

一方 FR-20 本文の「段階ごとの動作モード（SIMULATE / 実弾）と資金上限を**強制できる**」は 2026-08-02 の改訂後も
残っている。両立させる読み方は 1 つしかない——**設定は保存でき、発注は段階が既定として実弾を指すまで止まる**。
`RiskEvaluator` の `StageProhibitsLiveTrading` は従来どおり効かせる。安全側であり、計画のどの文とも矛盾しない。

### 決定6: 発注実行側の内部 enum `BrokerProvider` を `BrokerVendor` へ改称する

`OrderExecutionService.Infrastructure` は同名の `internal enum BrokerProvider`（`Paper` / `Moomoo`。
[IADR-0111](IADR-0111_broker-tier-selection.md) の vendor × environment のうち vendor 側）を持っていた。
同名の型が 2 つあると、**その名前空間の中でだけ `BrokerProvider` が別の意味になる**。3 値のつもりで書いたコードが
静かに 2 値の型に束縛される取り違えは、レビューでは見つけにくい。計画が「発注先（Broker Provider）」の語を
3 値へ確定させた以上、譲るのは内部型のほうである。

構成キー `Broker:Provider` と `BrokerSelection.Provider` の名は外部契約（Helm の `broker.tier`）と結びついているため
変えない。対応関係は `(Paper, *)` ＝ 内蔵 `paper` ／ `(Moomoo, Simulated)` ＝ moomoo `SIMULATE` ／
`(Moomoo, Live)` ＝ moomoo `REAL` である。

## 結果

- 計画適合レジストリの #334 担当 2 行（`BrokerProvider.Values` / `Stage.Stage1BrokerProvider`）を削除できた。
- 「実資金で執行されるか」の判定材料が 1 つになった。
- 序数・プロパティ名を据え置いたため、EF マイグレーションも旧データの移行も不要である。

### 残余リスク（本 PR では効かない部分）

- **設定値 `RiskManagementSettings.BrokerProvider` はまだ発注経路を動かさない。** 実際にどのアダプタへ発注するかは
  起動時の構成（`Broker:Provider` / `Broker:Environment`・[IADR-0111](IADR-0111_broker-tier-selection.md)）が決めており、
  設定値との結線は本 PR の範囲外である。したがって現時点の設定変更は「記録と表示」までであり、
  発注先そのものは変わらない。結線は実弾解禁（`LiveTradingGate.LiveTradingReleased`）と同じ議論を要するため、
  別 issue・別 ADR で扱う。
- 実弾は [IADR-0111](IADR-0111_broker-tier-selection.md) の閂 0 が未解禁のまま止め続ける。
  設定で `MoomooReal` を選べても実弾は撃たれない。
