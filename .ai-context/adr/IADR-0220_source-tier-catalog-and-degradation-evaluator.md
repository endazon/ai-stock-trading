---
title: IADR-0220 情報源の区分と欠測時の振る舞いは、ドメインのカタログ＋純関数の判定器で持つ
type: impl-adr
status: Accepted
related_ids: [FR-01, UC-01, ADR-0004, ADR-0020, IADR-0022, IADR-0064]
author: claude (Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
  - planning:projects/ai-stock-trading/06_technical/02_datasource-candidates.md
related_specs:
  - ../specs/20260828_336_information-collection-tiers-and-degradation.md
---

# IADR-0220: 情報源の区分と欠測時の振る舞いは、ドメインのカタログ＋純関数の判定器で持つ

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（起票 #336。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: FR-01（情報収集・Must）/ UC-01 / ADR-0020（区分・縮退）/ ADR-0004（案A+）
- 関連する実装仕様書: [`.ai-context/specs/20260828_336_information-collection-tiers-and-degradation.md`](../specs/20260828_336_information-collection-tiers-and-degradation.md)

## コンテキストと課題

ADR-0020 は情報源へ **必須 / 推奨 / 任意 / 検証用途** の区分を与え、必須の欠測時の振る舞いを **3 種**
（サイクル中止 / 限定縮退 / 記録・通知のみ）に限った。現行実装（IADR-0064 の多ソース合成）は
**区分の概念を持たず**、`CompositeInformationSource` は 1 ソースの失敗をログして捨てていた。

**捨てていたことが本質的な障害である。** 戻り値は取得アイテムの平坦な列であり、
**「どのソースが落ちたか」を呼び出し側が知る手段が無い**。区分ごとの欠測判定は成立し得なかった。

## 検討した選択肢

1. **ソース単位の成否を返すポート（`ISourceFetcher`）へ置き換え、区分表と判定器をドメインに置く**（採用）
2. `IInformationSource` を維持したまま、合成側に「直近の失敗」を状態として持たせる — 判定の入力が
   **可変状態**になり、巡回をまたいだ取り違えが起こり得る。判定をテストするのに合成の状態遷移が要る。**却下**
3. アイテムが 0 件のソースを欠測と見なす — **「新着が無い日」と「取れなかった日」を混同する。**
   ニュースが無い日に新規建てが止まる。**却下**
4. 区分を構成ファイル（appsettings）だけに置く — 既定値がデプロイ環境ごとにずれ、
   **区分が「実装の外」になる**。計画表との突合もできない。**却下**（構成で持つのは降格だけとする）

## 決定

1. **区分表は `InformationSourceCatalog`（Domain）**が持つ。初期値は計画
   `02_datasource-candidates`「区分の割当」表の写像であり、**テーブルテストで計画表と一致させる**。
2. **欠測時の振る舞いは 3 値の enum `MissingSourceBehavior`** とする（4 つ目を足すには ADR の改定が要る）。
   推奨・任意の欠測は「記録のみ」であり、3 種の規定は**必須ソースに対するもの**である。
3. **判定は純関数 `DegradationEvaluator.Evaluate(catalog, outcomes)`** で行う。外部依存を持たず、
   区分 × 欠測の全組み合わせをテストできる形にする。
4. **取得は `ISourceFetcher` が `SourceFetchResult`（アイテム＋ソース単位の成否）を返す。**
   実装 `SourceFetchRunner` は失敗をソース単位で隔離し、**`SourceOutcome` として判定へ渡す**。
   `CompositeInformationSource` は撤去する（同じ責務を 2 実装に割らない）。
5. **未構成の必須ソースは欠測に数えない。** 試行していないものは `UnconfiguredRequired` として警告に出す。
6. **検証用途区分のアイテムは収集段で破棄する**（ADR-0020 決定1「ライブの取引判断の入力にしてはならない」）。
   カタログに無い名前も不可とする（fail-closed）。

## 理由

- **欠測判定の入力は「事実」でなければならない。** ログは人が読むためのものであり、統制の入力にはならない。
- **未構成を欠測に数えると、安全既定（外部接続しない・IADR-0022）のままで毎サイクルが中止になる。**
  未構成のままではアイテムが 0 件となり `InformationCollected` が出ないため、**止めるべきものは構造的に止まっている。**
- **検証用途を運用で除外する形にしない。** KB へ入れてから「使わない」と決めても、RAG がいつか拾う。
  入口で落とすほうが確実である。

## 結果

- 良い影響: 区分・欠測の判定が外部依存ゼロの純関数になり、全組み合わせ（必須 7 ソース = 128 通り）を
  テストで回せる。計画表との一致がテーブルテストで固定される。
- 悪い影響・トレードオフ: `IInformationSource` を直接 DI していた箇所（Program・テスト）の書き換えが要った。
  合成の型（`CompositeInformationSource`）を見ていたテストは名前ベースの表明へ移した。
- フォローアップ:
  - **`moomoo`（サイクル中止）と FINRA（空売りの限定縮退）は判定器としては実装されているが、
    可用性の信号がまだ結線されていない。** moomoo は `BrokerAvailabilityObserved` を収集側へ引き込む
    結線（取引サイクルの結線＝#337 の射程）、FINRA はコネクタ自体が未実装である。
  - 報告用の日次収集の分離（FR-01 の「報告書用は日次」）は本 PR の対象外（#338 と SC-01 の供給に跨る）。

## 関連

- Supersedes: なし（IADR-0064 の「ソース単位で有効化する」構造はそのまま前提とし、**成否の返し方だけを変える**）
- Superseded by: なし
