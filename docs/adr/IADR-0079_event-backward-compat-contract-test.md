---
title: IADR-0079 イベント契約の後方互換を snapshot 比較の CI 契約テストで機械化し、共通エンベロープ型は上流確定まで繰延に準拠する
type: impl-adr
status: Accepted
related_ids: [ADR-0001, FR-11, IADR-0077, IADR-0078]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-28
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0079: イベント契約の後方互換を snapshot 比較の CI 契約テストで機械化し、共通エンベロープ型は上流確定まで繰延に準拠する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **ADR-0001**（platform 再利用）、FR-11（監査＝全イベントの時系列記録）
- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)（platform 拡張規約への準拠・受け入れ基準①「共通エンベロープ準拠＋契約テスト」）
- platform 規約（原典・隣接リポ `../microservices-platform`）: `10_composability-design §3`（イベント契約の標準化＝共通エンベロープ＋後方互換の追加のみ＋CI 契約テスト）、`IADR-0049`（共通エンベロープ・CI 契約テスト・ステージング適用の**段階適用と繰延**）、`ADR-0018`（固定/可変区分＝エンベロープ標準は固定部）
- 関連 IADR: [IADR-0077](IADR-0077_declarative-pipeline-binding.md)、[IADR-0078](IADR-0078_config-info-self-report.md)

## コンテキストと課題

`#22` 受け入れ基準①は「全イベントが共通エンベロープに準拠し、契約テストがある」ことを求める。前提確認の結果:

- **共通エンベロープ型は platform でも繰延中**（`IADR-0049`）。具体エンベロープ型は platform Shared.Contracts にも
  存在せず、現行規約は「イベントごとの個別 record ＋後方互換の追加のみ許可＋PR レビュー」。
- **エンベロープ標準は `ADR-0018` の固定部＝platform 所有**。拡張側（ai-stock-trading）が独自のエンベロープ型を
  定義するのは、固定契約の無断定義であり、上流が将来定める標準と衝突するリスクがある（憶測実装）。
- 一方、§3 の「互換性は CI の契約テストで検証」は **今 actionable**。platform 自身もこの契約テストは未整備で、
  「後方互換の追加のみ許可」を運用ルール＋PR レビューでしか担保していない。

## 決定

1. **イベント契約の後方互換を CI 契約テストで機械化する。** `AiStockTrading.Shared.Contracts.Tests` を新設し、
   `Shared.Contracts.Events` 名前空間の全 record 型のスキーマ（プロパティ名→型表示）を committed snapshot
   （`event-schemas.baseline.json`）と比較する。**フィールドの削除・改名・型変更を破壊的変更として検出**し、
   **新イベント・新フィールドの追加は後方互換として許容**する（§3「後方互換の追加のみ許可」の機械化）。
   - 基準更新は `UPDATE_EVENT_BASELINE=1`（意図的更新の運用手順）で snapshot を再生成し、差分を PR レビューで確認する。
   - 母集合は監査カバレッジテスト（`AuditConsumerCoverageTests`）と同一（`Events` 名前空間 record）で、監査規約と整合する。

2. **共通エンベロープ型自体の導入は上流確定まで繰延に準拠する。** platform `IADR-0049` の繰延解除条件
   （段の挿抜による横断的フィールド依存の顕在化・ABAC 属性/トレース ID の本体搬送要件確定 等）が満たされた時点で、
   上流 `07_abac-attribute-model.md` と整合するエンベロープ型を platform 側が定める。本リポジトリはそれに準拠して
   本契約テストの検証対象をエンベロープへ拡張する。**それまではエンベロープ型を定義しない。**

## 既知の限界

