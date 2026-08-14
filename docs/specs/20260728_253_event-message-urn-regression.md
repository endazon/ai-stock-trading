---
title: イベントの MessageUrn 回帰テストを追加し、名前空間移動による wire 契約破壊を CI で検知する（Issue #253）
type: spec
status: review
related_ids:
  - NFR
  - FR-11
  - ADR-0001
  - IADR-0037
  - IADR-0079
author: claude
created: 2026-07-28
updated: 2026-07-28
related_specs:
  - "../adr/IADR-0037_async-contract-format-reevaluation.md"
  - "../adr/IADR-0079_event-backward-compat-contract-test.md"
  - "../api/events-and-ports.md"
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 仕様書: イベント MessageUrn 回帰テストの追加（Issue #253）

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR**（契約の後方互換・保守性）。FR-11（監査＝全イベントの時系列記録）の前提となるイベント契約の安定性。
- 決定: [[IADR-0037]]（AsyncAPI 不採用・**代替として `MessageUrn` 回帰ガードを後続 issue/PR で実装せよ**と明示）、
  [[IADR-0079]]（`EventBackwardCompatibilityTests`＝プロパティ単位の後方互換 snapshot）。
- 制約: [[ADR-0001]]（platform 再利用）。platform の同型テスト `EventMessageUrnTests`
  （microservices-platform リポジトリの
  `src/knowledge/backend/Shared/Knowledge.Contracts.Tests/EventMessageUrnTests.cs`）に揃える。
  **先頭を `../` にした相対パスで書かない** —— 隣接クローンは CI に存在せず、
  **live link として解決できないまま検査を素通りしていた**（#493 で検出）。
  他リポジトリのファイルは**リポジトリ名を明示して参照する**。
