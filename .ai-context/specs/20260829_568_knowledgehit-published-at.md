---
title: KnowledgeHit に発行時刻を持たせ縮退段③（古い順）を実効にする
type: spec
status: approved
related_ids: [FR-02, FR-04, FR-08, ADR-0003, IADR-0069, IADR-0072, IADR-0247]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
related_specs:
  - 20260828_337_trading-cycle-and-screening.md
---

# 仕様書: KnowledgeHit に発行時刻を持たせ縮退段③（古い順）を実効にする（#568）

## 起点

- 起点 issue: **#568**
- 起点 ID: **FR-02** / **FR-04** / FR-08 / ADR-0003 / IADR-0069 / IADR-0072 / **IADR-0247**
- 実測時点: 本リポ `claude/ast-implementation-issues-rzkoxb-w568` = `3a2c08c`（develop 最新）。
  隣接クローン microservices-platform `../microservices-platform` = `9ae1136a`（読み取り専用）。
  project-planning 隣接クローン `../project-planning` = `666965a8`（読み取り専用）。
- 計画の一次情報: `06_technical/01_architecture-overview.md`「判断の二段化」（縮退段③＝ニュース・開示を
  古い順・関連度の低い順に削る）。
- 先行 IADR: [IADR-0247](../adr/IADR-0247_screening-context-degradation.md) 残余リスク
  「`RetrievedContext` に発行時刻が無く、段 3 の『古い順』は現状関連度順のみが実効」を解消する。

## 課題

`ScreeningContextPlanner.Plan` の段 3 ソート（`OrderBy(発行時刻の有無) → ThenBy(発行時刻) → ThenBy(関連度)`）
自体は PR #561 で実装・テスト済みだが、供給側が発行時刻を運ばないため常に null が渡り、
実質「関連度のみ」でしか削られていなかった。

## 🔴 最初に見極めたこと: 供給側は発行時刻を返せるか（実測）

**返せる。捏造ではなく実在する属性を使う。** #565（`POST /documents` が本文を受けない）と同じ型の
壁には**当たっていない**。実測根拠:

1. **書き込み側は既に発行時刻を送っている。**
   `backend/Services/InformationCollectionService/Infrastructure/ExternalServices/KnowledgeBaseWriterSink.cs:47`
   `["publishedAt"] = item.PublishedAt.ToString("O")` — `CollectedInformation.PublishedAt`
   （`DateTimeOffset`・ソース側の実際の発行時刻）を ABAC 属性 `publishedAt` として
   `KnowledgeDocument.Attributes` へ載せ、platform `POST /documents` へ送信している。
2. **platform 側は文書属性をチャンクペイロードへそのまま伝播し、検索応答へ復元して返す。**
   - 書き込み: `IngestionService.../QdrantIngestionVectorStore.cs` `BuildChunkPayload` が
     `attributes` 辞書をネスト構造体 `attributes -> {k: v}` として Qdrant ペイロードへ保存する
     （IADR-0014 選択肢C。RetrievalService 側 `QdrantVectorStore.cs` の書き込みも同型）。
   - 読み出し: `RetrievalService.../QdrantVectorStore.cs:285-300` `ExtractAttributes` が
     `attributes` 構造体を `Dictionary<string,string>`（`OrdinalIgnoreCase`）へ復元し、
     `SearchResultDto.Attributes` として `POST /search` の応答に含めて返す
     （`Knowledge.Contracts/Dtos/SearchResultDto.cs`）。
   - つまり AST が書いた `publishedAt` 属性は、**キー名を変えずに**検索応答の `Attributes["publishedAt"]`
     （ISO-8601 文字列・`"O"` 形式）として戻ってくる。
3. **一方で AST 側の HTTP アダプタ（`HttpKnowledgeBaseSearch`）は現状 `Attributes` を一切デシリアライズ
   していない**（`SearchResultBody` に `Attributes` フィールドが無い）。したがって受け入れ基準②
   「供給側が発行時刻を返すようにする」の実体は、**platform の改修ではなく AST 側 HTTP アダプタの
   写像追加**で満たせる。
4. **platform 契約には別の日時項目 `SearchResultDto.UpdatedAt`（`DateTimeOffset?`）も存在する**が、
   これは「索引（Qdrant ペイロード）の更新日時」（`Document.UpdatedAt`＝platform 側の取り込み・更新時刻）
   であり、**ニュース記事・開示の実際の発行時刻とは異なる概念**である（AST の取り込みは収集直後に
   行われるため近似はするが、遅延取り込み・再正規化があれば乖離する）。計画が求める「発行時刻」の
   一次情報は AST が書いた `publishedAt` 属性であり、`UpdatedAt` を代用しない。

