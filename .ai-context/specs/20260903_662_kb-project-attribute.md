---
title: KB 保存に project=ai-stock-trading 属性を必須付与する（ADR-0032 決定 2 (1)）
type: work
status: review
related_ids: [FR-08, ADR-0012]
author: claude (Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0032_mcp-non-exposure-is-enforced-by-attributes-not-the-allowlist.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0012_mcp-exposure-policy.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
---

# 作業仕様書: KB 保存に `project=ai-stock-trading` 属性を必須付与する

> Issue [#662](https://github.com/endazon/ai-stock-trading/issues/662)。計画 ADR-0032（起点環流
> [planning#513](https://github.com/endazon/project-planning/issues/513)。2026-09-03 確定）決定 2 (1) の
> 実装。設計判断は [IADR-0293](../adr/IADR-0293_kb-project-attribute-required.md)。

## 計画の確認（着手前）

ADR-0032 は 4 決定を置く。本 issue の射程は **決定 2 の 3 点のうち (1) のみ**である。

- 決定 1: ADR-0012 §決定 は改めない（統制は有効）。
- **決定 2 — 実現手段（`private-note` 前例と同じ 3 点）**:
  1. **AST が基盤へ保存する文書は `project` 属性に `ai-stock-trading` を必須で持つ**（本 issue の対象）。
  2. MCP のサービスアカウントへこの値を含む属性割当を構成上禁止する（基盤側。別途 MSP へ起票）。
  3. CI のスキーマ検証で弾く（基盤側・`private-note` と同じ場所・形。別途 MSP へ起票）。
- 決定 3: 現在の実現手段は無い。基盤の ABAC は `project` を判定軸に持たない。決定 2 の配備が
  「MCP クライアント登録簿が空／下流ツール口 404／AST 文書 0 件」いずれかの解消の先行条件。
- 決定 4: AST 側ドリフト検査（IADR-0273）は維持。射程は「AST 自身のツールが公開されていないこと」のみ。

**`doc_scope` に第 3 の値は足さない**（決定 2 が明記。個人資料/組織文書の軸とユニットの出所の軸は別）。

### platform 属性モデルの確認（`07_abac-attribute-model.md` §文書の基本属性）

`project`（プロジェクト）は**任意**・値は**プロジェクトコード**（単値の文字列。配列表現の記述は無い）。
`owner` / `department` と同じ「属性キー → 文字列 1 値」の形であり、`shared_with` のような集合型ではない。
したがって送る形は既存の `owner` / `department` 補完と同じ（`Dictionary<string,string>` のキー1つ）でよく、
複数値表現・配列化の考慮は不要。

### MSP `DocumentService` の受け口確認

`src/knowledge/backend/Services/DocumentService/Domain/DocumentAttributes.cs` は `confidentiality` と
`doc_scope` のみを保存時に検証し（`ValidateConfidentiality` / `ValidateDocScope`）、**`project` キーに対する
検証・拒否は無い**。すなわち `project` を送っても現状の MSP 側スキーマ検証では拒否されない
（`CreateDocumentRequest.Attributes` は自由な `Dictionary<string,string>` を受け付ける）。

ADR-0032 決定 3 が明記するとおり、**`project` を付けるだけでは ABAC 判定軸に無いため MCP 到達を止める
効果は無い**。本 issue は決定 2 (1) の属性付与のみを実装し、統制としての実効化は決定 2 (2)(3)（基盤側）に
委ねる。過大な効果を主張しない。

## 対象範囲

- 対象:
  - `AiStockTrading.Shared.KnowledgeBase.Adapters.HttpKnowledgeBaseWriter.BuildAttributes`
  - `AiStockTrading.Shared.KnowledgeBase.KnowledgeModels`（`project` の定数・許容値を持つ小さな静的クラスを追加）
  - `HttpKnowledgeBaseWriterTests`（肯定形・否定形）
- 対象外:
  - MSP 側のサービスアカウント属性割当禁止・CI スキーマ検証（決定 2 (2)(3)。MSP issue へ委譲）
  - `doc_scope` の変更（決定 2 が明示的に禁止）
  - ABAC 判定軸への `project` 追加（決定 3 が「現在の実現手段は無い」と明記。基盤側の設計判断）

## 設計

### 属性の送り方

- キー: `project`（platform `07_abac-attribute-model.md` の属性名をそのまま使う。言い換えない）
- 値: `ai-stock-trading`（本リポジトリの短縮プロジェクト名。`.claude/rules/traceability.repo.md` の
  クロスリポジトリ表記とは別の軸 —— こちらは ABAC のプロジェクトコードであり、issue/PR 番号の修飾語
  ではない）
- 型: 単値の文字列（`Dictionary<string,string>` の 1 エントリ）。配列化しない（上記確認のとおり `project`
  は単値属性）。

### 拒否規則（fail-loud か fail-safe か）

**採用: fail-loud（例外）。** 呼び出し側 `Attributes` に `project` の明示指定があり、値が
`ai-stock-trading` と異なる場合は `InvalidOperationException` を投げる。

理由:

- `confidentiality` / `owner` / `department` は「呼び出し側が未指定なら安全な既定へ倒す」ための補完であり、
  **呼び出し側の値は常に信頼して優先する**（誤りではなく選択の範囲内）。
- 一方 `project` は**本ユニットが基盤へ保存する文書すべてに対して不変な値**である。異なる値が来るのは
  「他ユニットの文書とテンプレートを取り違えた」「呼び出し側が誤ってハードコードした」という**プログラミング
  誤りの兆候**であり、ADR-0032 が守ろうとしている「本ユニットの文書に対する MCP 非到達」という統制の前提
  （文書が正しく `project=ai-stock-trading` を持つこと）を静かに壊す。**黙って上書きすると誤りが発見され
  ないまま統制の前提が崩れる**ため、`private-note` の前例（サービスアカウント側は「構成上禁止＋検証で
  拒否」という fail-loud 設計）と向きを揃え、書き込み側でも fail-loud にする。
- 例外は `SaveAsync` の `try` ブロック**外**（`BuildAttributes` の呼び出しは `CreateDocumentBody` 構築時、
  ネットワーク呼び出し前）で発生する。既存の fail-safe（HTTP 非 2xx・タイムアウト・例外は `NotSaved` に
  倒す）は「基盤側・ネットワークの障害」に対する縮退であり、**呼び出し側のプログラミング誤り**とは性質が
  異なる。両者を同じ `NotSaved` へ潰すと、誤りに気づく機会を失う。

未指定・同値（`ai-stock-trading`）はいずれも許可し、`project` を必須で補完する（`owner`/`department` と
同じ「明示指定は保持、無ければ埋める」の形。ただし許容値が単一である点が違う）。

### 実装

`KnowledgeModels.cs` に `KnowledgeAttributeDefaults` と対になる小さな静的クラスを追加する案と、
`KnowledgeAttributeDefaults` へ足す案を比較し、**既存クラスへの追加**を採る —— `owner`/`department` の
予約値と `project` の必須値は「保存時に必ず埋める文書属性」という同じ性質を持ち、クラスを分けても
呼び出し側の理解を助けない（1 か所を見ればよい状態を保つ）。

```csharp
public static class KnowledgeAttributeDefaults
{
    public const string ReservedOwner = "system";
    public const string UnassignedDepartment = "unassigned";

    // FR-08, ADR-0032 決定2(1): 本ユニットが基盤へ保存する文書は project 属性にこの値を必須で持つ。
    public const string ProjectKey = "project";
    public const string RequiredProject = "ai-stock-trading";
}
```

`HttpKnowledgeBaseWriter.BuildAttributes` へ追記:

```csharp
// project (FR-08, ADR-0032 決定2(1)): 本ユニットの文書は project=ai-stock-trading を必須で持つ。
// 明示指定が異なる値なら fail-loud（他ユニットの文書との取り違えを検出する。owner/department とは
// 異なり許容値は単一であり、黙った上書きは統制の前提の崩れを隠す）。
if (attributes.TryGetValue(KnowledgeAttributeDefaults.ProjectKey, out var project)
    && !string.IsNullOrWhiteSpace(project)
    && !string.Equals(project, KnowledgeAttributeDefaults.RequiredProject, StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        $"KB 保存: project 属性は '{KnowledgeAttributeDefaults.RequiredProject}' 以外を指定できません" +
        $"（指定値: '{project}'。ADR-0032 決定2(1)）。");
}
attributes[KnowledgeAttributeDefaults.ProjectKey] = KnowledgeAttributeDefaults.RequiredProject;
```

呼び出し順序は既存の `confidentiality` → `owner`/`department` 補完のあとに置く（属性の見た目の並びは
JSON では意味を持たないため、順序自体に機能上の意味は無い）。

## 受け入れ基準

- [x] `BuildAttributes` が送信する属性へ `project=ai-stock-trading` を常に含める
- [x] 呼び出し側 `Attributes` に `project` の明示指定が無ければ補完する
- [x] 呼び出し側 `Attributes` に `project` の明示指定があり、値が `ai-stock-trading` と異なれば
  `InvalidOperationException` で拒否する
- [x] 同値の明示指定（`ai-stock-trading`）はエラーにならない
- [x] 既存の `owner` / `department` 補完・`confidentiality` 優先ロジックは不変（既存テスト回帰）
- [x] `HttpKnowledgeBaseWriterTests` に肯定形（付与される・同値許可）・否定形（別値は例外）のテストを追加

## 計画書との差異

- 差異: なし。ADR-0032 決定 2 (1) をそのまま実装する。`doc_scope` への値追加は行っていない（決定 2 が
  明示的に禁止）。

## 未決事項・フォローアップ

- **決定 2 (2)(3)（基盤側: サービスアカウントへの属性割当禁止・CI スキーマ検証）は本 PR の範囲外。**
  MSP リポジトリへ受け皿 issue を別途起票する（前例: MSP `ToolPublicationConfigValidator
  .ValidateServiceAccountAttributes` が `doc_scope=private-note` の割当を構成上禁止している。
  `src/platform/backend/Services/McpServer/Domain/ToolPublicationConfigValidator.cs`）。
- **決定 3 が明記するとおり、本 PR の実装だけでは MCP 到達は止まらない。** 基盤の ABAC 評価式は現在
  `project` を判定軸に持たない。過大な統制効果を主張しない。
