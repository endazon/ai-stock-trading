---
title: IADR-0249 情報収集の縮退（BlocksNewEntries）はリスク管理の判定コアで新規建てを止める（決済は構造的に通る）
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-02, FR-10, ADR-0003, ADR-0009, ADR-0020, IADR-0163, IADR-0221, IADR-0249]
author: claude (Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
---

# IADR-0249: 情報収集の縮退はリスク管理の判定コアで新規建てを止める

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（起票 #337。PR #556〔#336〕の仕様書が明記した引き継ぎ拘束の消化）

## 起点・関連

- 関連する計画書 ID: FR-01・FR-02・FR-10・ADR-0020 決定2/決定3（限定縮退は新規建てのみ停止）
- 関連する実装仕様書: [`20260828_337_trading-cycle-and-screening.md`](../specs/20260828_337_trading-cycle-and-screening.md)・
  [`20260828_336_information-collection-tiers-and-degradation.md`](../specs/20260828_336_information-collection-tiers-and-degradation.md)

## コンテキストと課題

#336 は欠測の判定器（`DegradationEvaluator`）と遷移イベント（`InformationSourceDegraded` /
`InformationSourceRecovered`）を作ったが、**`BlocksNewEntries` の下流参照はゼロ**（実測）だった。
新規建ての抑止は KB の欠測文言を判断 LLM が読んで自制することに委ねられており、コードレベルの
強制が無かった（IADR-0221「結果」・#336 仕様書が「構造的な結線は #337 の射程」と明記）。

## 検討した選択肢

1. **TradeDecisionService で判断前にゲートする** — LLM 費用は節約できるが、建玉効果（Open/Close）が
   LLM 判断後にしか分からず、決済まで塞ぐか、判断側に統制の複製を持つことになる（為替鮮度切れで
   「入口と出口が同じゲートで塞がれていた」IADR-0197 の形）。**却下**。
2. **RiskManagementService の判定コアで止める**（採用）— ADR-0003 の設計原則（リスク管理は AI から
   独立した決定的な強制ポイント）どおりの位置。isEntry 短絡で決済は構造的に通る。
3. **OrderExecutionService で止める** — 拒否理由が監査（OrderRejected）に載らず、事前拒否と
   ブローカー拒否の区別（FR-05）も壊す。**却下**。

## 決定

1. **リスク管理が遷移イベントを購読し、カテゴリ集合として畳む**（`IInformationDegradationStore`）。
   **`BlocksNewEntries=true` の Degraded だけを登録**する——記録のみ／空売り限定の縮退で全新規建てを
   止めない（受け手が Behavior を再解釈して停止範囲を広げない。イベント自身の宣言に従う）。
   Recovered で除去し、**残があるあいだ停止が続く**（複数カテゴリの AND 解除）。
2. **判定は `RiskEvaluator` の isEntry 位置**（kill switch / pause と同じ）。拒否理由は
   `RejectionReason.InformationSourceDegraded`（**序数 28・末尾追加**〔IADR-0134 決定2〕・**クラス B**
   ——「取引を止めている状態そのものの記録」。市況由来の事象をクラス C へ混ぜると段階昇格ゲートが壊れる）。
   **手仕舞い・損切りは isEntry の短絡で構造的に通る**（否定形テストで固定）。
3. **縮退状態は `PortfolioSnapshotBuilder` の必須依存**（IADR-0163 決定2「不在が統制の無効を意味する
   依存は必須にする」——省略可能引数だと配線を削ってもコンパイルが通り、停止だけが静かに効かなくなる）。
4. **状態はプロセス内（非永続・singleton）。** 発行側（収集サービスの `DegradationStateTracker`）も
   プロセス内であり、受け手だけを永続化しても再起動時の取りこぼしは解消しない。

## 結果

- 良い影響: ADR-0020 の限定縮退が LLM の自制ではなく決定的コードで強制される（ADR-0003 の最終防衛線
  と同じ位置）。
- 残余リスク（fail-open 側・明示）: **縮退継続中にリスク管理サービスが再起動すると、次の遷移
  （回復→再欠測）まで停止状態が届かない。** 収集側 tracker も同じ形（プロセス内）であり、解消には
  再送（定期スナップショット発行）か収集側の永続化が要る——別 issue の射程とし、ここに記録する
  （「統制を定めた」と「統制が働いている」を読み分けられる形で残す・planning#286 の裁定と同じ向き）。
  moomoo 可用性（`BrokerAvailabilityObserved`）の収集側への引き込み（#336 仕様書の残件）は本結線とは
  独立であり、未消化のまま残る。