**結論: 受け入れ基準 1〜4 はすべて満たせる。** 保留・部分達成の判断は不要。

## 母集合の引き直し（規則 9・10）

### 軸 1: `KnowledgeHit` の全参照

```
grep -rln "KnowledgeHit" backend --include=*.cs
```
8 件: `IRetrievalContextProvider.cs` / `ScreeningContextAssembler.cs` /
`KnowledgeBaseRetrievalContextProvider.cs` / `KnowledgeBaseRetrievalContextProviderTests.cs` /
`HttpKnowledgeBaseSearchTests.cs` / `HttpKnowledgeBaseSearch.cs` / `NoOpKnowledgeBaseSearch.cs` /
`KnowledgeModels.cs`。**全数を確認した**。`NoOpKnowledgeBaseSearch.cs` は空リストを返すのみで
変更不要（除外・理由: 型シグネチャに影響しない）。

### 軸 2: `PublishedAt` の全参照（既存 24 件）

`ScreeningContextPlanner.cs` / `ScreeningContextPlannerTests.cs`（プランナは変更不要・宣言済み）に加え、
`InformationCollectionService/Domain/CollectedInformation.cs` / `DegradationNotice.cs`
（送信元。変更不要）。**プランナのソートキーには手を入れない**（IADR-0247 で確定済みの 2 段ソートを
再利用する）。

### 軸 3: `RetrievedContext` の全構築箇所（`new RetrievedContext(`）

12 件（`grep -rn "new RetrievedContext(" backend --include=*.cs`）。すべて位置引数 5 個以下
（Title, Text, SourceUri, Score, Tags）。**新フィールドを末尾オプション引数（既定 null）で追加すれば
全件が無改修で通る**ことを確認した。実際に変更するのは `KnowledgeBaseRetrievalContextProvider.cs`
の 1 箇所のみ（供給元）。

### 軸 4: `KnowledgeHit` の位置引数構築（`new KnowledgeHit(`）

2 件: `HttpKnowledgeBaseSearch.cs`（供給側・要修正）、
`KnowledgeBaseRetrievalContextProviderTests.cs`（6 引数・末尾オプション追加なら無改修）。

### 軸 5: 契約イベント全数レジストリへの波及（拘束 6）

`KnowledgeHit` は `AiStockTrading.Shared.KnowledgeBase`（リポ内 DTO）であり
`AiStockTrading.Shared.Contracts`（イベント契約）ではない。実測: `grep -rn "KnowledgeHit"
backend --include=*.cs | grep -Ei "Audit|Notification|event-schema"` は **0 件**。
`event-schemas.baseline.json` に `KnowledgeHit` / `SearchResult` の記載なし（`find ... -exec grep`
0 件）。**全数レジストリ（`AuditEntryFactory` / `AuditEventHandlers` /
`AuditCycleCompletenessTests` / `event-schemas.baseline.json` / `EventMessageTypeNameTests` /
ADR 索引・`NotificationFormatter.From`）への追随は不要**（波及なしを実測で確認。捏造ではなく
grep 0 件が根拠）。

### 除外したもの

| 除外 | 理由 |
| --- | --- |
| `ScreeningContextPlanner.cs` のソート式自体 | 既に「発行時刻の有無 → 発行時刻 → 関連度」の 2 段（実質 3 段）ソートを実装済み（IADR-0247・PR #561）。本 issue は**供給の欠落**が原因であり、ソート式は変更不要（変更すると無関係な回帰リスクを持ち込む） |
| `docs/functional/` `docs/tests/` | 必須範囲は FR-10/12/15/19/20（`docs/README.md` 網羅裁定 #211）。本件は FR-02/FR-04 であり任意。作業仕様書＋xUnit を正の記録とする |
| `SearchResultDto.UpdatedAt` の利用 | 意味が異なる（索引更新時刻 ≠ 記事発行時刻）。上記「供給側は発行時刻を返せるか」参照 |
| platform（`../microservices-platform`）側の改修 | 読み取り専用。契約は既に発行時刻相当（`Attributes["publishedAt"]`）を運べており、platform 側の変更は不要 |

## 決定（設計）

