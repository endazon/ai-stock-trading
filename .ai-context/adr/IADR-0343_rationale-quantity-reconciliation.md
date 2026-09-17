---
title: IADR-0343 数量はシステムが決めると本判断プロンプトで明示し、発行する記録では根拠文の株数言及がシステムの数量と食い違えば LLM の文言を保ったまま決定的な注記を追記する
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-10, FR-11, ADR-0003, ADR-0033, ADR-0040, IADR-0003, IADR-0029, IADR-0039, IADR-0119, IADR-0297]
author: endazon (with Claude Code)
created: 2026-09-17
updated: 2026-09-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md
  - planning:projects/ai-stock-trading/00_vision/00_vision.md
related_specs:
  - ../specs/20260917_822_rationale-quantity-consistency.md
---

# IADR-0343: 数量はシステムが決めると本判断プロンプトで明示し、発行する記録では根拠文の株数言及がシステムの数量と食い違えば LLM の文言を保ったまま決定的な注記を追記する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-17
- 決定者: endazon（[#822](https://github.com/endazon/ai-stock-trading/issues/822)）/ Claude Code（起案）

## 起点・関連

- 計画 ADR-0040 決定 5「方針文の散文は数量を拘束しない」・フォローアップ 4「LLM の根拠文の数量とサイジングの結果を突合する経路を作る（**方式は定めない**）」。
- 実測（2026-09-16〜17・稼働クラスタのログ）: 本判断の根拠文「…リスク制約内で1株単位の新規買いが可能と判断…」に対し `定時判断: … AAPL Buy 数量=849`。計画 00_vision の「米国株（1株単位）」（売買単位）を LLM が数量の指示と読んだ（ADR-0040 実測 6）。
- サイジングの所在: IADR-0003 / IADR-0029（判断サービス・`PositionSizer`）。決済の数量＝保有全量: IADR-0119。
- 作業仕様書: `.ai-context/specs/20260917_822_rationale-quantity-consistency.md`（生成点・消費点の母集合と除外理由）。

## コンテキスト

`TradeDecisionMade.Rationale` は LLM の自由文をそのまま運び、監査台帳（`AuditEntryFactory`）と報告書（`HttpTradeRationaleSource` → 取引履歴・損益の帰属）の唯一の供給元である。数量はサイジング（統制値）が決め、根拠文は数量を決めない —— にもかかわらず根拠文が株数を書くと、**記録の数量と記録の根拠が食い違い、後から判断を再現できない**。

## 決定

### 決定1: 本判断プロンプトのリスク制約節で、数量はシステムが決めること・「1株単位」は上限でないことを明示する

`TradeDecisionPromptBuilder.Build` の `# リスク制約` 節末尾に 2 行を無条件で足す（`public const` の `QuantityIsSystemDecidedRule` / `TradingUnitIsNotCapRule`。テストが const を直接参照する＝IADR-0297 決定1 と同じ規律）。

- 発注数量は判断の後にシステムが統制値から算出する。rationale で株数に言及しない。
- 方針にある「1株単位」等は売買単位であり数量の上限ではない。

**一次スクリーニング（`BuildScreening`）には足さない** —— リスク制約節を持たず、その根拠文は記録へ載らない（ログのみ）。費用統制（IADR-0039）で一次の文言を最小に保つ。

### 決定2: 発行点で根拠文とシステムの数量を突合し、食い違えば注記を追記する（文言は書き換えない）

純関数 `Domain/RationaleQuantityReconciler` を置き、`TradeDecisionAppService` が `TradeDecisionMade` を作る 2 か所（新規建て＝サイジング結果・決済＝保有全量）で掛ける。

- **検出**: 数字（半角・全角・桁区切り）または「一」＋「株」、英語の `N share(s)` / `one share`。**単価表現は除く**（直後が `あたり` / `当たり` / `当り` / `につき` / `価` / `式` / `主` / `券`、`share price`）。直前が数字・小数点の一致は拾わない。
- **判定**: 言及が 1 つでも数量と異なれば不一致。言及なし・全一致は原文のまま（既存の記録と完全一致）。
- **注記**: 原文 ＋ 半角空白 ＋ `［システム注記: 数量はシステムが統制値・保有数から決めた {N} 株であり、根拠文中の株数「…」は数量を拘束しない］`。全角角括弧の `NotePrefix` で始め、**LLM の文言と機械的にも人の目にも区別できる**。決定的（同じ入力から同じ文字列）で、既に注記を含む文には二重に付けない。
- 不一致時は WARN ログを 1 行出す（銘柄・数量。根拠文そのものは既存の `LLM 判断:` ログが持つ）。
- **検出は保守側に倒す** —— 取りこぼし（食い違う記録が残る）より、誤検出（無害な注記が 1 つ増える）を選ぶ。「1株単位」は文脈上売買単位でも拾う（実測の誤読そのものであり、注記が数量を明示して無害化する）。

発行点で直すため、監査台帳の要約・Detail JSON と報告書の根拠（`ITradeRationaleSource`）は**改修なしで**注記つきの文を受け取る。Discord 通知とフロントエンドは根拠文を扱わない（走査 0 件）。

### 決定3: Stage 0 記録の多数決根拠にも同じ突合を掛ける

`Stage0DecisionRecorder` の `MajorityRationale` を `|SignedQuantity|` と突合する（Hold は数量を持たないため対象外）。**各票の生の判断（`Stage0RawDecision.Rationale`）は出力そのままで残す**（ADR-0033 決定4 が残させた生の記録であり、票単位の数量を持たない）。`Rationale` は `Stage0StrategyIdentity` の同一性に入らないため、戦略 ID は変わらない。

### 決定4: 契約・サイジング・統制は変えない

`TradeDecisionMade` にフラグ項目を足さない（注記の有無は `NotePrefix` で判別でき、追加項目は下流の全消費者に意味づけを要求する）。`PositionSizer`・リスク管理・`OrderApproved`・発注執行・保護逆指値は触らない。

## 棄却した案

| 案 | 棄却理由 |
| --- | --- |
| 根拠文中の株数をサイジング数量へ置換する | LLM の言葉を黙って書き換える＝記録の改竄。どこがシステムの手かが読めなくなる |
| プロンプトの明示だけ | LLM 出力は揺れる。受け入れ基準「発行される記録で食い違わない」をテストで固定できない |
| 不一致なら判断を Hold に倒す | 散文は数量を拘束しない（ADR-0040 決定5）。根拠文の書き方で取引の成否を変えるのは統制の所在を散文へ戻すことになる |
| 契約へ `RationaleQuantityMismatch` 等の任意項目を足す | 監査・報告書・MarketMonitor・RiskManagement の全消費者へ波及し、表示側の追随が要る。注記で読み手に届けば足りる |
| 常に「数量 N 株」を併記する | 言及のない大多数の記録まで変わり、既存の記録・テストとの差分が広がる。最小の変更に留める |

## 結果

- 良い点: 記録の数量と根拠が食い違ったまま残らない（テストで固定）。LLM の文言は保たれ、システムの手が明示される。プロンプト側で誤読そのものも減る見込み。
- 突然変異の証跡: 突合の検出を空リストへ潰すと **5 件が赤**（Reconciler 2・AppService 2・Stage0 1）。
- 残余リスク:
  - 🔴 **注記の数量はサイジング（または保有全量）の数量であり、リスク管理が承認時に減らした数量（`OrderApproved.ApprovedQuantity`）ではない** —— 承認で減量された場合、監査台帳では `TradeDecisionMade` と `OrderApproved` の行が別の数量を持つ（従来どおり。本 IADR は判断記録内の整合だけを扱う）。
  - 漢数字（「百株」「十株」）・「数株」・英語の綴り数（`two shares`）は検出しない（`一` と `one` のみ）。
  - 本判断プロンプトの文言が変わるため、**Stage 0 記録の入力指紋（プロンプトの SHA-256）は本変更以降の記録で変わる**（プロンプト変更の常として。既存記録は不変）。
  - 稼働で LLM が株数言及を止めるかは未観測（デプロイ後のログで確認する）。
