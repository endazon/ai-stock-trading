---
title: 作業仕様書 — 再実装の退行防止テスト基盤（TradingDefaults 計画適合・写像規約・カバレッジ ratchet）
type: work
status: review
related_ids: [NFR, FR-10, FR-12, FR-15, FR-17, FR-19, FR-20, IADR-0127]
author: endazon (with Claude Code)
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0018_risk-defaults-sync-and-stage0-dd.md
related_specs:
  - ../adr/IADR-0127_plan-conformance-known-deviation-registry.md
  - ./20260802_344_reimplementation-preparation.md
  - ../../docs/tests/README.md
  - ../../docs/DEFINITION_OF_DONE.md
---

# 作業仕様書: 再実装の退行防止テスト基盤（#343）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制）・FR-12（ペーパートレード）・FR-15（バックテスト）・FR-19（取引ガード）・FR-20（段階ゲート）／ NFR（テスト・カバレッジ運用）
- ユースケース（UC）: UC-06（統制の設定・段階遷移）
- 画面（SC）: なし
- 関連 ADR: ADR-0008（段階ゲート）・ADR-0016（空売り統制）・ADR-0018（既定値の確定単一値・Stage 0 DD≤10%）
- 実装 ADR: [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)（計画適合テストと既知逸脱レジストリ）
- 起点 issue: [#343](https://github.com/endazon/ai-stock-trading/issues/343)（親: [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 計画書リンク: 05_trading-assumptions §5（計画リポ）

## 目的・背景

全面再実装（#344）では既存実装を破棄し得るため、**退行の検知手段をテストへ移す**。本作業は各ドメイン
issue（#329〜#348）のテストが載る共通基盤と横断ルールを先行整備する。資金を扱うシステムであるため、
**統制系（リスク統制・段階ゲート・取引ガード）の網羅を最優先**とする。

### 着手時点で判明している計画との乖離

計画大改定（project-planning#144）で確定した §5 の値と、現行 `TradingDefaults` / 契約 enum は
**すでに乖離している**。本作業の実測で確認したものは次のとおり。

| 項目 | 計画の確定値（§5 / ADR） | 現行実装 | 修正担当 |
| --- | --- | --- | --- |
| 統制値の基準 | 自己資金（equity）・USD 建て | 円建て・固定額 | #329 |
| 初期投入資金 | **$3,000** | `100_000m`（円） | #329 |
| 1 注文あたり発注金額上限 | **equity の 25%**（割合で保持） | `35_000m`（固定額） | #329 |
| 1 日あたり発注金額上限 | **equity の 150%/日**（新規建てのみ） | `100_000m`（固定額） | #329 |
| 連敗時縮小のしきい値 | **5 連敗** | `3` | #329 |
| 空売り専用統制 8 項目 | ADR-0016 決定 2〜9 | 未実装 | #329 |
| 空売りの拒否理由 7 種 | ADR-0016 決定 10（全てクラス A） | 未実装 | #329 |
| 取引可能な商品種別 | **3 値**（現物 / 信用買い / 空売り） | `ProductType` = `{Cash, Margin}` の 2 値 | #332 |
| 発注先（Broker Provider） | **3 値**（moomoo `REAL` / `SIMULATE` / 内蔵 `paper`） | `TradeMode` = `{Paper, Live}` の 2 値 | #334 |
| Stage 1 | **moomoo `SIMULATE`**（3 か月） | `TradeMode.Paper` | #333 / #334 |
| Stage 2 の発注可能額 | **総資金の 30%（$900）** | `35_000m`（円・1 ポジション相当） | #333 |
| **Stage 0 合格基準の DD 許容値** | **0.10**（ADR-0018 決定2。運用の DD 停止ラインと同値） | `Stage0GateCriteria.Default.MaxDrawdownTolerance` = **0.15** | #333 |
| 保有建玉数上限の呼称 | 「保有**建玉**数」（「保有銘柄数」の語は用いない） | XML doc が「保有銘柄数上限」 | #329 |
| **最小期待利益（§4）** | **往復費用＋税の 2 倍**（2026-07-23 利用者決定） | `TradingAssumptionsDefaults.MinimumExpectedProfitMultiple` = **1.5**、かつ `CostCalculator.MinimumViableProfit` の基準に税を含まない | #358 |

**Stage 0 の DD 許容値について**: ADR-0018 決定2 が名指しするのは Stage 0 合格判定の許容値
（`Stage0GateCriteria`）であり、運用の DD 停止ライン（`RiskLimitSettings.MaxDrawdownRatio` = 0.10）とは
**別のフィールド**である。現状は Stage 0 が運用停止ラインより 5 ポイント緩い戦略を合格させ得る
（検証を通った戦略が運用開始と同時に停止条件へ抵触し得る）。
**本表の最後の行（呼称）は値ではないため計画適合テストの対象外**であり、#329 の作業仕様書で扱う。

**§4 の行は着手後（対象範囲を §1 / §4 / §6 へ広げた時点）の実測で判明**したものであり、監査の独立検証で
**実装側の追随漏れ**と裁定されて #358 が起票された。他の行と同じく本作業では修正しない。

**これらを本作業で修正しない**（各担当 issue の範囲）。本作業の役割は、**乖離を機械的に可視化し、
担当 issue が直したときに確実に検知される状態を作ること**である。

## 対象範囲

- 対象:
  1. `TradingDefaults` の計画適合テスト（計画確定値テーブル ＋ 既知逸脱レジストリ）。
     対象とする計画確定値は **§5 ＋ ADR-0008 / 0016 / 0018 ＋ §1 / §4 / §6（全体前提条件・FR-17。
     実装は `TradingAssumptionsDefaults`）**（[IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md) 決定の対象範囲）
  2. 受け入れ基準 → テスト写像の規約と、その CI 検査（`scripts/check-test-traceability.js`）
  3. カバレッジ floor ＋ ratchet 運用（`scripts/check-coverage.js`）と CI 結線
  4. 統制系の網羅テスト方式（境界値テーブル・プロパティベース・否定形の 3 点セット）の標準化
  5. 必須テスト仕様書（FR-10 / 12 / 15 / 19 / 20）の再実装方針への再作成・更新
  6. `docs/DEFINITION_OF_DONE.md` の再実装方針への更新
- 対象外:
  - **統制値そのものの修正**（#329 / #332 / #333 / #334 の範囲。本作業は検知のみ）
  - フェイクブローカー / フェイク LLM の実装（#331 / #335 が対象の実体を持つまで書けない）
  - moomoo `SIMULATE` 結合テスト（#342 の PoC 完了が前提）
  - フロント Playwright E2E（#340 / platform#446 の新スタック追随後）
  - 性能・レイテンシの実測（#203 を受け入れゲートとして接続する枠組みのみ本作業で規定し、実測は取引サイクル実装〔#337〕後）
  - `FluentAssertions` → `AwesomeAssertions` 置換（**#345 の範囲**。本作業は現行の `FluentAssertions` に揃える）

## 設計

### 1. 計画適合テストと既知逸脱レジストリ

素朴に「計画確定値 == 実装値」を書くと、上表の乖離により**着手初日から CI が赤**になり、フェーズ 1 が
終わるまで全 PR がブロックされる。逆に現行実装値を書くと、それは計画ではなく**実装のスナップショット**を
固定するだけで、計画との乖離を永久に検知しない（既存 [#306](https://github.com/endazon/ai-stock-trading/issues/306) の再発）。

そこで **計画値テーブル ＋ 既知逸脱レジストリ**の 2 段構えとする（根拠は [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)）。

```
backend/Tests/AiStockTrading.PlanConformance.Tests/
├── PlanDefault.cs            計画確定値 1 行（キー・値・出典）
├── KnownDeviation.cs         既知逸脱 1 行（キー・現行値・担当 issue・理由）
├── PlanRiskDefaults.cs       §5 / §1 / §6 / ADR-0008 / ADR-0016 / ADR-0018 の確定値テーブル
├── KnownPlanDeviations.cs    現時点で受容する逸脱の登録簿
├── ActualDefaults.cs         実装側スナップショット（TradingDefaults ＋ 契約 enum → 正規化文字列）
└── PlanConformanceTests.cs   4 本の検査
```

検査は 4 本。**3 番目が本方式の要**である。

| # | テスト | 目的 |
| --- | --- | --- |
| 1 | 計画確定値と実装値が一致する（登録済み逸脱を除く） | 計画からの**新規**逸脱を止める |
| 2 | 計画値テーブルの全キーが実装側スナップショットに存在する | 抽出漏れ（キーの綴り誤り・削除）を止める |
| 3 | **登録済み逸脱は実際に逸脱している** | 担当 issue が値を直したのに登録簿を消し忘れた場合に**失敗させる**（登録簿の陳腐化を構造的に防ぐ） |
| 4 | 登録済み逸脱はすべて担当 issue 番号と理由を持つ | 「とりあえず登録して恒久化」を防ぐ |

検査 3 により、#329 が `LosingStreakThreshold` を 5 へ直した瞬間に本テストが赤になり、
**登録簿から当該行を削除するまでマージできない**。逸脱の解消が記録に反映されることが機械的に保証される。

値は**正規化文字列**で比較する（`"0.25 (ratio of equity)"` のように単位・基準を含めた表現）。数値だけで
比較すると「25% を `25m` と持つか `0.25m` と持つか」「円かドルか」といった**単位・基準の取り違え**を
素通ししてしまい、本システムで最も危険な種類の誤りを検知できないためである。

### 2. 受け入れ基準 → テスト写像の規約と CI 検査

規約（`docs/tests/README.md` に記載し、`.claude/rules/traceability.md` の「テスト」節と整合させる）:

- 受け入れ基準 1 項目 = `[Fact]` 1 本、または `[Theory]` の 1 ケース群に写像する。
- テストメソッドの直上コメント、またはクラスコメントに起点 ID（`FR-xx` / `UC-xx` / `ADR-xxxx` / `IADR-xxxx`）を残す。
- テスト名は日本語可（識別子に全角記号は使えない）。

CI 検査 `scripts/check-test-traceability.js`:

1. `backend/**/tests/**/*.cs` を走査し、起点 ID の出現を収集する。
2. **必須範囲 FR（10 / 12 / 15 / 19 / 20）は、それぞれ 1 本以上のテストから参照されていること**。欠けたら失敗。
3. 参照された `FR-\d+` / `UC-\d+` / `SC-\d+` が計画書に実在すること（planning submodule が読めない環境では
   `check-doc-links.js` と同じ扱いで当該検査のみ skip する）。
4. 必須範囲 FR にはテスト仕様書（`docs/tests/`）が存在すること。

### 3. カバレッジ floor と ratchet

`scripts/check-coverage.js`:

- CI が出力する `**/coverage.cobertura.xml` を集計し、**行カバレッジ全体値**を `coverage-floor.json` の
  `lineRateFloor` と比較する。下回れば失敗。
- 上回った場合は「ratchet 候補値」を出力する（`--suggest`）。floor の引き上げは**人手の PR**で行い、
  自動では上げない（自動 ratchet は不安定テストの揺れで floor が跳ね上がり、後続 PR を無関係に落とすため）。
- 初期 floor は**現行実測値から余裕を引いた値**とし、本 PR の実測で確定する。

### 4. 統制系の網羅テスト方式（3 点セット）

統制系（FR-10 / 19 / 20）のテストは次の 3 種を**必ず揃える**。詳細と雛形は `docs/tests/README.md`。

| 種別 | 内容 | 例 |
| --- | --- | --- |
| 境界値テーブル | 閾値の直下・一致・直上を `[Theory]` で網羅 | 維持率 39.9% / 40.0% / 40.1% |
| プロパティベース | 不変条件を任意入力で確認 | 「縮小後の維持率は必ず目標以上」「複数上限のうち常に厳しい方が効く」 |
| 否定形 | 統制を迂回できないことを確認 | 「kill switch 中に新規建てが通らない」「LLM 出力で上限を上書きできない」 |

否定形を明示的に標準へ含めるのは、統制のテストが「正常系で拒否されること」だけを見て、
**迂回経路（別の入口・別のフィールド）を塞げていない**という失敗が起きやすいためである。

## 受け入れ基準

- [ ] `TradingDefaults` と契約 enum の計画確定値との乖離が、テストで機械的に列挙される
- [ ] 現時点の乖離（上表）はすべて既知逸脱として担当 issue 付きで登録され、CI は green を保つ
- [ ] 担当 issue が乖離を解消したとき、登録簿を更新しない限り CI が赤になる
- [ ] 必須範囲 FR（10 / 12 / 15 / 19 / 20）にテストとテスト仕様書が存在することを CI が検査する
- [ ] カバレッジが floor を下回ると CI が失敗する
- [ ] 統制系の 3 点セット（境界値・プロパティベース・否定形）が `docs/tests/README.md` に標準として明記される
- [ ] `docs/DEFINITION_OF_DONE.md` が再実装方針（1 issue = 1 PR・写像必須・逸脱登録）を反映する
- [ ] `dotnet build` / `dotnet test`（`Category!=Integration`）が green

## テスト方針

本作業自体がテスト基盤であるため、**基盤が正しく壊れること**を確認する。

| 確認 | 方法 |
| --- | --- |
| 新規逸脱を検知する | 計画値テーブルに存在し登録簿に無いキーの値を実装側で変えると検査 1 が失敗する（手元で一時改変して確認） |
| 陳腐化した登録を検知する | 登録簿の現行値を実装の実際値と異なる値にすると検査 3 が失敗する |
| 必須 FR のテスト欠落を検知する | `check-test-traceability.js` を必須 FR を増やした設定で実行し失敗を確認 |
| カバレッジ floor が効く | `check-coverage.js` に floor を実測値より高く与えて失敗を確認 |

## 計画書との差異

- 差異: なし。本作業は計画書の値を**検査対象として取り込む**のみで、値の解釈を変更していない。
- ただし §5 は「1 注文あたり」「1 日あたり」を **equity に対する割合**で持つと明記しており、現行実装の
  固定額保持は計画違反である。本作業ではこれを既知逸脱として登録し、修正は #329 に委ねる。

## 未決事項

1. **カバレッジ floor の初期値** — 本 PR の実測値に基づき決定する（実測後に確定）。
2. **性能ゲート（NFR: 取引サイクル 10 分 / 変動→発注 5 分）の実測方法** — 取引サイクルの実体（#337）が
   無い段階では枠組みのみ規定し、実測の CI 結線は #337 で行う。
3. ~~**`FluentAssertions` → `AwesomeAssertions` 置換に伴う本基盤の追随**~~ → **対応済み（2026-08-03）**。
   #351（#345 の分割 1/4）が本基盤の `AiStockTrading.PlanConformance.Tests` も置換した
   （[作業仕様書](./20260803_351_awesomeassertions-migration.md)）。

## 変更履歴

| 日付 | 内容 |
| --- | --- |
| 2026-08-03 | 初版作成（#343 着手前） |
| 2026-08-03（追補） | 計画適合レジストリの対象範囲へ **§1（譲渡益税率）・§6（月次費用上限 4 値）** を追加（FR-17）。§4（最小期待利益倍率）は計画 **2 倍** と実装 **1.5** の不一致を実測で発見したため収録を保留し、裁定待ちとした（[IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md) フォローアップ） |
| 2026-08-03（追補2） | §4 の不一致は監査の独立検証で**実装側の追随漏れ**と裁定され、担当 issue [#358](https://github.com/endazon/ai-stock-trading/issues/358) が起票された。`Assumptions.MinimumExpectedProfitMultiple` を計画値テーブルへ収録し（正規化値は倍率と基準の組 `2x of (round-trip cost + tax)`）、現行の乖離を #358 担当で既知逸脱に登録した。これにより本表の「着手時点で判明している乖離」は 1 件増える（#358 は #329〜#334 と並ぶ是正担当） |
