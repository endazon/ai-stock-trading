---
title: IADR-0302 統制値の計画適合検査は復活させない（人手突合のまま据え置き、担保していないことを明示し続ける）
type: impl-adr
status: Accepted
related_ids: [NFR, FR-10, ADR-0016, ADR-0029, IADR-0127, IADR-0166, IADR-0172, IADR-0296]
author: claude (Claude Code)
created: 2026-09-04
updated: 2026-09-04
plan_refs: []
related_specs:
  - ../specs/20260904_675_plan-conformance-check-decision.md
---

# IADR-0302: 統制値の計画適合検査は復活させない（人手突合のまま据え置き、担保していないことを明示し続ける）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制）/ ADR-0016 決定6（統制値の表）/ ADR-0029 決定2（planning 依存の全撤去）
- 起点 issue: [#675](https://github.com/endazon/ai-stock-trading/issues/675)（[#636](https://github.com/endazon/ai-stock-trading/issues/636) から切り出した「(a) の是非」の判断）
- 関連する実装仕様書: [20260904_675_plan-conformance-check-decision](../specs/20260904_675_plan-conformance-check-decision.md)
- 関連 IADR: [IADR-0127](IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱レジストリ）、
  [IADR-0166](IADR-0166_plan-source-digest.md) / [IADR-0172](IADR-0172_plan-risk-defaults-value-level-conformance.md)（値水準の適合検査）、
  [IADR-0296](IADR-0296_llm-pricing-stays-local-profile-and-plan-conformance-check-deferral.md)（#636 で (b) を採り、(a) を本 issue へ切り出した）

## コンテキストと課題

`backend/Tests/AiStockTrading.PlanConformance.Tests/`（`PlanRiskDefaults` / `ActualDefaults` /
`KnownPlanDeviations` / `PlanSourceDigests` ほか計 11 ファイル）は、統制値 `TradingDefaults` が
計画書の統制値の表からずれたときに CI を赤くする検査であった。**planning submodule を読んでいた**ため、
[#536](https://github.com/endazon/ai-stock-trading/pull/536)（資料再編・ADR-0029 決定2「planning 依存の全撤去」）で削除された。

#636 は「(a) planning 非依存の形で復活させる」か「(b) 復活させないと決め、担保していないという記述へ
是正する」かを求め、**(b) を採って記述を是正した**（IADR-0296）。**(a) の是非だけが本 issue に残っていた。**

## 決定

### 決定 1: (a) は採らない。検査は復活させない

理由は 3 つある。

1. **リポジトリ自身の規約に照らして条件を満たしていない。** CLAUDE.md は「**検査器・規約の追加は
   同型の事故が 2 回起きてから**」と定める。統制値が計画から静かにずれた事故は、**実測できる範囲で
   1 度も起きていない**（IADR-0127 が記録する 2026-07-23 の `MinimumExpectedProfitMultiple` の不一致は、
   検査が**存在していた時期**に監査が人手で見つけたものであり、検査の不在に起因する事故ではない）。
2. **同型が一度試され、削除されている。** [#378](https://github.com/endazon/ai-stock-trading/issues/378) が
   この種の検査対象化を進め、#536 で削除された。**復活は 3 度目の往復**になる。
3. 🔴 **人手転記のホップが残り、「担保があるという誤認」を伴う劣化を招く。** planning 非依存で作る以上、
   計画書の値は C# の表へ**人が転記する**しかない。転記を誤れば **CI は緑のまま計画とずれる**。
   これは「検査が無い」と明示されている現状より**悪い** —— 現状の失敗様式は「気づかれない」だけだが、
   誤転記の失敗様式は「**検査に守られていると信じている**」である。

### 決定 2: 代わりに、担保していないことを明示し続ける

`docs/DEFINITION_OF_DONE.md` の 2 項目は既に（IADR-0296 で）「機械的に突合する CI 検査は存在しない」
「削除漏れを検知する CI 検査は無い」と書いている。**本 IADR ではその文面から「別 issue で判断する」という
未決の含みを外し、決着済みであることと本 IADR への参照へ差し替える。**

**この記述自体が唯一の防御である。** 「検査があると思って読み飛ばす」ことを防ぐために置かれており、
**軽くしない**。

### 決定 3: `KnownPlanDeviations` を参照する凍結記録には、日付付きの追記だけを足す

`.ai-context/` は凍結記録であり**本文プロズを後から書き換えない**（CLAUDE.md）。IADR-0127 ほかが
前提とする `KnownPlanDeviations` は実在しないが、**本文は直さず**、末尾へ日付付きの追記で
「レジストリは #536 で削除され、復活させないことが本 IADR で確定した」と記す。

### 決定 4: 残骸のビルド生成物を掃除する

`backend/Tests/AiStockTrading.PlanConformance.Tests/{bin,obj}` が削除後も残っている
（gitignore 済みで追跡はされていないため、リポジトリの内容には影響しない）。**ローカル作業環境の
掃除であり、CI・成果物への影響は無い。**

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| (a) planning 非依存で復活 | ❌ 上記 3 点。特に**誤転記による偽の担保**が現状より悪い |
| (a') 隣接クローン／GitHub URL を CI から読んで突合 | ❌ **ADR-0029 決定2 の再導入**にあたる（計画書をビルド／CI の前提にすること）。覆すには新しい計画 ADR が要る |
| **(b)（採用）復活させない** | ✅ 追加実装なし。**検査が無いことを明示する記述**が防御 |

## 影響

- 統制値のずれの検知経路は**棚卸し・監査セッションでの人手突合のみ**である（頻度は不定期）。
  これは受容したリスクであり、**隠さずに DoD へ書き続ける**。
- 統制値を触る PR のレビューでは、`docs/DEFINITION_OF_DONE.md` の該当項目（計画書との人手照合）が
  実際に行われたかを見ること。

## 残余リスク

- **人手照合は行われたかどうかを機械が確かめられない。** DoD のチェックボックスは自己申告である。
- 計画書の統制値が改訂されたとき、実装の追随漏れは**次の監査まで残り得る**
  （IADR-0127 が記録する 2026-07-23 の事例と同型。**検査があった当時ですら人手の監査が見つけた**という
  事実は、この残余リスクが (a) を採っても大きくは減らないことを示している）。
