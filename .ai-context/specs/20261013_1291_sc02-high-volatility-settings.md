---
title: SC-02 から高ボラティリティ銘柄の明示指定と区分の上限を表示・設定する（BFF 経由・誤記の防止つき。計画 ADR-0066 決定 2）（#1291）
type: spec
status: accepted
related_ids: [FR-10, FR-11, UC-06, SC-02, ADR-0066, ADR-0063, ADR-0042, IADR-0530, IADR-0527, IADR-0086, IADR-0090, IADR-0071, IADR-0288, IADR-0151]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0066_high-volatility-cap-floor-fixed-and-sc02-setting.md (決定 1・決定 2・決定 3・フォローアップ 1)
  - planning:projects/ai-stock-trading/05_screens/01_screens.md (SC-02 主要素「高ボラティリティ銘柄の明示指定と区分の上限」・入力の表・設定変更の一般則)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5「高ボラティリティ銘柄の区分」「高ボラティリティ銘柄の 1 注文あたりの発注金額上限」)
---

# SC-02 の高ボラティリティ銘柄の設定（#1291・ADR-0066 決定 2）

## 背景

- 計画 ADR-0066 決定 2（planning#793 → planning#794・利用者裁定 2026-10-13）: SC-02 に明示指定（銘柄と市場の一覧）と区分の上限（equity 比）を表示し、設定できるようにする。
  - 手続きは §5 の他の統制値と同じ（OwnerOnly・理由必須・値域〔1%〜25%〕外は拒否・履歴）。拒否の実効はサーバ。
  - 経路は既存の `PUT /risk-controls/settings/high-volatility` を BFF 経由で使う。新しい API は作らない。
  - **画面は銘柄コードの誤記を防ぐ手段を持つ**（方式は実装に任せる）。
- 計画の画面設計（05_screens SC-02）も同日に同じ要件を足した（入力の表: 区分の上限は必須・数値・0.01〜0.25・空欄/非数値は保存無効・理由必須／明示指定は任意・誤記を防ぐ手段）。

## 実測（origin/develop `6b2f0f31`）

- サーバ: `PUT /risk-controls/settings/high-volatility`（`{maxOrderAmountRatio, designatedSymbols:[{symbol, market}], reason}`。全置換・OwnerOnly・値域外/理由なし/省略は 400・履歴 `HighVolatilityChanged`＝序数 10）。`GET /risk-controls/settings` は `highVolatility` を返す（契約フィクスチャにもある）。
- BFF（`RiskControlsBffEndpoints`）に高ボラの `PUT` が無い。他の統制値（limits / guard / broker-provider / stage1 / stop-loss-method）は素通しの `MapPut` が 1 本ずつ。
- フロント: `contracts.ts` は `HighVolatilitySettings` 型と履歴の種別ラベル（10）を既に持つ。SC-02 の入力は無い（注記「まだ無い」）。保存は `useRiskSettingsMutation(path)` の形で 1 本ずつ。監視銘柄は `useWatchlist()`（`/monitor/watchlist`）が SC-02 で既に使われている。
- 銘柄の実在を答える口（シンボル辞書・ブローカーの銘柄照会）は本リポに無い。

## 設計（IADR-0530）

- **誤記の防止**: 監視銘柄からの選択を主の経路にし、手入力（先回りの指定）は (1) 大文字へ正規化・書式（英数字と `.` `-` の 12 文字以内）で拒む、(2) 監視銘柄と照合して無い・市場が違う・照合できないなら警告、(3) 確認の印を付けるまで追加できない。保存済みの指定の各行にも照合の印を出す。比較表と理由は IADR-0530 決定 1。
- **フォーム**: `HighVolatilityForm`（リスク上限の直後・保存単位は別）。区分の上限は百分率で入力し実額を併記。全置換・変更が無ければ保存不可・理由必須・緩める変更（上限を上げる・指定を外す）は確認・400 は詳細表示で自動再試行しない。
- **BFF**: `PUT /bff/risk-controls/settings/high-volatility` の素通しを 1 本足す。
- 現在値の追随は描画中の同期調整（`useEffect` を使わない。#498）。

