---
title: 報告書を種別ごとの purpose で生成し、上位方針の本文を feed-forward する（issue #291 / #293）
type: spec
status: done
related_ids:
  - FR-06
  - FR-07
  - FR-11
  - FR-16
  - UC-03
  - UC-04
  - UC-05
  - ADR-0003
  - ADR-0011
  - IADR-0032
  - IADR-0071
  - IADR-0115
  - IADR-0120
author: claude
created: 2026-07-30
updated: 2026-07-30
related_specs:
  - "../adr/IADR-0120_report-kind-purpose-and-parent-policy-feedforward.md"
  - "../adr/IADR-0115_report-auto-generation-scheduler.md"
  - "../adr/IADR-0071_report-service-remaining.md"
  - "../adr/IADR-0028_daily-policy-sync-api.md"
  - "./20260729_280_report-auto-generation-scheduler.md"
---

# 仕様書: 報告書の種別別 purpose と上位方針の feed-forward（issue #291 / #293）

## 起点となる計画書（トレーサビリティ）

- 起点 issue:
  - [#291](https://github.com/endazon/ai-stock-trading/issues/291)
    report-service が報告書の種別ごとの purpose で LLM を呼ぶ。
  - [#293](https://github.com/endazon/ai-stock-trading/issues/293)
    上位方針（月報→週報→日報）の本文を feed-forward する。
- 計画根拠: [04_workflows/03_reporting-cycle](../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md)
  （報告サイクル・**fixed**）。要点は次のとおり。
  > 取引方針を**月報→週報→日報**の階層で管理する。（中略）確定した日報が翌営業日の取引方針となる。

  | 報告書 | 参照する上位方針 | 主な内容 |
  | --- | --- | --- |
  | 月報 | —（最上位） | 月間損益・資産推移・方針評価、翌月の目標・投資方針・リスク上限案 |
  | 週報 | 当月の月報 | 週間損益・**目標達成度**、翌週の目標・注目セクター/銘柄 |
  | 日報 | 当週の週報 | 当日の取引と根拠の要約・損益、翌営業日の目標・監視銘柄・売買条件 |

  同ドキュメント §業務フローは「AI がドラフト生成＝**週報の目標との差異評価**＋翌営業日の目標案」と
  明記しており、上位方針の**本文**が生成に必要である。
- 要求: FR-06（報告書）、FR-07（方針の確定と取引への反映）、FR-16（数値はコード集計）、
  FR-11（platform の LLM 送信可否統制）。UC-03〜05。
- 設計: [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)
  （AI 判断のガードレール・完全無人での方針変更は行わない）、
  [ADR-0011](../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md)
  （取引判断のモデル固定。§決定「報告書生成の LLM は別扱い」は計画側で改定依頼中＝下記）。
- 計画への環流: [project-planning#50](https://github.com/endazon/project-planning/issues/50)。
  報告書を「方針書」と位置づけたうえで種別ごとに割当モデルを指定する旨を、ADR-0011 の改定として起案依頼済み。
- 基盤側の対応: [microservices-platform#420](https://github.com/endazon/microservices-platform/issues/420) /
  [#421](https://github.com/endazon/microservices-platform/issues/421)、
  [PR #422](https://github.com/endazon/microservices-platform/pull/422)（`PurposeModels` へ
  `report-monthly` / `report-weekly` / `report-daily` を追加）。**別リポ・別 PR**。
- 本作業の実装判断は [[IADR-0120]]（当初 `IADR-0117` で起票したが、並行 PR #294 が同番号を先に確保し
  0118 / 0119 も #297 / #298 が使用中だったため 0120 へ改番した）。

## 背景と問題（原因の確定）

利用者は本システムを「生成 AI を活用した金融商品の完全自動取引システム」と定義し、**月報/週報/日報を
「次の取引に活かす方針書」**と位置づけた。そのうえで種別ごとの割当モデルを仕様として指定した。

| 種別 | purpose | 割当モデル（基盤側で設定） |
| --- | --- | --- |
| 月報 | `report-monthly` | `claude-fable-5` |
| 週報 | `report-weekly` | `claude-opus-5` |
| 日報 | `report-daily` | `claude-sonnet-5` |

### 問題 1（#291）: 種別が LLM ルーティングに一切届いていない

`ReportService.Worker/Program.cs` は `IReportNarrativeDrafter` を**単一の purpose** で登録する。

```csharp
cfg["LlmGateway:Purpose"] ?? "report-narrative",
```

`HttpReportNarrativeDrafter.DraftNarrativeAsync` はこの固定値をそのまま `POST /complete` の
`purpose` に載せる（`Model: null`＝明示指定なし）。`ReportNarrativeContext.Kind` は
`ReportNarrativePromptBuilder` の**プロンプト文面（「日報」「週報」「月報」のラベル）にしか届かず、
ルーティングには一切影響しない**。

基盤の `Llm:Routing:PurposeModels` に `report-narrative` のエントリが無いため、
`LlmRouter.ResolveModel` は `DefaultModel`（`claude-opus-5`）へ着地する。結果、
**月報・週報・日報のすべてが同一モデルで生成されている**。#283 の自動生成
（`ReportAutoGenerator` → `ReportDraftService.BuildDraftAsync` → `drafter.DraftNarrativeAsync`）も
同じ経路を通るため、自動生成された 3 種別のドラフト散文はすべて同一モデルで書かれている。

### 問題 2（#293）: 上位方針の本文が LLM に届いていない

階層の骨格は実装済みだが、**参照が PeriodKey に留まり本文が届いていない**。

- `ReportPolicyDraft.ParentKind` が階層（日報→週報 / 週報→月報 / 月報→前月の月報）を定義している。
- `ReportAutoGenerator.GenerateAsync` は上位を取得している。

```csharp
var parentKind = ReportPolicyDraft.ParentKind(due.Kind);
var parent = store.GetLatestConfirmed(parentKind);   // ← 取得している
```

  しかし使うのは `parent?.Report.PeriodKey` だけである。用途は 2 つ。

  1. `TradingReport.BasedOn`（参照リンク）
  2. `ReportPolicyDraft.CarryOver` の `parentPeriodKey`（null なら
     「上位方針（週報）は未確定のため参照していません。」と付記するだけ）

- **`parent.Report.PolicySummary`（＝上位方針の本文）はどこにも渡らず破棄される。**
- `ReportNarrativeContext` に上位方針のフィールドが無い。`ReportNarrativePromptBuilder.Build` が
  出力するのは自種別の `PolicySummary`（＝**同種別**の直近確定済みの継続案）のみで、上位方針は
  1 文字も入らない。

つまり LLM は「週報の目標との差異評価」を書けない。**参照連鎖はリンクとしては存在するが、生成には
効いていない。**

### 実装済みで差分が無い箇所（監査結果）

- **日報→取引の結線は実装済み**である。`ReportService.GetConfirmedDailyPolicy`
  （`store.GetLatestConfirmed(ReportKind.Daily)`）→ `GET /reports/daily-policy` →
  TradeDecision の `HttpDailyPolicyProvider` → `IDailyPolicyProvider`。未確定（404）・非 2xx・
  タイムアウト・例外はすべて `null`＝取引しない安全側（FR-07・[[IADR-0028]]）。**本作業の対象外**。
- 確定は OwnerOnly のまま（ADR-0003・[[IADR-0115]] 決定1）。本作業は生成時の文脈供給のみを扱う。

## 対象範囲

### 変更する

| 対象 | 変更内容 |
| --- | --- |
| `ReportNarrativePurpose.cs`（新規・Application） | `ReportKind` → purpose の純関数写像 |
| `HttpReportNarrativeDrafter.cs` | 要求ごとに `context.Kind` から purpose を決めて送出。構成値は**上書き**として扱う |
| `Program.cs` | `LlmGateway:Purpose` を既定値なしで渡す（未設定＝種別ごとの purpose） |
| `IReportNarrativeDrafter.cs` | `ReportNarrativeContext` に上位方針の参照 `ParentPolicyReference`（期間キー＋本文）を追加。片方だけ在る状態を表現不能にする |
| `ReportNarrativePromptBuilder.cs` | 上位方針の節を追加し「差異評価」を指示に含める。未確定なら明記 |
| `ReportDraftService.cs` | `DraftRequest` に上位方針本文を追加し `ReportNarrativeContext` へ渡す |
| `ReportAutoGenerator.cs` | 取得済みの `parent?.Report.PolicySummary` を渡す（捨てるのをやめる） |
| `ReportEndpoints.cs` | 手動ドラフト経路の要求に上位方針本文を任意フィールドとして追加 |
| 各テスト | 3 種別 × 上位あり/なしを固定 |

### 変更しない（意図的に対象外）

- **日報→取引の結線**。実装済み（[[IADR-0028]]）。
- **確定（Confirm）の経路**。OwnerOnly のまま。自動確定は導入しない（ADR-0003・[[IADR-0115]] 決定1）。
- **`ReportPolicyDraft.CarryOver` が生成する方針文**。自動生成では「新しい方針を機械に提案させない」
  という [[IADR-0115]] 決定4 の判断は維持する。上位方針は**散文（Narrative）の文脈**としてのみ与え、
  `PolicySummary`（確定すると取引に効くフィールド）には混ぜない。
- **数値の扱い**。数値はコード集計が唯一の権威（FR-16）。上位方針は散文の文脈にのみ用いる。
- **`ReportNarrativeDefaults.PlaceholderText` と fail-safe の全経路**。無変更。
- **基盤側の `PurposeModels`**。別リポ・別 PR（microservices-platform#422）。

## 受け入れ基準

- [x] `ReportKind` → purpose の写像が純関数として存在し、3 種別を固定するテストがある
- [x] 3 種別それぞれで `POST /complete` の `purpose` が `report-daily` / `report-weekly` /
      `report-monthly` になることをテストで固定した
- [x] `LlmGateway:Purpose` を明示設定した場合は全種別へ上書き適用される（既存デプロイの非破壊）
- [x] `ReportAutoGenerator` が上位方針の**本文**を `ReportNarrativeContext` へ渡す
- [x] 上位方針の「期間キーだけ / 本文だけ」という半端な状態が型として表現不能である
- [x] プロンプトに上位方針の節が入り、「上位方針との差異を評価する」指示が含まれる
- [x] 上位方針が未確定のときは、その旨がプロンプトに明記される（捏造しない）
- [x] 数値はコード集計が権威という制約が崩れていない（プロンプトの指示文は不変）
- [x] 日報→取引の結線（`GetConfirmedDailyPolicy`）が無変更で通る

## 設計判断

### 種別 → purpose の写像を Application の純関数に置く

`HttpReportNarrativeDrafter` は Worker（Foundation）にあり実 HTTP を伴う。写像を Application の
純関数へ切り出すことで、HTTP を立てずに 3 種別の期待値を固定できる。`ReportNarrativePromptBuilder`
（純関数・Application）と同じ配置方針であり、新しい構造を持ち込まない。

### `LlmGateway:Purpose` は「上書き」として残す

構成値を単純に削ると、`LlmGateway__Purpose` を設定済みのデプロイで挙動が変わる。未設定なら種別ごと、
明示設定なら全種別へ適用、とすることで既存デプロイを壊さずに移行できる。既定値
（`?? "report-narrative"`）だけを外す。

### 上位方針は `PolicySummary` ではなく散文の文脈として渡す

`PolicySummary` は「確定すると取引に効く」フィールドであり、[[IADR-0115]] 決定4 は自動生成で
新しい方針を機械に提案させないことを定めている。上位方針の本文をここへ混ぜると、機械が合成した
方針文が承認待ちに並び、「読んで承認するだけ」への退化を招く。上位方針は
`ReportNarrativeContext` 経由で**散文の文脈**としてのみ与える。

### 月報の上位は「前月の月報」

`ReportPolicyDraft.ParentKind(Monthly) == Monthly` であり、月報の上位は前月の月報である
（最上位ゆえ自種別を遡る）。`ReportAutoGenerator` は `parentKind == due.Kind` のとき
`previous` と `parent` を同一とするため、月報では同じ報告書が「継続案の素」と「上位方針」を兼ねる。
プロンプトでは重複させず、月報のときは上位を「前月の月報」と呼ぶ。

## 実装方針（TDD）

1. **Red**: 3 種別の purpose 送出（`HttpReportNarrativeDrafterTests`）、purpose 写像
   （`ReportNarrativePurposeTests`）、プロンプトの上位方針節（`ReportNarrativePromptBuilderTests`）、
   自動生成が上位方針本文を渡すこと（`ReportAutoGeneratorTests`）のテストを先に書く。
2. **Green**: 純関数の追加 → drafter の purpose 決定 → 文脈の受け渡し → プロンプトの節追加。
3. **Refactor/追随**: コード内コメント・IADR・索引を更新する。
4. **検証**: `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` /
   `dotnet format backend/backend.slnx --verify-no-changes`。

## テスト観点

| ID | 観点 | 期待 |
| --- | --- | --- |
| T-1 | 種別 → purpose の写像 | Daily→`report-daily` / Weekly→`report-weekly` / Monthly→`report-monthly` |
| T-2 | 送出 JSON の purpose | 3 種別それぞれで対応する purpose が載る |
| T-3 | purpose の上書き | 構成値が明示されたら全種別へ適用される |
| T-4 | プロンプトの上位方針節（あり） | 上位の期間キーと本文が入り、差異評価の指示が含まれる |
| T-5 | プロンプトの上位方針節（なし） | 未確定である旨が明記され、本文を捏造しない |
| T-6 | 自動生成の feed-forward | 上位の確定済み報告書の `PolicySummary` が drafter へ届く |
| T-7 | 上位未確定時の自動生成 | 上位方針なしとして生成が継続する（例外にしない） |
| T-8 | 数値の権威（回帰） | 「数値の再計算・改変をしない」指示がプロンプトに残っている |

## 完了条件（DoD）

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が通る
- `dotnet format backend/backend.slnx --verify-no-changes` が通る
- 上表の受け入れ基準がすべてチェック済み
- `docs/DEFINITION_OF_DONE.md` を満たす