- Issue: [#253](https://github.com/endazon/ai-stock-trading/issues/253)（`tech-debt` / priority: could）。

## 目的・背景

MassTransit はメッセージの wire 上の識別を**正準 URN**（`urn:message:<Namespace>:<TypeName>`）で行う。
URN は**名前空間と型名の双方**から導出されるため、名前空間の移動は型名が不変でも wire 契約を破壊し、
キューに滞留中／`_error` キュー内のメッセージが再消費不能になる。

現行の契約ガード [[IADR-0079]] `EventBackwardCompatibilityTests` は、snapshot のキーを
**`Type.Name`（単純型名）**で構成している。したがって:

| 変更 | `EventBackwardCompatibilityTests` |
| --- | --- |
| プロパティの削除・改名・型変更 | 検出する ✅ |
| イベント型の削除・改名 | 検出する ✅ |
| **名前空間の移動（型名は不変・URN は破壊）** | **検出しない** ❌ |

本リポジトリは過去に #102（`src/` → `backend/` 再編）のような大規模レイアウト変更を実施しており、
名前空間の移動が起き得ない構成ではない。[[IADR-0037]] のフォローアップが指示した `MessageUrn` 回帰テストは
**2026-07-28 時点で未実装**（`backend/` に `MessageUrn` 参照ゼロ）で、この穴が空いたままだった。

本作業はその穴を塞ぐ。

## 対象範囲

- **対象**: `AiStockTrading.Shared.Contracts.Tests` へ `EventMessageUrnTests` を追加。
  併せて [[IADR-0079]]「既知の限界」へ、本テストとの検出範囲の分担を追記する（Issue 受け入れ基準③）。
- **対象外**:
  - イベント契約（`Shared.Contracts.Events`）の変更。**プロダクションコードは一切変更しない**（テストのみ）。
  - `[MessageUrn]` 属性による URN の明示固定。現状の**正準 URN（名前空間から導出）で一貫**しており、
    属性で上書きすると「名前空間を自由に動かせるが URN は据え置く」という別の設計判断になる。
    本作業は現行の正準 URN を**固定して見張る**だけで、契約の意味論は変えない（platform と同方針）。
  - `EventBackwardCompatibilityTests` の snapshot キーを FQN 化する改修（後述「代替案」で棄却）。

## 設計

### 1. `EventMessageUrnTests`（新規・テストのみ）

`backend/Shared/AiStockTrading.Shared.Contracts.Tests/EventMessageUrnTests.cs`。3 本のテストで構成する。

1. **URN の固定（`[Theory]` + `[InlineData]`）**
   全 17 イベント型について `MassTransit.MessageUrn.ForType(t).ToString()` が
   `urn:message:AiStockTrading.Shared.Contracts.Events:<TypeName>` と一致することを固定する。
   **期待値は文字列リテラルで直書きし、実装から導出しない**（テスト側で `t.Namespace` から組み立てると、
   名前空間が動いたときに期待値も一緒に動いてしまい、ガードとして機能しない = トートロジー）。

2. **母集合の網羅（`[Fact]`）**
   固定済み URN の型集合が `EventTypeDiscovery.GetEventTypes()` と**完全一致**することを検証する。
   - 新イベントを追加したのに URN を固定し忘れる → 失敗する（サイレントな穴を防ぐ）。
   - イベントを削除したのに固定が残る → 失敗する。
   - 母集合を [[IADR-0079]] と同じ `EventTypeDiscovery` に単一化し、監査カバレッジ
     （`AuditConsumerCoverageTests`）・後方互換テストと対象が乖離しないようにする。

3. **ガードが load-bearing であることの構造的証明（`[Fact]`）**
   テストアセンブリ内の**別名前空間**に、イベントと**同名の**ダミー record を宣言し、
   その URN が本物と異なることを検証する。これにより「名前空間だけを動かすと URN が変わり、
   1 のテストが赤になる」ことを CI 上で恒久的に示す（Issue 受け入れ基準②の一時的な赤確認を、
   一過性の手作業ではなくテストとして残す）。

### 2. `MassTransit` パッケージ参照の追加（テストプロジェクトのみ）

`AiStockTrading.Shared.Contracts.Tests.csproj` に `<PackageReference Include="MassTransit" />` を追加する
（バージョンはルート `Directory.Packages.props` の CPM で 8.4.1 に一元管理済み。本作業でバージョンは変更しない）。

- **`MessageUrn.ForType` を実際に呼ぶ**（URN 文字列を自前で組み立てない）。MassTransit の URN 導出規約が
  将来バージョンで変わった場合も、本テストが赤になって検知できる。規約を手写しすると、その退行を見逃す。
- `AiStockTrading.Shared.Contracts`（プロダクション側）は**引き続き MassTransit に依存しない**。
  参照はテストプロジェクトに閉じ、契約アセンブリの依存関係は不変。

### 3. [[IADR-0079]]「既知の限界」への追記

`Type.Name` キーが名前空間移動を検出しないことを明記し、その分担を `EventMessageUrnTests` が担うと追記する。
既存の Accepted な決定内容は変えない（限界の記述の補完のみ）。

## 受け入れ基準

Issue #253 の受け入れ基準を転記する。

- [ ] 全イベント型の正準 URN（`urn:message:...`）を固定する回帰テストが追加され、CI で実行される
- [ ] 名前空間の移動がテスト失敗として検出されることを確認する（一時的な改名での赤確認）
- [ ] IADR-0079（または IADR-0037）に本テストとの検出範囲の分担を追記する

本作業で追加する条件:

- [ ] `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が緑
- [ ] `dotnet format` 済み・警告ゼロ
- [ ] プロダクションコード（`backend/Shared/AiStockTrading.Shared.Contracts` 以下および各 Service）は無変更

## テスト方針

| 受け入れ基準 | 写像先 |
| --- | --- |
| 全イベントの URN 固定 | `全イベントの正準URNは固定値である`（`[Theory]` × 17） |
| 固定漏れの防止 | `URN固定の対象はイベント型の母集合と完全に一致する`（`[Fact]`） |
| 名前空間移動の検出 | `名前空間が変わればURNも変わる_本ガードが名前空間移動を検出できることの証明`（`[Fact]`） |
| （手動）一時改名での赤確認 | 実装時に `Events` 名前空間を一時変更して赤を実測し、結果を PR 本文に記録する |

## 代替案と棄却理由

- **`EventBackwardCompatibilityTests` の snapshot キーを FQN 化する**: 名前空間移動は検出できるようになるが、
  (a) 既存 baseline の全面再生成が必要で、レビュー時に「意図的更新」と「退行」の差分が埋もれる、
  (b) 検出されるのは「型の FQN が変わった」ことであって **wire URN そのものではない**（MassTransit の URN 導出規約が
  変わればずれる）、(c) platform の `EventMessageUrnTests` と手法が乖離し [[ADR-0001]] の整合制約に反する。
  → 棄却。関心事（プロパティ後方互換 / wire 識別子）はテストを分けたほうが失敗時の原因が明確になる。
- **`[MessageUrn]` 属性で URN を明示固定する**: 名前空間を動かしても URN が保たれるが、
  現行の正準 URN 前提（platform が旧 URN 固定を**撤廃済み**）から逸れる。URN 破壊の防止は「動かさない」で足り、
  「動かしても壊れない」まで求めるのは過剰。→ 棄却（対象外に記載）。

## ⚠️ Wolverine 移行時の再検証（[[ADR-0013]] 追随）

本テストは **MassTransit の URN 導出規約**（`urn:message:<Namespace>:<TypeName>`）を前提とする。
[[ADR-0013]]（Accepted 2026-07-25）により、本ユニットは基盤の Wolverine 移行（platform `ADR-0027`）へ
追随することが確定しており、Wolverine はメッセージ識別子の導出規約が MassTransit と異なる。

移行時は本テスト（および同じ前提に立つ [[IADR-0106]] のキュー名固定テスト）を、
Wolverine の識別子規約へ更新するか、移行後の識別子固定テストへ置き換える必要がある。
本 PR 時点で本リポジトリに Wolverine 移行の Issue は起票されていない。

## 実装 ADR の要否

**新規 IADR は作成しない**。本作業は [[IADR-0037]] が「決定」節で明示的に指示した後続実装
（「platform `EventMessageUrnTests` と同型の `MessageUrn` 回帰テストを別 issue/PR で実装する」）の**遂行**であり、
新たな意思決定を含まない。設計上の判断（期待値の直書き・母集合の単一化・MassTransit 参照をテストに閉じる）は
本作業仕様書と [[IADR-0079]] への追記に記録し、決定の権威は既存 IADR に置く。

## 計画書との差異

- 差異: なし。

## 未決事項

- なし。