## 母集合（規則 9・10）

「まだ無い」「BFF の経路」「SC-02 の表示・入力」「フォローアップ 5」を `git grep` で走査した（`.ai-context/specs/` は凍結記録として除外）:

| 場所 | 扱い |
| --- | --- |
| `RiskManagementService/.../UpdateHighVolatility/Endpoint.cs:9`（「画面と BFF の経路はまだ無い」） | 改める |
| `frontend/src/lib/risk/contracts.ts:73-74`（「SC-02 の表示・入力はまだ無い」） | 改める |
| `docs/functional/FR-10_risk-controls.md`（「画面からの表示・入力と BFF の経路はまだ無い」） | 改める |
| `docs/tests/FR-10_risk-controls-tests.md`（残余リスク「明示指定の画面と BFF の経路は無い」） | 改める |
| `IADR-0527` §残る制約（「SC-02 の表示・入力・BFF の経路は無い」） | 凍結記録のため本文は残し、日付つき追記 |
| `RiskControlsBffEndpoints.cs` 冒頭の追加履歴の注記 | 1 行足す |
| `docs/screens/20260718_SC-02_risk-settings.md`（構成要素・データ取得の表） | 項目 8 と表の行を足す |
| 他サービスの試験の「ブラウザ・BFF の経路」・`Stage0AsOfInputCompleteness.cs` の「フォローアップ 5」 | 無関係（除外） |

新たに誤りになる自分の記述: 直前の PR（#1328）で書いた起動時の警告の文と helm の注記は「`GET /risk-controls/settings` で確かめる」とだけ書いた（SC-02 の入力が無かったため）→ 本 PR で「SC-02 または」を足す。

## 受け入れ基準 → 試験

| 受け入れ基準（ADR-0066 決定 2・05_screens SC-02） | 試験 |
| --- | --- |
| BFF に `PUT /bff/risk-controls/settings/high-volatility` が認証必須で登録されている | T-10-2591 |
| BFF は本文を後段へ素通しし、200 / 400 / 403 を透過する（統制は後段） | T-10-2592 |
| 現在の上限と明示指定を表示し、保存済みの指定に監視銘柄との照合の印を出す | T-10-2593 |
| 理由必須・変更が無ければ保存しない | T-10-2594 |
| 監視銘柄から選んで追加し、全置換の本文を PUT する | T-10-2595 |
| 🔴 監視銘柄に無い手入力は警告し、確認するまで追加できない（大文字へ正規化） | T-10-2596 |
| 🔴 市場の取り違えを警告する。監視銘柄にある組は確認なしで追加できる | T-10-2597 |
| 🔴 書式の誤り・重複は追加できない | T-10-2598 |
| 区分の上限の値域 1%〜25%（両端を含む）。外は保存しない | T-10-2599 |
| 緩める変更（上げる・外す）は確認を求め、締める変更は求めない。緩める変更が増えたら確認を外す | T-10-2600 |
| サーバの 400 は詳細を表示し、自動再試行しない | T-10-2601 |
| 🔴 監視銘柄を取得できないときは照合できないと示し、手入力に確認を求める | T-10-2602 |
| 実ブラウザで BFF の経路と誤記の防止を追認する | T-10-2603（E2E） |
| 🔴 監視銘柄の取得中は「照合中」と出し、警告を点滅させず、手入力も追加させない（AI レビューの指摘で追加） | T-10-2604 |
| 🔴 手入力の確認の印は確かめた銘柄・市場に紐づき、書き換えたら外れる（AI レビューの指摘で追加） | T-10-2605 |

## 残る制約

- サーバは銘柄コードの書式も実在も検証しない（API を直接呼べば誤記は通る）。
- 照合は監視銘柄との突き合わせであり、実在の検証ではない。
- モックアップ（hi-fi / wireframe の sc-02）は未反映（ADR-0066 フォローアップ 2。人間）。