- **名前空間の移動は検出対象外（→ `EventMessageUrnTests` が分担）**: snapshot のキーを **`Type.Name`（単純型名）**
  で構成するため、イベント型を別名前空間へ移しても（型名・プロパティが不変なら）本テストは**緑のまま**である。
  一方 MassTransit の wire 識別子は正準 URN `urn:message:<Namespace>:<TypeName>` で、名前空間から導出されるため、
  名前空間の移動は **wire 契約を破壊する**（キューに滞留中／`_error` キュー内のメッセージが再消費不能になる）。
  この分担は `EventMessageUrnTests`（[IADR-0037](IADR-0037_async-contract-format-reevaluation.md) の決定・
  [#253](https://github.com/endazon/ai-stock-trading/issues/253)）が担い、全イベントの正準 URN を固定する。
  **実測（#253 の PR で確認）**: 全イベントを `Contracts.Events` → `Contracts.EventsMoved` へ移す実験で、
  本テストは 5/5 緑のまま、`EventMessageUrnTests` は 17 件が赤になった。両テストの検出範囲は以下のとおり。

  | 変更 | `EventBackwardCompatibilityTests`（本 ADR） | `EventMessageUrnTests`（#253） |
  | --- | --- | --- |
  | プロパティの削除・改名・型変更 | 検出する | 検出しない |
  | イベント型の削除・改名 | 検出する | 検出する |
  | **名前空間の移動（URN 破壊）** | **検出しない** | **検出する** |
  | enum メンバーの削除・改名 | 検出しない（下記） | 検出しない |

- **enum メンバーの変更は検出対象外**: 契約テストはプロパティの**型名**単位で比較するため、`OrderStatus` /
  `Market` / `TradeSide` / `RejectionReason` 等の enum の**メンバー削除・改名**は検出しない（プロパティの型名
  `"OrderStatus"` 自体は不変のため）。プロパティ単位の後方互換に絞る設計判断であり、「契約テストが通れば enum の
  値変更も安全」ではない。enum メンバーの後方互換は PR レビューで担保し、必要になれば検証対象を enum メンバー
  集合へ拡張する（共通エンベロープ確定と同時期の後続）。
- **比較ロジック自体の回帰テスト**: `FindViolations` を純関数に切り出し、削除・改名・型変更・追加の各ケースを
  合成スキーマで検証する（比較ロジックのリファクタ退行を CI で検知する）。
- **母集合の単一化**: 検証対象（`Shared.Contracts.Events` の record）は `EventTypeDiscovery.GetEventTypes()` に
  単一化し、監査カバレッジ（`AuditConsumerCoverageTests`）と対象が乖離しないようにする。

## 根拠 / 代替案

- **エンベロープ型を今作らない**: 固定部の無断定義になり、上流標準と衝突しうる。契約テストのみ先行しても、
  検証対象（エンベロープ）が無いと空振り——という platform IADR-0049 の判断と一貫する。actionable な後方互換
  検証を先に入れる方が、実効的な退行防止になる。
- **snapshot 比較を選ぶ**: 型注釈やアナライザでは「削除・改名・型変更」を横断的に捕捉しにくい。committed snapshot
  との差分比較は、意図的変更（基準更新＋レビュー）と非意図的退行を明確に分離できる。
- **`connectors`/トピック命名/冪等性キーは対象外**: いずれも共通エンベロープ標準の一部として上流確定に依存する。
  本テストは「個別 record の後方互換」に閉じ、エンベロープ確定時に拡張する。

## 影響

- 追加: `AiStockTrading.Shared.Contracts.Tests`（プロジェクト・契約テスト・基準 snapshot）を `backend.slnx` へ登録。
- ドキュメント: 本 IADR、`events-and-ports.md` の未決事項に契約テストの存在と境界を明記。
- コード・イベント契約変更なし（監査 Consumer 追随不要）。

## フォローアップ

- 共通エンベロープ型の導入は platform `IADR-0049` の繰延解除時。確定したら本契約テストをエンベロープ検証へ拡張し、
  トピック命名・冪等性キーの platform 準拠も併せて詳細化する（`#22` の残・`Refs`）。
