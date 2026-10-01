---
title: 判断の KB 検索で銘柄を持たない文書が上位 K 件から押し出されないようにする（保存時の目印＋旧文書の補充。#1138）
type: spec
status: accepted
related_ids: [FR-01, FR-02, FR-08, UC-01, ADR-0020, IADR-0474, IADR-0454, IADR-0072, IADR-0293, IADR-0315, IADR-0313, IADR-0436]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01・FR-02・FR-08)
  - planning:projects/ai-stock-trading/03_usecases/ (UC-01 手順 3「過去の判断（RAG）」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0020 (ニュース系ソースと欠測の明示)
---

# 判断の KB 検索で銘柄を持たない文書が上位 K 件から押し出されないようにする（#1138）

## 起点

- #1138（medium・一部推測）。`KnowledgeBaseRetrievalContextProvider` の 2 本目の検索（銘柄を持たない文書）は
  `AttributeFilters: null`・`topK`・新しい順で引き、**取得後に** `symbol` を持つ文書を落とす。落とした枠は補充されない。
- 情報収集は銘柄を持つ文書（現在値・企業ニュース）を 5 分ごとに書くので、新しい順の上位 K 件が全部銘柄つきになり、
  市場全体の文書（google-news・FRED・BoJ・収集状態）と確定報告書が 0 件になり得る。#1078（市場全体の情報を判断へ届ける）に反する。
- 候補の実際の混ざり方は測っていない（issue も同じ）。本件は「起きても届く」形にする。
- issue の案: (a) 2 本目の TopK を広げる（例: topK×4）、(b) 保存時に銘柄を持たない文書へ目印の属性を付け、基盤の `AttributeFilters` で絞る。
  否定形の試験「上位が全部銘柄つき」を必須とする。

## 現状の調査（origin/develop 58730639・基盤は microservices-platform の作業ツリーを読み取りのみ）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 情報収集の保存は `KnowledgeBaseWriterSink.ToDocument` の 1 か所。属性は `kind`・`source`・`publishedAt`、銘柄があるときだけ `symbol`。銘柄を持たない文書（google-news・FRED・BoJ・`collection-status`〔`DegradationNotice`・`Symbol: null`〕・sec-edgar の一部）は**「銘柄が無い」ことを表す属性を持たない** | `InformationCollectionService/Infrastructure/ExternalServices/KnowledgeBaseWriterSink.cs` |
| 2 | 確定報告書の保存は `ReportKnowledgeMapper.ToDocument` の 1 か所（確定の REST・gRPC の代理書き込み〔同じハンドラ〕・入れ直し `ReportKnowledgeReingestService`）。属性は `periodKey`・`kind`・`assumptionsVersion`・`confirmedAt`。`symbol` も `publishedAt` も持たない | `ReportService/Infrastructure/ExternalServices/ReportKnowledgeMapper.cs` |
| 3 | 共有の書き手 `HttpKnowledgeBaseWriter.BuildAttributes` は `confidentiality`・`owner`・`department`・`project` を補完するだけで、他の属性はそのまま送る | `Shared.KnowledgeBase/Adapters/HttpKnowledgeBaseWriter.cs` |
| 4 | 基盤の保存時に検証される属性は `confidentiality` だけ（`DocumentAttributes`。属性辞書との動的照合は無い）。**新しい属性キーを足しても保存は拒否されない**（タグと違い辞書検証が無い） | 基盤 `DocumentService/Domain/DocumentAttributes.cs`・計画 `07_abac-attribute-model.md` §必須指定と実データの乖離 |
| 5 | 取り込みは文書の属性を全部チャンクの payload `attributes` へ写す（`QdrantIngestionVectorStore.BuildChunkPayload`）。`symbol` の単値フィルタが効くのと同じ経路で、新しい属性でも絞れる | 基盤 `IngestionService/Infrastructure/ExternalServices/QdrantIngestionVectorStore.cs` |
| 6 | **`AttributeFilters` の意味**: `Dictionary<string,string>`＝キー → 単一の許可値の**完全一致**。キー間は **AND**、値は 1 つだけ（多値の OR は `Scope` 側の `AllowedValues` だけ）。`ScopeNarrowing.Apply` で ABAC の許可へ交差させる絞り込みで、権限は広げない。**「属性が無い」「値が等しくない」は書けない**——判定は集合帰属だけで否定を持たず（計画 MSP ADR-0036 D-04）、**フィルタのキーを持たない文書は除外される** | 基盤 `Knowledge.Contracts/Dtos/SearchDto.cs`・`ScopeNarrowing.cs`・`HybridSearchService.BuildFilters` |
| 7 | 新しい順（`sortBy: updated`）は取得後の並べ替え。候補は関連度で `4×TopK` を取り、索引の更新日時の降順に並べて TopK に切る。**フィルタは候補を取る前に効く**（ベクトル・キーワードの両系統へ同じ `ScopeFilter` を渡す） | 基盤 `HybridSearchService.cs` 73〜82 行・284〜295 行 |
| 8 | 基盤の `TopK` に上限の検証は無い（REST）。判断側の `Retrieval:TopK` も上限を持たない（正の整数・既定 5） | 基盤 `HybridSearchService`・`TradeDecisionService/Program.cs` `ParseTopK` |
| 9 | 判断側の注入件数は「1 本目 ≦ K ＋ 2 本目の残り ≦ K」＝最大 2K（既定 10）。IADR-0313 の予算が縮退を起こすのは TopK 約 127 以上（IADR-0454 決定3） | `KnowledgeBaseRetrievalContextProvider` |

## 設計（IADR-0474）

**(b) 保存時の目印 ＋ 目印を持たない旧文書の補充（(a) を条件付きで使う）。**

1. **保存時の目印**: 銘柄を持たない文書に属性 `coverage = market` を付ける。
   - 情報収集: `KnowledgeBaseWriterSink.ToDocument` で `symbol` を書かないとき（銘柄が空・空白）に付ける。銘柄を持つ文書には付けない（両方を持つ文書を作らない）。
   - 確定報告書: `ReportKnowledgeMapper.ToDocument` で常に付ける（報告書は銘柄を持たない）。**付けないと、目印つきの文書が K 件たまった後は補充が走らず、報告書だけが 2 本目から構造的に落ちる**（UC-01 手順 3 の後退）。
   - キーと値は共有物 `KnowledgeSearchAttributes.Coverage` / `KnowledgeSearchAttributes.MarketCoverage` の 1 か所に置く。
   - キー名は `scope` を採らない（基盤の `Scope`〔ABAC の許可〕・`doc_scope`〔個人資料と組織文書の区別〕と紛れる）。
   - 書き込みは後方互換: 属性を 1 つ足すだけ。基盤は検証しない（調査 4）。既存の属性・タグ・指紋（`SavedContentFingerprints.Of` は収集情報から作る）は変えない。
2. **2 本目の検索**: `AttributeFilters = { coverage: market }`・`topK`・新しい順。目印つきの文書だけが候補になるので、銘柄つきの文書に候補の枠（基盤の `4×TopK`）を取られない。
3. **補充（3 本目・条件付き）**: 2 本目から残った件数（銘柄なし・足切りを通ったもの）が `topK` 未満のときだけ、
   フィルタなし・`topK × 4`（`int` の上限で頭打ち）・新しい順で引き、銘柄を持たない文書だけを、重複を除いて後ろへ足す。
   - 重複の鍵は（`DocumentId`・本文）。ヒットはチャンク単位なので、同じ文書の別のチャンクは別に数える（現状どおり）。
   - 並びは「目印つき（基盤の新しい順）→ 補充（基盤の新しい順）」。目印つきは配備後に書いた文書、補充は主に配備前の文書なので、おおむね新しい順を保つ。
4. **上限**: 2 本目と補充を合わせた市場側は `topK` 件で切る。注入は従来どおり最大 2K 件（調査 9 の予算を超えない）。
5. **要求の回数**: 定常（目印つきが K 件以上ある）は 2 回（従来と同じ）。目印つきが足りない間（配備直後・文書が少ない環境）だけ 3 回。

### 採らなかった案

| 案 | 評価 |
| --- | --- |
| (a) だけ: 2 本目の TopK を `topK×4` に広げる | 毎回 1 回の要求で済むが、確率の改善にとどまる。銘柄つきが 5 分ごとに増えるので、`4K` も埋まれば 0 件に戻る。否定形の試験（上位が全部銘柄つき）を満たせない |
| (b) だけ: 目印で絞り、補充しない | 配備前の文書（目印なし）が二度と届かない。確定報告書の過去分・直前に収集したマクロが配備の瞬間に消える |
| (b) ＋ 常に補充（3 回） | 結果は採用案と同じになるが、定常で毎判断 1 回ずつ要求が増える |
| (b) ＋ 補充を目印つきの件数で判定（足切り前） | 目印つきが K 件あっても全部古ければ足切りで 0 件になり、補充も走らない。足切りの後の件数で判定する |
| 旧文書へ目印を遡及付与する移行 | 基盤に属性の部分更新（upsert）が無く、全文書の再登録になる。補充で読めるため採らない |
| 基盤に「属性が無い」条件を足す | 基盤は改修しない（CLAUDE.md の前提）。計画 MSP ADR-0036 D-04 が否定の条件を持たないと定めている |

## 母集合（規則 9: 誤りの側の文字列で引いた）

走査: `git grep -e "AttributeFilters: null" -e "銘柄フィルタなし" -e "後段で落と" -e "属性が無い" -e "銘柄を持たない文書" -e "銘柄フィルタを掛けない" -- ':!.ai-context/specs'`
（`.ai-context/specs/` は凍結の記録なので除外）。

| 当たり | 扱い |
| --- | --- |
| `KnowledgeBaseRetrievalContextProvider.cs` の冒頭の注記・② のコメント・`AttributeFilters: null` | 直す（本件の対象） |
| `KnowledgeBaseRetrievalContextProviderTests.cs` の 3 試験と偽の検索 | 直す（2 本目はフィルタつき、補充はフィルタなし） |
| `KnowledgeModels.cs` の `KnowledgeSearchAttributes` の注記（「銘柄を持たない文書には無い」）・`KnowledgeHit.Symbol` の注記（「銘柄フィルタを掛けない検索の結果から」） | 直す（目印と補充を書く） |
| `HttpKnowledgeBaseSearchTests.cs` の `symbol` 属性の復元の注記 | 誤りにならない（復元の規則は変えない） |
| IADR-0454 決定3・残余リスク・README の行 | **凍結の記録**。本文は書き換えず、IADR-0454 に日付つき追記を 1 行置いて IADR-0474 を指す |
| IADR-0072 の追記（2 本を新しい順で引く） | 誤りにならない（2 本は維持。3 本目は条件付き） |
| IADR-0139 の「属性が無い」 | 無関係（バックテストの事実） |

書き手側の母集合（銘柄を持たない文書を KB へ書く経路）: `git grep -e IKnowledgeBaseWriter -e "catalog.CreateAsync"`（試験を除く）。
情報収集の `KnowledgeBaseWriterSink`（収集状態を含む全収集情報）と報告書の `ReportKnowledgeMapper.ToDocument`（確定の REST・gRPC の代理書き込み・入れ直し）の 2 か所だけ。両方に目印を付ける。

`docs/` の走査（`RAG|KB 検索|銘柄を持たない|Retrieval:TopK`）: 判断の KB 検索の組み立てを書いた文書は無い（構成図の「RAG 参照」と、セキュリティの注入対策だけ）。`docs/` の本文は変えず、テスト仕様書に節を足す。

### 規則 10（この変更で新たに誤りになる自分の記述）

- IADR-0454 決定3 の表の案 C「保存時に `symbol` の代わりの値を書き…既存文書には効かない。今回の範囲を超える」→ 本件で採る。既存文書は補充で覆う。追記で指す。
- IADR-0454 決定3「件数は最大 2×TopK」→ 変わらない（市場側を K で切る）。
- IADR-0454 残余「2 本目の候補が銘柄を持つ文書で埋まると届かない」→ 本件で塞ぐ。追記で指す。
- `Program.cs` の注記「TopK は Retrieval:TopK（既定 5）」→ 誤りにならない。補充の倍率は配線を変えない。

## 窓の表（規則 11）

配備の前後で「目印を持つ文書」と「持たない文書」が入れ替わる窓がある。

- **P1（増える側）**: 配備後に書いた市場全体の文書（目印つき）が、新しい銘柄つきの文書に囲まれている。期待: 届く。
- **P2（減る側）**: 配備前に書いた市場全体の文書・確定報告書（目印なし）だけがある。期待: 届く（目印つきが K 件に満たない間）。
- **P3（定常）**: 目印つきが K 件以上ある。期待: 要求は 2 回で、市場側は K 件。

| 形 | P1 | P2 | P3 |
| --- | --- | --- | --- |
| 後の端だけ（目印のフィルタだけ） | ✓ | ✗ 旧文書が二度と届かない | ✓ |
| 前の端だけ（フィルタなしで広げる＝案 (a)） | ✗ 銘柄つきが `4K` を埋めると 0 件 | △ 広げた範囲に入れば届く | ✓（要求 2 回）だが P1 と同じ欠陥 |
| **両端（目印で引き、足りない間だけフィルタなしで補充）＝採用** | ✓ | ✓ | ✓ |

## 試験（T-10-1980〜T-10-1989）

| ID | 内容 |
| --- | --- |
| T-10-1980 | 2 本目の検索は `coverage = market` の単値フィルタ・銘柄をクエリに入れない・`topK`・新しい順 |
| T-10-1981 | 🔴 否定形: フィルタなしの上位が全部銘柄つき → 目印つきの市場全体の文書が届く（是正前は 0 件） |
| T-10-1982 | 補充: 目印つきが `topK` 未満 → フィルタなし・`topK×4`・新しい順で引き、銘柄を持たない文書だけを足す（目印のない確定報告書が届く） |
| T-10-1983 | 補充しない: 目印つき（足切り後）が `topK` 件ある → 要求は 2 回 |
| T-10-1984 | 補充の判定は足切りの後: 目印つきが K 件でも全部古ければ補充を引く |
| T-10-1985 | 重複: 目印つきと補充に同じチャンクがあれば 1 件。同じ文書の別のチャンクは別に数える |
| T-10-1986 | 並び: 目印つき（基盤の順）→ 補充（基盤の順）。銘柄の文書が先頭 |
| T-10-1987 | 上限: 市場側は `topK` 件で切る（目印つき＋補充が多くても）。全体は 2K 以下 |
| T-10-1988 | 書き手（情報収集）: 銘柄を持たない収集情報（空・空白を含む・収集状態）は `coverage = market`、銘柄を持つものは付かない |
| T-10-1989 | 書き手（報告書）: 確定報告書の写しは `coverage = market` を持ち、`symbol` を持たない |

## 自己変異（実測）

作業ツリーにだけ当て（`cp` で退避・復元し `git diff --stat` で戻ったことを確かめた）、対象の試験を走らせた。

| 変異 | 落ちた試験（実測） |
| --- | --- |
| 🔴 2 本目の絞り込みを外す（是正前の形） | T-10-1980・T-10-1981・T-10-1982・T-10-1983・T-10-1984・T-10-1985・T-10-1986・T-10-1987（ほかに既存の 4 件） |
| 🔴 補充を引かない（目印だけ） | T-10-1982・T-10-1984・T-10-1985・T-10-1986・T-10-1987（ほかに既存の 2 件） |
| 補充を常に引く | T-10-1983 |
| 補充の判定を足切りの前の件数で行う | T-10-1984 |
| 重複を除かない | T-10-1985 |
| 市場側を K 件で切らない | T-10-1987 |
| 補充の倍率を 1 にする | T-10-1982（2 件） |
| 補充から銘柄つきを落とさない | T-10-1981・T-10-1982（ほかに既存の 1 件） |
| 補充の件数を頭打ちにしない（あふれる） | T-10-1982 |
| 情報収集で銘柄つきにも目印を付ける | T-10-1988 |
| 確定報告書に目印を付けない | T-10-1989 |

11 個の変異はすべて赤になった（生き残りなし）。「既存の」は #1083 の試験（片方の検索が空・確定報告書の足切り・発行時刻の足切り・長文方針の切り詰め・補充から銘柄つきを落とす・銘柄の検索の要求）。
走らせた範囲: 取引判断は `KnowledgeBaseRetrievalContextProviderTests`、情報収集は `KnowledgeBaseWriterSink*`、報告書は `ReportKnowledgeMapperTests`。

## 検証

- `dotnet build backend/backend.slnx -warnaserror`
- `dotnet test`: TradeDecisionService.Tests・InformationCollectionService.Tests・ReportService.Tests・AiStockTrading.Shared.KnowledgeBase.Tests
- `dotnet format --verify-no-changes`
- node の検査器（trace ブロック・試験の追跡・知識グラフ・クロスリポ参照・計画 ID の修飾・文書リンク・ADR 索引・必読の予算・コミットメッセージ）と `node scripts/scripts.test.js`

## 残余リスク

- 候補の実際の混ざり方は測っていない。補充の倍率 4 は基盤の候補の倍率と同じ値で、配備前の文書の届き方は確率のまま（配備後に書かれた文書は確定で届く）。
- 目印つきが K 件たまると補充は止まり、配備前の文書（確定報告書の過去分を含む）は以後届かない。報告書は確定のたびに新しい写しが目印つきで入り、入れ直し（`ReportKnowledgeReingestService`）も欠けた写しを目印つきで作る。既にある写しへ遡及はしない。
- 基盤の `updated` は索引の更新日時で並べる。目印つきの 2 本目は市場全体の文書と報告書が同じ枠を取り合うので、報告書の比率は収集の頻度に左右される（報告書の枠を分ける拡張は後続）。
- 目印は書き手 2 か所が付ける。新しい書き手が銘柄を持たない文書を書くときは同じ属性を付ける必要がある（付け忘れても補充の側で届き得るが、確定ではない）。
- 目印つきが K 件に満たない環境（文書の少ない検証環境）では毎判断 3 回の要求になる。
