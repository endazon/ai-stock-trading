---
title: IADR-0484 moomoo SIMULATE では経費の実績が取れない前提で、未計上のまま推計を積まず、取得できない理由を発注先ごとに明示する
type: impl-adr
status: Accepted
related_ids: [FR-11, FR-16, FR-17, UC-07, ADR-0016, ADR-0027, ADR-0035, ADR-0041, IADR-0300, IADR-0226, IADR-0140]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0027_borrow-fee-accrual-recording.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0035_cost-ratio-denominator-and-cost-total-composition.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0484: SIMULATE では経費を未計上のまま推計を積まず、取得できない理由を発注先ごとに明示する（#1086 段 2 の設計し直し）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-02
- 決定者: Claude Code（実装）。計画が一意に定める範囲（未供給の扱い）だけを実装し、推計の記録は利用者の裁定へ残す

## 起点・関連

- 関連する計画書 ID: **FR-11**（取引記録の経費区分 7 種・建玉単位）／**FR-16**（費用はコードで集計）／FR-17（前提条件）／UC-07／
  **ADR-0016 決定15**（記録は遡って復元できない）／**ADR-0027 決定4**（取得できなかったものを 0 として積まず「未供給」として記録する）／
  **ADR-0035 決定3・決定5**（未供給の区分は「未供給」と描き費用合計が過小である旨を明記する）／ADR-0041 決定1（推定を確定値から分離して運ぶ通り道が無い値は記録しない）
