---
title: moomoo SIMULATE では注文費用照会が使えない前提で、経費を未計上のまま推計を積まず、取得できない理由を発注先ごとに明示する（#1086 段 2 の設計し直し）
type: spec
status: accepted
related_ids: [FR-11, FR-16, FR-17, UC-07, ADR-0016, ADR-0027, ADR-0035, ADR-0041, IADR-0484, IADR-0300, IADR-0226, IADR-0140]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0027_borrow-fee-accrual-recording.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0035_cost-ratio-denominator-and-cost-total-composition.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: SIMULATE では経費を未計上と明示する（#1086 段 2 の設計し直し）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-11（取引記録の経費区分 7 種・建玉単位）／FR-16（費用はコードで集計）／FR-17（前提条件）
- ユースケース（UC）: UC-07
- 画面（SC）: なし
- 関連 ADR: ADR-0016 決定15／ADR-0027 決定4（未供給は 0 として積まない）／ADR-0035 決定3・決定5（未供給の区分は「未供給」と描き過小である旨を明記）／ADR-0041 決定1
- 関連する実装ADR: IADR-0484（本作業）／IADR-0300（経費の取得ポート）／IADR-0226 決定7／IADR-0140 決定6
- 計画の参照: 隣接クローン `../project-planning`（`origin/main` b9d0d27 まで fetch。経費・手数料に関する差分は無いことを確認）

## 目的・背景

#1086 段 1 の検証口を利用者が 1 回実行した（2026-10-02）。`Trd_GetOrderFee` は `retType=-1 retMsg=Paper trading is not supported.` で失敗した。
**moomoo の注文費用照会は SIMULATE 口座では使えない。** 第 2 段は SIMULATE の間は実績が取れない前提で設計し直す。
候補は案 1（手数料体系から推計し「推計」と明示して記録）と案 2（SIMULATE では未計上と明示）。

## 計画との突き合わせ（結論）

詳細は IADR-0484 §計画との突き合わせ。要点:

- ADR-0027 決定4・ADR-0035 決定3／04_report-templates §数値の定義「費用合計」: 取れないものは「未供給」と明示し 0 と書かない → **案 2 と合致**。
- 計画は経費の**推計を取引記録へ積むこと**を定めていない（FR-11 に推計の軸が無い。ADR-0041 は推定を確定値から分離する通り道が無い値を記録しなかった）。
- 05_trading-assumptions §2: **米国株 売買手数料は `要確認`** → 案 1 の `Commission` は式が作れない。
- よって**案 2 は計画と整合して一意に決まる。案 1 は計画に根拠が無く、利用者の裁定へ残す。**

## スコープ

### 対象（やること）

- `UnsuppliedOrderExpenseSource` に発注先ごとの理由（moomoo SIMULATE／内蔵 paper／moomoo REAL）と `For(BrokerProvider)` を足す。
- `Program.cs` の登録を `UnsuppliedOrderExpenseSource.For(brokerSelection.ToBrokerProvider())` へ替える。
- 試験（単体＋`WebApplicationFactory<Program>` の組み立て）と自己変異。IADR-0484・索引行・IADR-0300 への追記。

### 対象外（やらないこと）

- 推計の記録（案 1）・実弾口座での照会（#1000 と同じ別環境の判断）。
- 報告書（report-service）の表示。費用 0 を「負担なし」と書かない是正は #1156 の範囲であり、**report-service のファイルには触らない**。
- 警告ログの文面の形（`TradeExpenseRecordingLog`）・駆動点・ポートの型・契約イベント。
- 検証口の手順書（`docs/operations/order-fee-probe-runbook.md`）への実測結果の追記（別 PR で可）。

## 母集合（規則 9）

引き方: `git grep -n -E "UnsuppliedOrderExpenseSource|IOrderExpenseSource" -- backend`（origin/develop 10a32a8e）。

| 位置 | 扱い |
| --- | --- |
| `Program.cs` の登録 1 か所 | **是正**（発注先から理由を決める） |
| `UnsuppliedOrderExpenseSource.cs` | **是正**（理由 3 つと `For`） |
| 試験の DI 差し替え（`OrderApprovedConsumerTests` ほか 7 か所の `AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>()`・`new UnsuppliedOrderExpenseSource()`） | 対象外。引数なしの既定（未接続の理由）は残すため、そのまま通る |
| `IOrderExpenseSource.cs` / `TradeExpenseRecordingService.cs` / `TradeExpenseRecordingLog.cs` | 対象外（型・駆動・ログの形は変えない） |

## 受け入れ基準

- [x] moomoo SIMULATE 構成の本番の組み立てで、経費の供給口は「取得できない」を返し、理由は SIMULATE で照会が提供されないこと（と推計で埋めないこと）を述べる。
- [x] 内蔵 paper 構成では、理由はブローカーの経費明細が存在しないことを述べる。
- [x] moomoo REAL は従来の「未接続」の理由を返す。未知の発注先は例外。
- [x] どの発注先でも供給（空の明細を含む）は返さない（推計を積まない）。
- [x] 既存の試験（引数なしの既定を使うもの）は変更なしで通る。

## 試験と変異

- `UnsuppliedOrderExpenseSourceTests`: 発注先ごとの理由（Theory 3 件）・3 理由の相異・未知の発注先の例外。
- `OrderExpenseSourceCompositionTests`（新規）: `WebApplicationFactory<Program>` で moomoo（sim）・paper を組み、登録された供給口の理由を固定する。
- 自己変異（いずれも赤を確認）: M1 Program.cs を引数なしの既定登録へ戻す／M2 SIMULATE の理由を未接続へ／M3 未知の発注先を既定へ倒す／M4 paper の理由を取り違える。

## 並行作業との衝突（#1156）

本作業が触るのは `backend/Services/OrderExecutionService/` 配下と `.ai-context/` だけで、report-service は触らない。
