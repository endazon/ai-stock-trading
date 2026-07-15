---
title: IADR-0056 moomoo SIMULATE PoC 完了に基づき実アダプタを実装（実弾は引き続きゲート）
type: impl-adr
status: Accepted
related_ids: [FR-05, ADR-0002, IADR-0016, IADR-0053]
author: endazon (with Claude Code)
created: 2026-07-15
updated: 2026-07-15
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
---

# IADR-0056: moomoo SIMULATE PoC 完了に基づき実アダプタを実装（実弾は引き続きゲート）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-15
- 決定者: endazon（利用者・方針「実弾は撃たない・SIMULATE 前提」）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-05**（発注執行）、**ADR-0002**（moomoo OpenAPI・**Proposed**: デモ取引 `TrdEnv.SIMULATE` の PoC 成功が Accepted 条件）
- 対象 Issue: [#13](https://github.com/endazon/ai-stock-trading/issues/13)（moomoo アダプタ）/ [#124](https://github.com/endazon/ai-stock-trading/issues/124)（OpenD 常駐）
- 関連 IADR: [IADR-0016](IADR-0016_safe-broker-execution.md)（安全既定 paper・moomoo ゲート）、
  `IADR-0053`（OpenD Docker/常駐。別ブランチ feat/124-opend-docker / PR #126 で追加。develop 未マージのため
  本ブランチにはファイル無し＝リンクにしない。マージ後にリンク化する）
- 関連仕様書: [20260715_13_moomoo-broker-adapter](../specs/20260715_13_moomoo-broker-adapter.md)

## コンテキストと課題

[IADR-0016](IADR-0016_safe-broker-execution.md)（Accepted）は「moomoo 実発注アダプタは **ADR-0002 の PoC 完了・Accepted 化と連動する後続で実装する**」とゲートしていた。当時 ADR-0002 は Proposed で、その Accepted 条件である「デモ取引（`TrdEnv.SIMULATE`）での PoC 成功」も、OpenD 常駐（#124）も未確立だった。

2026-07-15 に前提条件が揃った:

- **OpenD 常駐が確立**（#124 / PR #126）。RSA 暗号化で in-cluster（cross-network）trade 接続が成立。
- **SIMULATE PoC が成功**（本セッションの live 検証）。実 OpenD の SIMULATE 口座（accId=724808）に対し、
  接続（暗号化）→ 口座取得 → **発注（`TrdEnv_Simulate`）→ 状態追跡 → 取消** の一巡を確認した。

これは ADR-0002 の Accepted 条件（デモ取引 PoC 成功）に**まさに該当する事実**である。一方で ADR-0002 の Accepted 化そのものは上流（`project-planning`）の意思決定であり、本実装リポジトリからは直接変更できない。ゲート（IADR-0016）を維持したまま実装を止め続けるべきか、PoC 完了を根拠に実装を進めるべきかを決める必要がある。

## 決定

**SIMULATE PoC の成功をもって、moomoo 実アダプタ（`MMApiMoomooTradeClient`）を実装する。ただし実弾（`TrdEnv_Real`）は引き続き撃たない。**

1. **PoC 完了の記録**: 上記 live 検証（2026-07-15）が ADR-0002 の Accepted 条件（SIMULATE デモ取引成功）を満たすことを、本 IADR と仕様書で記録する。IADR-0016 のゲート趣旨（「PoC で実証されるまで実発注経路を作らない」）は、SIMULATE PoC の成功により充足された。
2. **SIMULATE 固定を維持**: 実アダプタは `TrdHeader` に `TrdEnv_Simulate` を固定する。`OrderIntent.Mode=Live` でも SIMULATE で発注する（IADR-0016 の二重化した実弾防止＝BrokerFactory の config ゲート＋SIMULATE 強制を維持）。
3. **実弾（`TrdEnv_Real`）解禁は別 IADR＋明示 config を要する**。解禁の前提として、少なくとも次を満たすこと:
   - 発注の**冪等化**（outbox / 発注前 `DecisionId` 予約行）。現状の冪等性は発注後の `DecisionId` 照合のみで、「ブローカ発注成功→永続化失敗」の窓が未保護（`OrderExecutionService.cs` にコメント済・IADR-0016 の後続）。
   - リスク統制・監査・上限（`TradingDefaults`）の実弾向け再確認、秘匿情報の Vault 化。
4. **上流への環流**: ADR-0002 の Accepted 化は上流（`project-planning`）の triage に委ねる。PoC 結果・無人運用の追検証は
   plan-feedback 記録（`feedback/20260715_adr0002-opend-unattended-limited.md`。**別ブランチ feat/124-opend-docker /
   PR #126 に存在**・develop 未マージのため本ブランチには無い）で AST 側に起票済み。上流計画リポへの Issue 化
   （`project-planning`）は本 PR 群のマージ後に人手で行う（未完＝「環流予定」）。ADR-0002 が Accepted 化されるまでの間も、
   本 IADR を根拠に **SIMULATE 限定での実装・利用**を可能とする。

## 影響

- **肯定的**: #13 の実アダプタが正当な根拠（PoC 成功）の下で実装・利用可能になる。実弾防止は SIMULATE 固定＋config ゲートで維持。
- **制約**: 実弾解禁には別 IADR＋冪等化等の前提充足が必要（本 IADR では解禁しない）。ADR-0002 が Proposed の間は「SIMULATE 限定」という但し書きが付く。
- **可搬性**: ADR-0002 が Accepted 化されたら、本 IADR の「SIMULATE 限定」条項は実弾解禁 IADR に引き継ぐ。

## 備考

本 IADR は「ADR-0002 Proposed のまま実アダプタが完成している」という状態を、**PoC 完了を根拠に正当化しつつ実弾はゲートし続ける**ことで解消する。実装を止める（ゲート維持）と PoC 完了の成果を活かせず、無条件に進める（ゲート撤廃）と実弾リスクが上がる——その中間として「SIMULATE 限定で進め、実弾は別途」を選んだ。
