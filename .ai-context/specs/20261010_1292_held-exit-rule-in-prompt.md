---
title: 取引判断のプロンプトの保有状況節に「方針の銘柄の列挙は新規建ての対象。保有の手仕舞いは常に判断する」を固定文で置く（#1292）
type: spec
status: accepted
related_ids: [FR-04, FR-10, FR-02, UC-01, ADR-0003, IADR-0523, IADR-0521, IADR-0351, IADR-0440, IADR-0470]
author: claude (Claude Code)
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-01 基本フロー 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 取引判断のプロンプトの保有状況節に、保有の手仕舞いを常に判断する固定文を置く（#1292）

## 起点

- [#1292](https://github.com/endazon/ai-stock-trading/issues/1292)。オーナー裁定（2026-10-10）: 取引判断のプロンプトに
  「確定済み方針の銘柄の列挙は新規建ての対象を定める。保有中の銘柄の手仕舞い（利確・損切り）は、列挙に関係なく常に判断する」を固定の規則として入れる。
  方針の文の書き方（運用）には頼らない。
- 観測（PoC 2026-10-09 US・AST 586902eb）: 保有中の AAPL・MSFT・AMZN・GOOGL を監視銘柄へ戻した後も、一次スクリーニングの LLM は 3 サイクルとも Hold。
  根拠は「確定済み方針の監視銘柄 8 銘柄に含まれていない…取引対象外」「売却検討も本方針に基づかないため本判断対象外」。保有状況は渡していた。
- 関連: #1286（PR #1288・IADR-0521。監視銘柄の外の保有銘柄を出口専用で判断に回す）。

## 計画の確認

- FR-04・ADR-0003: AI は確定済み日報の方針とリスク制約の範囲内で判断する。判断入力は「確定済み日報＋保有ポジション＋収集情報＋過去判断の RAG」。
- UC-01 基本フロー 3: 判断の文脈に保有ポジションを含む。
- 方針の銘柄の列挙が保有の手仕舞いまで限定するという定めは計画に無い。裁定は実装（プロンプト）の解釈の固定であり、計画への環流は要らない。

## 現況（origin/develop 24dc448b）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 方針の本文は `# 確定済み日報の方針（日付）` の見出しの下にそのまま入る。「取引対象」等のラベルは付けていない | `TradeDecisionPromptBuilder.Build` / `BuildScreening` |
| 2 | 冒頭に「方針の範囲外・不確実な場合は必ず Hold」（本判断）／「方針の範囲外・関心なし・不確実な場合は必ず Hold（見送り）」（一次）がある | 同上 |
| 3 | 保有状況節（本判断）は出口の規則（`ExitFollowsPolicyRule` ほか）を持つが、方針の銘柄の列挙と出口の関係は書いていない | `AppendHeldPositionSection` |
| 4 | 保有状況節（一次）は `ScreeningHeldRule`（手仕舞いの検討に値する場合も本判断へ進める）を持つ。列挙との関係は書いていない | `AppendHeldPositionSectionShort` |
| 5 | 出口専用の判断（#1286）は監視銘柄節の末尾に `ExitOnlyLine` を足す。方針の列挙との関係は書いていない | `WatchlistSection` |

## やること

1. `TradeDecisionPromptBuilder.HeldExitAlwaysJudgedRule`（固定文）を足し、保有あり（`IsHeld`）のとき、本判断の保有状況節（保護の状態の直後）と
   一次の保有状況節（保有の行の直後）の両方に出す。保有なし・不明・未約定だけでは出さない。
2. 一次の縮退の見積り（`ScreeningContextAssembler`）に固定文の行の予約 `HeldExitRuleReserveChars`（150 文字）を銘柄ごとの保護分として足す。
3. 試験（T-10-2508〜T-10-2510）・テスト仕様書・IADR-0523。

## 母集合（規則 9・10）

### 方針の銘柄の列挙を取引の限定として読ませ得るプロンプトの文言

走査: `git grep -nE '方針の範囲外|範囲内でのみ|方針外|取引してよいか|取引対象' -- backend ':!*Tests*'`。

| 箇所 | 扱い |
| --- | --- |
| `TradeDecisionPromptBuilder.cs` 本判断の冒頭「あなたは確定済み日報の方針とリスク制約の範囲内でのみ判断する取引アシスタントです。」「方針の範囲外・不確実な場合は必ず Hold（取引しない）を選びます。」 | **据え置き**（ADR-0003 の判断の枠そのもの。固定文が「列挙に含まれないことは、方針の範囲外として手仕舞いを見送る理由にならない」と明示して両立させる。冒頭を変えると保有の無い判断のプロンプトまで変わる） |
| 同 一次の冒頭「方針の範囲外・関心なし・不確実な場合は必ず Hold（見送り）を選びます。」 | **据え置き**（同上） |
| 同 `WatchlistIsNotPolicyRule`「…取引してよいかは、引き続き方針・リスク制約・保有状況で判断します」 | **据え置き**（保有状況を判断の根拠に含めており、固定文と矛盾しない） |
| 同 `ExitOnlyLine`「…手仕舞うかどうかは、方針の利確・撤退の基準と保有状況に従って判断してください。」 | **据え置き**（出口の判断を求めており矛盾しない。固定文は同じプロンプトの保有状況節に出る） |
| 同 `ExitFollowsPolicyRule`「出口の基準…が方針に無ければ、保有継続（Hold）を既定とします。」 | **据え置き**（Hold を既定とするのは判断の結果であり、判断の放棄ではない。裁定の「常に判断する」と両立） |
| 方針の本文の挿入（見出し `# 確定済み日報の方針`） | **据え置き**（「取引対象」等のラベルは付けていない） |
| 他サービスのコメントの「取引対象銘柄数」（`InMemoryOrderActivitySource.cs`）・判断の文言以外のコメント | **除外**（プロンプトの文言ではない） |

### プロンプトの文言を固定する試験

走査: `git grep -lE 'ScreeningHeldRule|ExitFollowsPolicyRule|HeldPositionSectionTitle|方針の範囲外' -- 'backend/**/Tests/**'`、および縮退の予算の境界
`git grep -nE 'NewsStatusReserveChars' -- 'backend/**/Tests/**'`。

| 箇所 | 扱い |
| --- | --- |
| `TradeDecisionPromptBuilderTests` `GoldenHeldSection`（本判断の保有状況節の全文） | **是正**（固定文の行を足した） |
| `TakeProfitReachedInPromptTests` 一次のショートの保有状況節の全文 | **是正**（固定文の行を足した） |
| `ScreeningContextAssemblerTests`（予算の境界 2 本）・`ScreeningContextDegradationTests`（1 本）・`WatchlistInDecisionPromptTests`（1 本） | **是正**（予約の分だけ予算を同幅ずらした） |
| `TakeProfitNotReachedInPromptTests`・`TakeProfitReachedInPromptTests` の行の並び（出口の規則の直後に利確の行、一次は保有中の規則の直後に利確の行） | **据え置き**（固定文を本判断は保護の状態の直後、一次は保有の行の直後に置き、並びを崩さない） |
| `TradeDecisionPromptBuilderTests` T-10-1811（一次の保有中の規則の行の全文）・`HeldAddOnBlockersTests`・`TradeDecisionServiceTests`（`Contain`） | **据え置き**（行単位の一致・部分一致で、固定文の追加の影響を受けない。実行して緑） |
| `TradeDecisionPromptBuilderTests` `LegacyScheduledPrompt`（#854 以前の出力） | **据え置き**（保有状況節を除いた部分の比較。冒頭の文言は変えていない） |
| `HeldOutsideWatchlistExitOnlyTests`（#1286 の試験） | **据え置き**（変更なしで緑） |

## 受け入れ基準

- [x] 保有ありなら、本判断と一次の両方の保有状況節に固定文が 1 回だけ出る。保有なし・不明・未約定だけなら出ない（T-10-2508）。
- [x] 方針の本文が列挙しない・監視銘柄にも無い保有銘柄を出口専用で判断すると、一次・本判断の両方に保有状況節と固定文が出て、LLM の Sell は保有全量の決済になる（T-10-2509）。
- [x] 固定文の行は一次の縮退の保護分の予約に収まる（T-10-2510）。
- [x] #1286 の試験（T-10-2490〜T-10-2502）は変更なしで緑。
- [x] `dotnet build`（警告 0）・TradeDecisionService の試験・`dotnet format --verify-no-changes`・repo の node 検査が通る。

## 範囲外

- 方針の生成（日報）のプロンプトの文言（裁定は「方針の文の書き方には頼らない」）。
- 冒頭の判断の枠の文言（上の母集合の表）。

## 検証

- `dotnet build backend/backend.slnx`・`dotnet test`（TradeDecisionService.Tests）・`dotnet format backend/backend.slnx --verify-no-changes`。
- node 検査（`check-trace-blocks`・`gen-knowledge-graph --check`・`check-commit-messages`・`check-test-traceability`・`check-adr-index-sync`・`check-cross-repo-refs`）。
- 変異 M1（一次から固定文を外す）→ T-10-2508・T-10-2509 が赤。M2（本判断から外す）→ T-10-2508・T-10-2509・全文の固定が赤。いずれも戻して緑。

## ［2026-10-10 追記 / #1292］独立監査（PR #1293・条件付き GO）の F1 への対応

- 指摘 F1: 予約 `HeldExitRuleReserveChars` が銘柄ごとの保護分（`PerSymbolLineChars`）に入っていることを固定する試験が無かった。
  変異 M3（保護分から予約を外す）で全件緑だった。上の母集合の表で「是正（予約の分だけ予算を同幅ずらした）」とした境界試験 4 本は、
  余裕が予約（150 文字）を超える（`ScreeningContextAssemblerTests` は 165 文字）か、予算ちょうどで削られないこと（Dropped=0）しか見ない
  （`WatchlistInDecisionPromptTests`）ため、予約を固定しない。**同幅ずらしたことは事実だが、予約の固定はこれらの試験では成り立たない。**
- 対応: T-10-2511（`HeldExitAlwaysJudgedInPromptTests`）を足した。保護分（予約を含む）と材料 1 件でちょうどの予算では削らず、1 文字少ない予算では 1 件削る。
- 変異 M3 → T-10-2511 だけが赤（1704 件中 1 件）。戻して緑。