- 起票: [#1086](https://github.com/endazon/ai-stock-trading/issues/1086)（段 1 の検証口の実測コメント 2026-10-02）
- 前提: [IADR-0300](IADR-0300_trade-expense-source-port-and-negative-recording.md)（経費の取得ポート・未供給は 7 区分 `LineCount` = 0・推定しない）／
  [IADR-0226](IADR-0226_trade-expense-categories-and-position-linkage.md) 決定7（概算を実費として積まない）／[IADR-0140](IADR-0140_broker-provider-axis.md) 決定6（発注先 3 値）
- 関連する実装仕様書: [`.ai-context/specs/20261002_1086_order-fee-simulate-unsupported.md`](../specs/20261002_1086_order-fee-simulate-unsupported.md)

## コンテキストと課題

段 1（IADR-0300 2026-09-29 追記）の検証口を利用者が 1 回実行した（2026-10-02）。注文の照会は成功した（status=11・`orderIdEx` を取得）。
費用照会 `Trd_GetOrderFee` は **`retType=-1 retMsg=Paper trading is not supported.`** で失敗した（exit=1）。
**moomoo の注文費用照会は SIMULATE 口座では使えない。** 段 2 が前提にしていた「結線すれば実費が取れる」は SIMULATE の間は成り立たない。

一方、本番の既定 `UnsuppliedOrderExpenseSource` は約定のたびに「**未接続**（照会は実装済みだが経費の供給ポートへ未接続）」を理由に警告していた。
これは SIMULATE では**誤った理由**である —— 結線しても取れない。決めるべきは 2 点である。
**(a) SIMULATE の間、経費の記録を推計で埋めるか（案 1）、未計上と明示するか（案 2）。(b) 取得できない理由をどう表すか。**

## 計画との突き合わせ（案 1 と案 2）

| 根拠 | 記述 | 案 1（推計を記録） | 案 2（未計上と明示） |
| --- | --- | --- | --- |
| ADR-0027 決定4（借株料） | 取得できなかった日は「未供給」として記録し、**0 として計上しない**。表示は「供給が無い値」の規約に従う | 推計で埋めることを定めていない | **合致** |
| ADR-0035 決定3・決定5 ／ 04_report-templates §数値の定義「費用合計」 | 供給されない区分は「未供給」と描き、**費用合計が過小である旨を凡例に明記する**。暫定値（SEC・TAF）を前提条件へ登録したら算入できる | 報告書の**事後集計**で登録済み料率を使うことは許すが、**取引記録へ積むこと**は定めていない | **合致** |
| 05_trading-assumptions §2 | **米国株 売買手数料は `要確認`**（moomoo の体系が未登録）。売却時諸費用（SEC・TAF）だけ暫定値が在る | **`Commission` は推計の式そのものが計画に無い**。作れるのは売りの `Fee` だけ | 影響なし |
| FR-11 ／ ADR-0016 決定15 | 取引記録は経費区分 7 種を持ち建玉単位で紐づく。「推計」を示す軸は定めていない（由来の軸は ADR-0041 の手動売買取り込みだけ） | 推計と実績を区別して運ぶ欄が契約（`TradeExpense`）に無い | 影響なし |
| ADR-0041 決定1（04_report-templates 変更履歴 2026-09-19） | 手動売買の実現損益を記録しない理由は「**推定を確定値から分離して運ぶ通り道が無い**」こと | 同型の通り道の欠如がある（7 年保持の台帳へ単一の数値として流れる） | 合致 |
| ADR-0016 決定4 | 強制買戻しの「推定」は**計画 ADR が明示的に認めた**うえで「推定」と表示する | 計画が推定を認めるには ADR 単位の決定が要るという前例 | — |

**結論: 計画は経費の推計を取引記録へ積むことを定めていない。未供給の扱い（0 と書かない・未供給と明示する）は一意に定めている。**
よって本 IADR は案 2 だけを実装し、案 1 は利用者の裁定（必要なら計画への環流）へ残す。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **案 2（採用）** | SIMULATE の間は未計上（7 区分 `LineCount` = 0・イベント 0 本）のまま。取得できない理由を**発注先ごとに**明示する | 計画（ADR-0027 決定4・ADR-0035 決定3）と合致。IADR-0300 の構造（2 状態の結果型・推定しない）を変えない |
| 案 1 | 前提条件の手数料体系から推計し、「推計」と明示して `TradeExpenseRecorded` を積む | 計画に根拠が無い。`Commission` は料率が `要確認` で式が作れない。`TradeExpense` に推計の欄が無く、足すと契約イベントの破壊的変更（IADR-0300 決定6）。報告書の費用は既に概算費用関数（`CostCalculator`）で出ており、台帳へ同じ概算を写しても情報は増えない |
| 案 3 | 実弾口座のヘッダで読み取り専用に照会する | #1000（読み取り専用の Real 照会環境）と同じく**別の環境の判断**。本 IADR の範囲外 |

理由の表し方について:

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **R-1（採用）** | 合成起点で発注先（`BrokerSelection.ToBrokerProvider()`）から理由を 1 度だけ決める（`UnsuppliedOrderExpenseSource.For`） | 新しい構成キーを作らない（IADR-0300 決定7）。既存の 3 値写像（IADR-0140 決定6）をそのまま使う |
| R-2 | 固定文言を SIMULATE 向けに書き換えるだけ | 内蔵 paper・moomoo REAL でも「SIMULATE では提供されない」と出て、別の誤りになる |
| R-3 | 理由を列挙型にして `OrderExpenseLookup` へ持たせる | 理由で分岐する呼び手が無い（診断用の文字列）。ポートの型を変える費用に見合わない |

## 決定

1. **SIMULATE の間、経費は未計上のままとし、推計を積まない**（案 2）。`TradeExpenseRecorded` は 1 本も出さず、
   建玉の 7 区分集計は `LineCount` = 0 のまま（IADR-0300 決定3・決定6 を変えない）。
2. **取得できない理由は発注先ごとに明示する**（R-1）。

   | 発注先 | 理由（`UnsuppliedOrderExpenseSource` の定数） |
   | --- | --- |
   | moomoo SIMULATE | `MoomooSimulateReason` —— Trd_GetOrderFee は SIMULATE 口座では提供されない（retMsg 'Paper trading is not supported.'・2026-10-02 実測）。推計でも埋めない |
   | 内蔵 paper | `InternalPaperReason` —— 外部へ発注しない擬似約定でありブローカーの経費明細が存在しない。推計でも埋めない |
   | moomoo REAL | `Reason`（従来の「未接続」）—— 照会は成立し得るが供給ポートへ未接続（段 2 の残り） |

   未知の発注先は既定へ倒さず例外にする。
3. **Program.cs は `UnsuppliedOrderExpenseSource.For(brokerSelection.ToBrokerProvider())` を登録する。** 引数なしの既定（未接続）を登録しない —— 本番の組み立てを `WebApplicationFactory<Program>` の試験で固定する。
4. **警告ログの形（`TradeExpenseRecordingLog`）・駆動点・ポートの型は変えない。** 変わるのは警告の「理由=」の中身だけである。
5. **報告書（report-service）には触らない。** 費用 0 を「負担なし」と書かない表示は #1156 の範囲である。

## 理由

- **計画が一意に定めるのは未供給の扱いだけである**（前掲の突き合わせ）。推計の記録は計画に根拠が無く、`Commission` の料率は `要確認` のため式も無い。
- **誤った理由は誤った運用判断を招く。** 「未接続」は「結線すれば取れる」と読めるため、SIMULATE の間に段 2 の結線を急ぐ判断を誘う。実測で分かった事実（ブローカーが拒否する）を警告そのものに載せる。
- **理由の決定点は合成起点の 1 か所に置く。** 発注先は既に `BrokerSelection` が 1 度だけ解決しており（IADR-0111）、新しいフラグは要らない。

## 結果

- **良い影響**: SIMULATE の約定ごとの警告が、取れない本当の理由（ブローカーの拒否）と「推計でも埋めない」ことを述べる。内蔵 paper でも理由が正しくなる。
- **悪い影響・トレードオフ**:
  - **SIMULATE の期間（Stage 1）の経費の実績は永久に残らない。** ADR-0016 決定15 の「記録は遡って復元できない」に照らすと失われる情報だが、
    SIMULATE の費用は仮想でありブローカーも返さないため、失われるのは「返らなかった」という事実だけで、それは警告ログに残る（保持はログ基盤依存。IADR-0300 決定6 のまま）。
  - 報告書の費用は引き続き概算費用関数（`CostCalculator`）に依存する（本 IADR は変えない）。
- **フォローアップ（利用者の裁定が要るもの）**:
  - 推計を取引記録へ「推計」として積むか（案 1）。積むなら計画への環流が要る（FR-11 に推計の軸が無い・米国株売買手数料が `要確認`）。
  - 実弾口座での読み取り専用の費用照会（案 3）を #1000 の Real 照会環境へ載せるか。
  - 未供給の事実を 7 年保持の監査台帳へ残すか（IADR-0300 決定6 はログに留めた）。

## 関連

- Supersedes: なし（IADR-0300 の決定 1〜10 は不変。段 2 の「結線」は moomoo REAL に限って残る）
- Superseded by: なし
