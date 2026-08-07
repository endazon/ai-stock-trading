---
title: 禁止銘柄の一時解除 Runbook（建玉を手仕舞えないときの手順）
type: runbook
status: draft
related_ids:
  - FR-19
  - FR-10
  - FR-11
  - UC-06
  - ADR-0007
  - IADR-0132
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md"
---

# Runbook: 禁止銘柄の一時解除（建玉を手仕舞えないときの手順）

> リポジトリ単位の運用 Runbook。起点: [#380](https://github.com/endazon/ai-stock-trading/issues/380) /
> [ADR-0007](../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md) の 2026-08-04 追補。

## 症状 — **建玉が閉じられない**

禁止銘柄リストに登録した銘柄の建玉を手仕舞おうとすると、**注文が拒否される**。

```
拒否理由: BannedSymbol
```

**これは不具合ではない。設計どおりである。**

## なぜそうなっているか

**取引禁止銘柄ガードは「全注文」に適用される** —— 新規建てだけでなく**手仕舞いも拒否する**（ADR-0007 2026-08-04 追補・利用者裁定 質問票 第 1 回 Q4）。

理由は計画に明記されている。

> **インサイダー取引は売付けも対象**である。AI が利用者の関知しないタイミングで規制対象銘柄を自動売却する経路を残さない。

つまりこのロックインは**受容された代償**であり、緩めてはならない。

> **他のガードと適用範囲が違うのは意図である。** ADR-0007 追補は「ガードごとに適用範囲が異なるのは**各ガードの目的が異なるためであり、揃えるべき不整合ではない**」と明示している。**「一貫性がない」として揃えないこと。**

| ガード | 適用範囲 |
| --- | --- |
| 商品種別（現物 / 信用買い / 空売り） | 新規建て（Open）のみ |
| 市場別の有効 / 無効 | 全注文 |
| **取引禁止銘柄リスト** | **全注文（手仕舞いにも適用）** ← 本 Runbook の対象 |
| 差金決済防止 | 新規建てのみ |
| 相場操縦とみなされ得る発注パターン | 全注文 |

## 手順 — **一時解除 → 手仕舞い → 再登録**

**これは統制を外す抜け道ではない。** 計画の意図は「**規制対象銘柄の処分に人間の明示的な判断を要求する**」ことであり、**手順を踏むこと自体が統制**である。

### 1. 一時解除

対象銘柄を禁止銘柄リストから外す。

- 画面: **SC-02**（リスク統制設定）のガード設定
- API: **`PUT /risk-controls/settings/guard`**（`RiskControlEndpoints.cs` の `MapGroup("/risk-controls")` ＋ `MapPut("/settings/guard")`）
- BFF 経由: **`PUT /bff/risk-controls/settings/guard`**（`RiskControlsBffEndpoints.cs`）

**アクターと理由は必須である**（`RiskSettingsService.RequireActorAndReason`）。理由には**手仕舞いのための一時解除であること**と**再登録の予定**を書く。

### 2. 手仕舞い

対象建玉を決済する。**この間、当該銘柄は新規建ても可能な状態にある** —— 手順 3 までの時間を最小にすること。

### 3. 再登録

**手仕舞いの完了を確認してから**、対象銘柄を禁止銘柄リストへ戻す。理由には**一時解除の完了**を書く。

> ⚠️ **再登録を忘れると統制が外れたままになる。** 現時点で「解除しっぱなし」を検知する仕組みは無い（後述「本 Runbook の限界」）。

## 監査への記録 — **自動で残る（追加の操作は不要）**

解除・再登録はいずれも設定変更履歴と監査ログに残る。**運用者が別途記録する必要はない。**

| 計画が要求する項目 | 記録される場所 |
| --- | --- |
| **日時** | `SettingsChangeEntry.ChangedAt` |
| **操作者** | `SettingsChangeEntry.Actor`（`RequireActorAndReason` により必須） |
| **対象銘柄** | `SettingsChangeEntry.Before` / `After`（禁止銘柄リストはガード設定の一部であり、前後値に含まれる） |
| 理由 | `SettingsChangeEntry.Reason`（必須） |
| 種別 | `SettingsChangeType.Guard` |

**コードの所在**:

- `backend/Services/RiskManagementService/src/RiskManagementService.Application/Services/RiskSettingsService.cs`（`UpdateGuard` → `RequireActorAndReason` → `Save(..., SettingsChangeType.Guard, actor, reason)`）
- `backend/Services/RiskManagementService/src/RiskManagementService.Application/State/SettingsChangeEntry.cs`

照会は **UC-06 / UC-07** の設定変更履歴から行う。

## 本 Runbook の限界（正直に記録する）

1. **実機で試していない。** 実弾運用が未開始のため、**実際に建玉を持った状態での一連の手順は未検証**である。手順は計画の裁定と実装コードの読解から書いている。
2. **「解除しっぱなし」を検知する仕組みが無い。** 一時解除したまま再登録を忘れても、**警告は出ない**。運用者の注意に依存する。
3. **解除中は新規建ても可能になる。** ガードは注文の種類を区別せずリストを引くため、解除している間は当該銘柄への新規建てが通る。**時間を最小にすること**以外の防御は無い。
4. **対象銘柄の特定は before/after の差分から行う。** 変更履歴はガード設定全体の前後値を持つため、「どの銘柄を解除したか」は**差分を読んで判断する**必要がある（銘柄単位の専用イベントは持たない）。

## 関連

- 計画: [ADR-0007](../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md)（2026-08-04 追補が適用範囲と本手順を定める）
- 実装 ADR: [IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md)（商品種別の 3 値化とガード適用範囲）
- 機能仕様: [FR-19 取引ガード](../functional/FR-19_trading-guard.md)
- 作業仕様書: [20260807_380_guard-scope-arbitration](../specs/20260807_380_guard-scope-arbitration.md)
- 運用仕様書: [operations.md](operations.md)