1. `KnowledgeHit`（`KnowledgeModels.cs`）へ `DateTimeOffset? PublishedAt = null` を末尾オプションで追加。
2. `HttpKnowledgeBaseSearch`（供給側）:
   - `SearchResultBody` へ `Dictionary<string, string>? Attributes` を追加し `POST /search` 応答から受け取る。
   - `Attributes["publishedAt"]`（大文字小文字は `OrdinalIgnoreCase` 辞書として比較。platform 側の
     `ExtractAttributes` も `OrdinalIgnoreCase` のため揃える）を `DateTimeOffset.TryParse`
     （`DateTimeStyles.RoundtripKind`）で解釈できれば `KnowledgeHit.PublishedAt` へ、
     欠落・解釈不能なら **null（fail-safe・最古扱いへ倒す。捏造しない）**。
3. `RetrievedContext`（`IRetrievalContextProvider.cs`）へ `DateTimeOffset? PublishedAt = null` を追加。
4. `KnowledgeBaseRetrievalContextProvider.GetContextAsync` の写像へ `h.PublishedAt` を追加。
5. `ScreeningContextAssembler.Assemble` の `ScreeningMaterial` 生成を
   `PublishedAt: null`（ハードコード）から `PublishedAt: reference.PublishedAt` へ変更し、
   コメント（「発行時刻は KnowledgeHit が持たないため現状 null」）を実情に合わせて書き換える。
6. `IADR-0247` へ 2026-08-29 追記ブロックを追加し、残余リスクが本 issue で解消したことを記録する
   （旧 IADR は書き換えず追記のみ。`.claude/rules/traceability.repo.md` の凍結規約に従う）。
7. 新規実装判断（`Attributes` 辞書からの解釈を fail-safe にする設計・`UpdatedAt` を代用しない判断）は
   **IADR-0270** に記録する。

## テスト計画（3 点セット + 対の肯定形）

統制系（判断材料の統制＝ADR-0003 系）のため 3 点セットに加え、受け入れ基準④が明示する
「対の肯定形」を必ず添える。

1. **供給側（`HttpKnowledgeBaseSearchTests`）**
   - 肯定: `attributes.publishedAt` が ISO-8601 文字列なら `KnowledgeHit.PublishedAt` へ正しく写像される。
   - 否定（対）: `attributes` に `publishedAt` が無い／解釈不能な値なら `PublishedAt` は `null`
     （fail-safe。既定の最古扱いへ倒れることを保証）。
2. **写像（`KnowledgeBaseRetrievalContextProviderTests`）**
   - `KnowledgeHit.PublishedAt` が `RetrievedContext.PublishedAt` へそのまま伝播する。
3. **組み立て（新設 `ScreeningContextAssemblerTests`）**
   - 肯定形: 発行時刻を持つ参考情報を渡すと `ScreeningMaterial.PublishedAt` に反映される
    （`ScreeningContextAssembler` が null を上書きしないことを固定）。
   - 境界値: 予算超過時、発行時刻が新しく関連度が低い記事と、発行時刻が古く関連度が高い記事が
     競合する場面で**古い方が先に削られる**（段③が実効であることを直接示す。ここが本 issue の核心）。
   - 否定形（対）: 発行時刻を持たない参考情報が混在しても落ちず、最古扱いで先に削られる。
4. **境界値/プロパティベース/否定形は既存 `ScreeningContextPlannerTests` が既に持つ**
   （プランナのソート式自体は変更しないため、追加不要。母集合の軸 1 で確認済み）。

## 変異試験計画

- ソートキー（`PublishedAt`）を落とす／`OrderBy` を `ThenBy` の後ろへ入れ替える等の変異を当て、
  新設・既存テストで何件赤くなるかを実測し、元に戻して `git status --short` が空であることを確認する。
- `HttpKnowledgeBaseSearch` の `publishedAt` 解釈で `TryParse` を `Parse`（例外化）に変える変異、
  `null` 既定を撤去する変異も当てて fail-safe テストが赤くなることを確認する。

## 受け入れ基準との対応

| # | 受け入れ基準 | 対応 |
| --- | --- | --- |
| 1 | `KnowledgeHit` に発行時刻を持たせる | 決定 1 |
| 2 | 供給側が発行時刻を返す | 決定 2（実測: 捏造ではなく既存の `publishedAt` 属性を写像するだけで満たせる） |
| 3 | 段③の 2 段ソートを実効にする | 決定 3〜5（ソート式は変更せず、供給を埋めるだけで実効になる） |
| 4 | 発行時刻不明＝最古扱い（保守側）をテストで固定・対の肯定形も固定 | テスト計画 1・3 |
