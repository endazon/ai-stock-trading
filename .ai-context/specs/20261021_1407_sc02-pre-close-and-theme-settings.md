---
title: SC-02 から閉場前の締め切り・閉場前の手仕舞いとテーマごとの同時保有の上限を表示・設定する（保存値と構成の値の食い違いを示す）（#1407）
type: spec
status: in-progress
related_ids: [FR-10, FR-19, FR-11, SC-02, UC-06, ADR-0070, ADR-0071, ADR-0066, ADR-0009, IADR-0561, IADR-0562, IADR-0527, IADR-0530, IADR-0564]
author: claude (Claude Code)
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0070_pre-close-entry-cutoff-and-flatten.md（決定 5・6）
  - planning:projects/ai-stock-trading/07_adr/ADR-0071_theme-concentration-limit.md（決定 2・5・6）
  - planning#808 / planning#809（裁定 2026-10-21 の「残作業（実装）」）
---

# 仕様書: SC-02 の閉場前の統制とテーマの上限（#1407）

> 本仕様書は実装着手前に作成した（着手 2026-10-21。AST `origin/develop` `179e1e80`）。段 A（サーバ）と段 B（BFF・画面）に分ける。本 PR は段 A。

## 起点となる計画書（トレーサビリティ）

- 画面: **SC-02**（リスク設定）。機能要求: **FR-10**、FR-19、FR-11。UC-06。
- 関連 ADR: **ADR-0070 決定 5・6**、**ADR-0071 決定 2・5・6**（原文を読んだ。計画 `origin/main` `577d21f`）、ADR-0066 決定 2（設定の手続きの先例）、ADR-0009（手仕舞いは止めない）
- 裁定: planning#808・planning#809 の 2026-10-21 のコメント
- 新規 IADR: **IADR-0564**。追記: IADR-0561・IADR-0562（計画 ADR が起きたこと・置き場の変更）

## 方針

IADR-0564 の決定 1〜6 のとおり。要点:

- 置き場はリスク管理の設定行。効く値は「保存値があれば保存値・無ければ構成の値」（項目ごと）。
- 手仕舞いの構成（`PreCloseFlatten:Enabled`・`:Minutes:<Market>`）はリスク管理へ移す。取引判断は効いている値をサイジング文脈で受け取る。
- 保存値と構成の値の食い違いを GET の応答で返し、画面が示す。

## 母集合（規則 9・10）

走査語: `PreCloseEntryCutoffSettings`・`ThemeConcentrationSettings`・`PreCloseFlattenOptions`・`PreCloseFlatten:`・`new OrderScreeningService(`・`SizingContextView(`・`SettingsChangeType`・`HighVolatilityChanged`（種別の写像の追随先）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `RiskManagementSettings`・設定行の直列化 | 保存値 3 つを足す（null＝保存していない） |
| 2 | `OrderScreeningService`（新規建ての審査・予約の組み直し） | 構成の値を 1 引数で受け、審査のたびに効く値を解決する |
| 3 | `new OrderScreeningService(` の試験 14 ファイル（統合試験を含む） | 引数を `ConfiguredSessionAndThemeControls.Default` に置き換える（挙動は同じ） |
| 4 | `SizingContextService`・`SizingContextView`・REST と gRPC の写し（送り手・受け手）・proto・proto の基準 | 手仕舞いの効いている値を足す |
| 5 | 取引判断の `PreCloseFlattenOptions`・ローダー・`PreCloseFlattener`・`Program.cs`・試験 | 有効と M をサイジング文脈から受け取る。構成のキーが残れば起動を止める |
| 6 | リスク管理の `Program.cs`（構成の読み取り） | 手仕舞いのローダーを足し、3 つを構築時に読む（範囲外は起動を止める） |
| 7 | `SettingsChangeType`・種別の整数の試験・画面の種別の写像（`contracts.ts`）・統合試験 | 11・12 を末尾へ足す。画面の写像も足す |
| 8 | 契約フィクスチャ（`risk-controls.settings.json`）・新しい 2 つのフィクスチャ・フロントの契約型 | 再生成し、フロントの型と取り込みを足す |
| 9 | helm の values の注記（risk-management・trade-decision） | 手仕舞いのキーの置き場を改める（実際の構成は無い） |
| 10 | `docs/functional/FR-10_risk-controls.md`・`docs/tests/FR-10_risk-controls-tests.md` | 節と試験の表を足す・設定の行を改める |
| 11 | IADR-0561・IADR-0562 | 日付つきの追記 |
| 12 | `docs/screens/20260718_SC-02_risk-settings.md`・BFF・画面 | **段 B**（本 PR の範囲外） |

除外: Discord Bot・報告書（種別の整数を読まない。`HighVolatilityChanged` の走査で当たらなかった）。

## 受け入れ基準（段 A）

- [x] AC-1: 保存が無ければ構成の値が効き、食い違いは無い（T-10-2945）
- [x] AC-2: 保存値は往復する。旧行と値域の外の永続値は構成の値で読む（T-10-2946）
- [x] AC-3: 利用者だけが保存できる（T-10-2947）
- [x] AC-4: 値域外・理由なし・欠けた市場・重複は 400 で、設定も履歴も変えない（T-10-2948）
- [x] AC-5: 保存値が効き、構成と違えば食い違いを示し、履歴に前後値つきで残る（T-10-2949）
- [x] AC-6: 審査は保存した締め切りとテーマの定義で判定する（再起動なし）（T-10-2950・T-10-2951）
- [x] AC-7: サイジング文脈が手仕舞いの効いている値を運び、本番の配線で構成の値が渡る。範囲外の構成は起動を止める（T-10-2952）
- [x] AC-8: 受け手（取引判断）が REST・gRPC で同じ値を読み、旧応答・欠け・値域外は既定で読む（T-10-2953）
- [x] AC-9: 手仕舞いの常駐は設定の値で判定し、閉場の 60 分より前は照会しない。取引判断に残った構成のキーは起動を止める（T-10-2933 の更新）
- [x] AC-10（PR #1408 のレビュー）: 照会に失敗した巡回は直近に受け取った値で判定する（T-10-2955）
- [x] 変異 SC1〜SC7 がそれぞれ赤になる（試験仕様書の表）

## 段 B（別 PR）

- BFF の素通し 4 本。SC-02 の 2 つの区画（閉場前の統制・テーマの定義）。効いている値・出どころ・食い違いの表示。方針の行による有効化の注記。
  テーマの銘柄コードの誤記の防止（IADR-0530 の形）。緩める変更の確認。i18n・E2E・SC-02 の画面設計書。
