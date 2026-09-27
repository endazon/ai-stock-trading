---
title: 監視銘柄の全体置換と初回シードの構成で未定義の市場を拒み、費用の倍率の数値の文字列の書式を以前どおりにする（#1064 の監査の残り）
type: spec
status: done
related_ids: [FR-01, FR-02, FR-13, NFR, IADR-0031, IADR-0282, IADR-0437, IADR-0447]
author: endazon (with Claude Code)
created: 2026-09-27
updated: 2026-09-27
---

# 仕様書: #1064 の監査の残り（#1065）

## 起点

- issue #1065（PR #1064 の監査で出た、止めない残り F1・F2a）。判断の訂正は [IADR-0447](../adr/IADR-0447_grpc-read-latent-residuals.md) の
  ［2026-09-27 追記 / #1065］（新しい IADR は起こさない —— 決定は変えず、到達可能性の記述の訂正と入口の追加である）。

## 項目

| 項目 | 事実（`origin/develop` `40d992ee`） | 方針 |
| --- | --- | --- |
| F1 | `MonitorSettingsService.Replace`（`PUT /monitor/settings`・OwnerOnly）は市場の定義を確かめず `(Market)7` を保存する。`MonitorWatchlistService.Add` だけが `Enum.IsDefined` を見る。初回シードの構成の束縛（`MonitorSeedOptions`）も番号の `"7"` を通す | 全置換は未定義の市場を 400（`ArgumentException` → 群のフィルタが 400 に写す）。シードの構成は `ValidateOnStart` で起動時に止める（束縛は解決時に構成を読む `BindConfiguration`） |
| F2a | `HttpCostControlGate` の数値の文字列の書式が `NumberStyles.Float` で、前後の空白（`" 2"`）を許す。以前の Web 既定の逆直列化は拒み Normal 1 倍だった | `AllowLeadingSign | AllowDecimalPoint | AllowExponent` に絞る |
| 記録 | IADR-0447・#1063 の仕様書の「実在の提供元からは届かない」は、F1 の口から到達し得たので不正確 | 日付つき追記で訂正する（凍結の射程: `.ai-context/specs/` は経過追記が可） |

### 母集合（F1。規則 9: 監視銘柄を保存する口を全数で引く）

`git grep -n 'new MonitoredSymbol(\|Normalize(\|IsDefined' -- backend/Services/MarketMonitorService`（テスト以外）:

| 口 | 市場の検証 | 扱い |
| --- | --- | --- |
| `MonitorWatchlistService.Add` / `Remove`（`Normalize`） | あり | 変更なし |
| `MonitorSettingsService.Replace`（全置換） | **なし** | 是正 |
| `MonitorSeedOptions.ToMonitoredSymbols`（初回シード） | **なし** | 起動時の検証で是正 |
| `MonitorWatchlistService.ApplyProposal` / `WatchlistProposalPlan`（入れ替え案） | 追加は `Market.UnitedStates` 固定 | 対象外（未定義の値が入る道が無い） |
| `ApplyWatchlistProposal/Endpoint.ToSymbol`（期待値） | なし | 対象外（楽観排他の比較にだけ使い、保存しない） |
| `WatchlistAsOfReconstructor`（変更履歴の読み戻し） | あり | 変更なし |

F2a の同型（gRPC の `CostControlWire.IntervalMultiplier` の `NumberStyles.Number`）は、送り手が `decimal.ToString(InvariantCulture)` で
書く線上の値であり REST の以前の読み方との同等性の問題ではないため、本 issue の射程外とした（他の段の 10 進の読み取りと同じ書式）。

## 受け入れ基準

- [x] 全置換は未定義の市場を 400 で拒み、一部も保存しない（1 件の追加と揃える）
- [x] 初回シードの構成の未定義の市場は起動時に止まる。列挙名・定義済みの番号は起動しシードに使われる
- [x] 前後に空白のある倍率の文字列は以前と同じく拒む（停止していない応答なら Normal 1 倍）。以前のコード（`2fb284c1`）でも同じ試験が緑
- [x] 新しい試験は是正前のコードで赤・是正後で緑（実測）
- [x] IADR-0447・#1063 の仕様書の記述を日付つき追記で訂正する
- [x] helm の既定描画・values-local の描画は不変（values-local のシードは列挙名 `UnitedStates` なので起動も変わらない）

## テスト方針（テスト ID は T-10-1720〜T-10-1722。develop の最大 T-10-1713 から間を空けた区画）

| ID | 置き場 | 観点 |
| --- | --- | --- |
| T-10-1720 | MarketMonitorService.Tests | 全置換・1 件の追加の未定義の市場は 400、定義済みは通る |
| T-10-1721 | 〃 | 初回シードの構成の未定義の市場は起動時に止まる |
| T-10-1722 | InformationCollectionService.Tests | 倍率の数値の文字列の書式が以前の Web 既定と同じ |
