---
title: テスト戦略 — 受け入れ基準の写像規約と統制系の網羅方式
type: test
status: approved
created: 2026-08-03
updated: 2026-08-03
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-10, FR-12, FR-15, FR-19, FR-20]
adrs: []
iadrs: [IADR-0049, IADR-0127, IADR-0128]
specs: [01_requirements, 05_trading-assumptions, 20260803_343_regression-test-foundation, DEFINITION_OF_DONE, IADR-0127_plan-conformance-known-deviation-registry, traceability]
issues: [#343]
-->


# テスト戦略

全面再実装（[#344](https://github.com/endazon/ai-stock-trading/issues/344)）では既存実装を破棄し得るため、
**退行の検知手段をテストへ移す**。本書はその共通規約であり、各ドメイン issue のテストはこれに従う。

本システムは資金を扱う。**統制系（リスク統制 FR-10・取引ガード FR-19・段階ゲート FR-20）の網羅を最優先**とする。

## 1. 受け入れ基準 → テストの写像規約

- 受け入れ基準 1 項目 = `[Fact]` 1 本、または `[Theory]` の 1 ケース群に写像する。
- テストメソッドの直上コメント、またはクラスコメントに**起点 ID**（`FR-xx` / `UC-xx` / `SC-xx` / `ADR-xxxx` / `IADR-xxxx`）を残す。
- テスト名は日本語でよい（識別子に全角記号は使えない）。「何が起きたら何になるか」を名前で読み切れるようにする。

```csharp
// FR-10, ADR-0018: 日次損失上限（equity の 2%）に到達したら当日は新規建てを停止する
[Fact]
public void 日次損失が上限に達したら新規建てを拒否する() { /* ... */ }
```

CI の `test-traceability` ジョブ（`scripts/check-test-traceability.js`）が次を強制する。

1. **必須範囲 FR**（網羅裁定 [#211](https://github.com/endazon/ai-stock-trading/issues/211): FR-10 / 12 / 15 / 19 / 20）が、それぞれ 1 本以上のテストから参照されていること
2. 必須範囲 FR に機能仕様書（`docs/functional/`）とテスト仕様書（`docs/tests/`）が存在すること
3. テストが参照する FR / UC / SC が計画書に実在すること（PR CI では planning submodule を取得しないため skip し、夜間の `doc-links-planning` が担う）

## 2. 統制系の網羅方式（3 点セット）

統制系のテストは、次の 3 種を**必ず揃える**。1 種でも欠けたら統制のテストとして不完全とみなす。

| 種別 | 何を確かめるか | 例 |
| --- | --- | --- |
| **境界値テーブル** | 閾値の直下・一致・直上を `[Theory]` で網羅する | 維持率 39.9% / 40.0% / 40.1%、株価 $4.99 / $5.00 / $5.01 |
| **プロパティベース** | 入力によらず成り立つ不変条件を確認する | 「縮小後の維持率は必ず回復目標以上」「複数の上限が掛かるとき常に厳しい方が効く」 |
| **否定形** | 統制を**迂回できない**ことを確認する | 「kill switch 中に新規建てが通らない」「LLM の出力で上限を上書きできない」「別の入口（再送・訂正）からも拒否される」 |

**否定形を標準へ含める理由**: 統制のテストは「正常系で正しく拒否されること」だけを見て終わりやすい。
実際の事故は**迂回経路**（別のエンドポイント、別のフィールド、再送・訂正の経路）で起こる。
「塞いだこと」ではなく「**塞ぎ残しが無いこと**」を確かめる形にしなければ、統制のテストは意味を持たない。

### 境界値の書き方

```csharp
// FR-10, ADR-0016 決定7: 空売りの株価下限は USD 5.00（未満は対象外）
[Theory]
[InlineData(4.99, false)]
[InlineData(5.00, true)]
[InlineData(5.01, true)]
public void 空売りは株価5ドル未満を拒否する(decimal price, bool allowed) { /* ... */ }
```

閾値そのものは**マジックナンバーで書かず**、統制設定から引く。設定値の正しさは
`AiStockTrading.PlanConformance.Tests` が計画書と突き合わせて別途保証する（責務を分ける）。

## 3. 計画確定値の適合検査（既知逸脱レジストリ）

`backend/Tests/AiStockTrading.PlanConformance.Tests` が、計画書の確定値（05_trading-assumptions §5・
ADR-0008 / 0016 / 0018）と実装の既定値を突き合わせる。再実装の途上で受容する逸脱は
`KnownPlanDeviations` に**担当 issue 付き**で登録する。

**担当 issue が値を直したら、登録簿から該当行を消さない限り CI が赤になる**（IADR-0127: 計画確定値の適合は「計画値テーブル＋既知逸脱レジストリ」で検査し、逸脱の解消を機械的に強制する）。
逸脱の解消が記録へ反映されることが機械的に保証されるため、「直したが登録簿が古いまま」という状態が残らない。

新しい統制値を実装するときは:

1. `PlanRiskDefaults` に**計画側の確定値**を追加する（実装値を書き写さない）
2. `ActualDefaults.Snapshot()` に**実装からの機械的な抽出**を追加する
3. 一致するなら完了。逸脱するなら `KnownPlanDeviations` へ担当 issue と理由を添えて登録する

**振る舞いの規則**（「厳しい方が効く」「kill switch 中でも手仕舞いは止めない」など）は値ではないため
本レジストリでは扱わない。上記 3 点セットのテストで担当 issue が検証する。

## 4. カバレッジ運用（floor と ratchet）

- 行カバレッジの下限は `coverage-floor.json` の `lineRateFloor`。CI（`build-and-test`）が `scripts/check-coverage.js` で強制する。
- 集計は **(ファイル, 行番号) の和集合**で行う。複数テストプロジェクトが同じアセンブリを計測するため、
  レポートの `lines-valid` を単純に足すと二重計上になる。
- **自動では上げない**。`node scripts/check-coverage.js --suggest` が引き上げ候補（実測 − 余裕 2%）を出すので、
  更新は人手の PR で行う。自動 ratchet は不安定テストの揺れで floor が跳ね上がり、無関係な後続 PR を落とす。

## 5. テストの層と実行区分

| 層 | 置き場所 | CI |
| --- | --- | --- |
| ドメイン単体 | `backend/Services/<Svc>/tests/<Svc>.Domain.Tests` | 既定 CI |
| アプリケーション単体 | `.../<Svc>.Application.Tests` | 既定 CI |
| ホスト / エンドポイント（Api） | `.../<Svc>.Api.Tests`（`WebApplicationFactory<Program>` 系・配線） | 既定 CI |
| 技術詳細（Infrastructure） | `.../<Svc>.Infrastructure.Tests`（EF Core・consumer・外部 API アダプタ） | 既定 CI |
| 層の依存規律（横断） | `backend/Tests/AiStockTrading.Architecture.Tests`（csproj の静的解析・IADR-0128: 標準プロジェクト構成は「Worker を Api / Infrastructure に割り、実体のある層だけを作る」形で実現する） | 既定 CI |
| 計画適合（横断） | `backend/Tests/AiStockTrading.PlanConformance.Tests` | 既定 CI |
| 実基盤結合（Testcontainers） | `backend/Tests/AiStockTrading.IntegrationTests` | `Category=Integration`。既定 CI から除外し `integration.yml`（夜間/手動）で実走 |

## 6. 未整備（担当 issue で追加する）

本作業では枠組みのみを定め、実体は対象の実装が入る issue で追加する。

| 項目 | 追加する issue | 理由 |
| --- | --- | --- |
| フェイクブローカー / フェイク LLM によるサイクル 1 周のシナリオテスト | #331 / #335 / #337 | 対象の実体が無い段階では書けない |
| moomoo `SIMULATE` 結合テスト | #342 | PoC（2026-08-31 期限）の完了が前提 |
| フロント Playwright E2E | #340 | platform#446 の新スタック追随後 |
| 性能ゲート（取引サイクル 10 分 / 変動→発注 5 分） | #337（実測は #203 を接続） | 取引サイクルの実体が必要 |

## 変更履歴

| 日付 | 内容 |
| --- | --- |
| 2026-08-03 | 初版作成（#343・全面再実装の退行防止テスト基盤） |
