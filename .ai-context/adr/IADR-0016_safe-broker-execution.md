---
title: IADR-0016 発注執行は安全既定（ペーパー）とし、moomoo 実発注は PoC まで構成でゲートして実弾を撃たない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-12, ADR-0002, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# IADR-0016: 発注執行は安全既定（ペーパー）とし、moomoo 実発注は PoC までゲートする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・方針指示「実弾は撃たない」）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-05（発注）、FR-12（ペーパー）、ADR-0002（**Proposed**: moomoo は OpenD PoC 成功が Accepted 条件）、ADR-0003（承認済み注文のみ発注）
- 対象 Issue: [#13](https://github.com/endazon/ai-stock-trading/issues/13)
- 関連する実装仕様書: [20260710_order-execution](../specs/20260710_order-execution.md)
- 関連 IADR: [IADR-0007](IADR-0007_broker-rejection-vs-risk-rejection.md)（証券会社拒否）、[IADR-0013](IADR-0013_platform-foundation-testsupport-shim.md)（shim）

## コンテキストと課題

発注執行サービスは承認済み注文を実際にブローカへ送る。moomoo OpenAPI は ADR-0002 で **Proposed**（一次確認＋デモ取引
`TrdEnv.SIMULATE` の PoC 成功が Accepted 条件）であり、OpenD ゲートウェイの常駐という運用要素も未確立。資金を扱う以上、
「実弾（実口座での実発注）を誤って撃つ」ことは絶対に避けねばならない（利用者方針「実弾は撃たない」）。どのブローカ実装を
既定にし、moomoo をどう扱うかを決める必要がある。

## 検討した選択肢

1. **moomoo 実発注アダプタを本 Slice で実装し構成で切替** — ADR-0002 が Proposed・OpenD PoC 未了・CI で検証不能・実弾リスク。
   時期尚早で危険。
2. **安全既定＝ペーパー、moomoo は構成でゲート（未実装）** — 既定は `PaperBrokerAdapter`（参照価格で即時約定・実発注しない）。
   `Broker:Provider=moomoo` を選ぶと**起動時に明示的な例外で停止**し「OpenD PoC 完了・ADR-0002 Accepted まで利用不可」を告知する。
   実 moomoo アダプタは PoC 連動の後続で実装する。
3. **moomoo を `TrdEnv.SIMULATE` 固定で実装** — デモでも OpenD 常駐・SDK 依存・CI 非対応で本 Slice の範囲を超える。将来 PoC で実施。

## 決定

選択肢 2 を採用する。

- 発注執行の**ブローカ既定はペーパー**（`PaperBrokerAdapter`）。判断・記録・報告のフローは実発注と同一に保つ（FR-12 の検証価値）。
- ブローカ選択は構成 `Broker:Provider`（既定 `paper`）。`moomoo` を選ぶと**起動時に安全に停止**する（実弾防止ゲート）。中途半端に
  発注し得る moomoo 実装は本 Slice では置かない。
- moomoo 実発注アダプタ（OpenD・C# SDK・`TrdEnv.SIMULATE` PoC）は ADR-0002 の PoC 完了・Accepted 化と連動する後続で実装する。
  実口座（`TrdEnv.REAL`）での発注は、PoC 完了＋利用者の明示承認＋段階ゲート（FR-20 Stage2 以降）が揃うまで行わない。
- 証券会社拒否は `OrderStatus.Rejected`（IADR-0007）で表し、発注前拒否（`OrderRejected`）と区別する。

## 理由

- ADR-0002 が Proposed であり、PoC 未了の段階で実発注コードを持つこと自体が実弾リスク。安全既定＝ペーパー＋moomoo ゲートで、
  「誤って実弾を撃つ」経路を構造的に塞げる。
- ペーパーで発注執行のパイプライン（購読→発注→約定イベント→永続化）を完成させれば、moomoo は PoC 後に差し替えるだけでよい
  （`IBrokerAdapter` で抽象化済み・ADR-0002）。

## 結果

- 良い影響: 実弾を撃つ経路が存在しない（moomoo 選択は起動停止）。パイプラインはペーパーで完成し CI で検証できる。
- 悪い影響・トレードオフ: 本 Slice では実発注できない（意図どおり）。moomoo アダプタ・PoC は後続に持ち越し。
- 冪等性: 消費者再配送（`UseAiStockTradingRetry`）での二重発注を防ぐため、`DecisionId` をキーに既存の発注結果があれば
  再発注せず既存結果を再発行する。~~**残存窓**: ブローカ発注は成功したが永続化（`Save`）前に失敗した場合は記録が無く、
  再試行で再発注し得る~~ → **解消済み**（2026-07-16・#131 /
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md)）。発注前 `DecisionId` 予約による3相化（予約→発注→確定）で
  当該窓を塞いだ。ただし予約が `Reserved` のまま残った注文の**自動リコンサイルは未実装**（#141）。
- フォローアップ: ADR-0002 PoC（デモ発注・**完了**＝IADR-0056）→ moomoo アダプタ実装（**完了**＝IADR-0056）→
  発注の冪等化（**完了**＝IADR-0057）→ Vault 秘匿 → 段階ゲートと連動した実弾解禁の設計。
  実弾（`TrdEnv_Real`）は**引き続きゲート**しており、解禁には別 IADR＋明示 config を要する（IADR-0056 §3）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [ADR-0002], [IADR-0007](IADR-0007_broker-rejection-vs-risk-rejection.md)
