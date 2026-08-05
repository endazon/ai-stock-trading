---
title: 画面仕様書（素案） — SC-02 リスク設定画面（リスク上限の閲覧/変更）
type: screen
status: Draft
related_ids: [SC-02, FR-10, FR-13, FR-19, FR-20, FR-12, UC-06, ADR-0003, ADR-0007, ADR-0008, IADR-0130, IADR-0140, IADR-0141, IADR-0151]
issue: 106
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-08-05
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/05_screens/01_screens.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
related_specs:
  - ../specs/20260718_106_frontend-risk-settings-and-controls.md
  - ../specs/20260718_196_frontend-watchlist-ui.md
  - ../adr/IADR-0084_frontend-risk-settings-and-control-status.md
  - ../adr/IADR-0086_frontend-guard-edit-ui.md
  - ../adr/IADR-0090_frontend-watchlist-ui.md
  - ../adr/IADR-0130_equity-ratio-risk-limits.md
  - ../adr/IADR-0140_broker-provider-axis.md
  - ../adr/IADR-0141_live-switch-explicit-confirmation.md
  - ../adr/IADR-0151_risk-limit-percent-input-and-bounds.md
  - ../specs/20260805_334_broker-provider-axis.md
  - ../specs/20260805_362_sc02-ratio-input.md
---

# SC-02 リスク設定画面（リスク上限の閲覧/変更）【素案】

> 起点: **FR-13**（利用者が設定を変更できる）、FR-19（相場操縦ガード）、FR-20（段階）、**UC-06**。計画リポジトリ `05_screens/`
> は空のため SC-02 は素案（project-planning#33・#31 後続 で環流）。データ源は RiskManagementService `/risk-controls/settings`（OwnerOnly）。

## 画面の位置づけ

platform SPA 認証済みレイアウト配下に feature `sc02-risk-settings` としてマウント（route `settings/risk`・nav「リスク設定」）。
SC-01（FR-17 全体前提条件・ConfigurationService）とは別サービス由来のため独立画面とする（[IADR-0084](../adr/IADR-0084_frontend-risk-settings-and-control-status.md)）。

## アクセス制御

- 表示・変更とも利用者（`trading-owner`）限定。`RequireRole anyOf=['trading-owner']`・権限外は `NotFound`（存在秘匿）。
- 実効認可はサーバ側（`/risk-controls/settings` = OwnerOnly）。権限外では構成 API を呼ばない。

## 構成要素

1. **リスク上限（変更可・#362／[IADR-0151](../adr/IADR-0151_risk-limit-percent-input-and-bounds.md)）**: `RiskLimitSettings` の 8 項目。
   数値入力（文字列保持・送信時に数値化）。**「保有銘柄数上限」の語は用いない**（ADR-0016 決定9・計画 §5。
   同一銘柄で複数の建玉を持ち得るため銘柄数と建玉数は一致しない）。

   ### 割合の画面表現 — **百分率（`25%`）を採る**（#362 の裁定事項・[IADR-0151](../adr/IADR-0151_risk-limit-percent-input-and-bounds.md) 決定1）

   [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md) 決定1 により、金額系の統制上限は **equity 比**で保持される。
   その割合を画面でどう表すか（`0.25` か `25%` か）は #362 の裁定事項であり、**百分率（%）**を採る。

   - **画面（表示・入力）は %**。ワイヤ（HTTP・永続化・ドメイン）は**比率のまま**（バックエンドは無改変）。
     既定値は `25` / `150` / `2` / `1` / `10`（%）と表示される。
   - **根拠1（計画）**: 計画 05_screens SC-02 は表記例を **「25%（$750）」**と百分率で明示している。
   - **根拠2（誤入力が外れる向き）**: 比率入力で `25` と打てば **equity の 25 倍**（統制の消滅）。百分率入力で
     `0.25` と打てば **0.25%**（過度に厳しい＝発注が通らないだけ）。**外れる向きが安全側である方**を採る。
   - **% ⇄ 比率の変換は `contracts.ts` の 2 関数だけを通す**（呼び出し側で `× 100` と書かない）。変換は
     10 進文字列の小数点移動で行い、丸め誤差を統制値へ紛れ込ませない。
   - **単位が異なる 3 項目は % にしない** — 保有建玉数上限・連敗しきい値は**件数**、連敗時サイズ縮小係数は**倍率**である。
     **各入力欄には単位を明示表示する**（`%` / `%/日` / `件` / `連敗` / `倍`）。

   ### 実額の併記（issue #362・[IADR-0151](../adr/IADR-0151_risk-limit-percent-input-and-bounds.md) 決定4）

   割合だけでは利用者が実効額を判断できないため、**equity 比の 5 項目には現在 equity での実額を併記する**
   （1注文発注額上限・1日発注額上限・日次損失上限・1取引あたりリスク・最大DD上限）。

   - equity の出どころは **`GET /risk-controls/status` の `capital`**（[IADR-0130] 決定2 が定めた「判定に用いる
     自己資金＝前営業日終値時点の評価額」）。**新しい取得口を作らない。**
   - **保存前の入力値に対する実額は画面が `capital × 入力比率` で計算する。** サーバの
     `RiskStatusView.maxOrderAmount` は**現在保存されている設定**から解決した実額であり、入力中の値は表せない。
   - **通貨は基準通貨（円）建てで表示し「$」を付けない。** 計画は USD 表記（`$750`）を求めるが、実装が供給する
     `capital` は円建てである（[IADR-0130] 決定3・`MarketCurrency.Base = Jpy`）。**円建ての数値に「$」を付けることは
     単位の取り違えそのもの**であるため行わない。USD 表記への切替は判定通貨の USD 移行と同時に行う（未決事項）。
   - **equity を取得できないときは実額を「—」とし、その旨を明記する**（併記できないことを黙って隠さない）。

   ### 値域バリデーション（[IADR-0151](../adr/IADR-0151_risk-limit-percent-input-and-bounds.md) 決定2）

   統制を無効化する値を保存させない。**画面（送信前の即時提示）とサーバ（`RiskLimitBounds`・実効）の両方**に置く
   （画面だけの統制は API 直叩きで消える＝[IADR-0141](../adr/IADR-0141_live-switch-explicit-confirmation.md) 決定1 と同じ判断）。

   | 項目 | 画面のラベル / 単位 | 範囲 |
   | --- | --- | --- |
   | `maxOrderAmountRatio` | 1注文発注額上限（equity 比） `%` | 0 < v ≤ 100 |
   | `maxDailyOrderAmountRatio` | 1日発注額上限（equity 比） `%/日` | 0 < v ≤ 1000 |
   | `maxOpenPositions` | 保有建玉数上限 `件` | 1 ≤ v ≤ 20（整数） |
   | `dailyLossLimitRatio` | 日次損失上限（equity 比） `%` | 0 < v ≤ 20 |
   | `perTradeRiskRatio` | 1取引あたりリスク（equity 比） `%` | 0 < v ≤ 10 |
   | `maxDrawdownRatio` | 最大ドローダウン上限（equity 比） `%` | 0 < v ≤ 50 |
   | `losingStreakThreshold` | 連敗しきい値 `連敗` | 1 ≤ v ≤ 20（整数） |
   | `losingStreakSizeFactor` | 連敗時サイズ縮小係数 `倍` | 0 < v < 1（**1.0 は「縮小しない」＝統制の無効化のため不可**） |

   範囲外の項目がある間は**保存ボタンを無効化**し、該当項目と許容範囲を警告表示する。範囲の根拠は IADR-0151 決定2。
2. **ガード（変更可・#188/IADR-0086）**: 有効な商品種別・市場（チェックボックス）、禁止銘柄（追加/削除）、同日再エントリ禁止・
   相場操縦パターン禁止（トグル）を編集。危険な緩和（トグル OFF・禁止銘柄削除・信用の新規有効化）は明示確認を要求（fail-safe）。
3. **運用段階と発注先（参照・#334）**: 現段階・**現在の発注先**・段階の既定発注先を**別々の行**として表示する
   （INDEX 決定 46・05_screens 共通規約:「独立した 2 軸であり 1 行に混ぜて表示しない」）。段階変更は段階ゲート承認フロー
   （#165 Bot 側）。
3-2. **発注先の変更（変更可・#334/IADR-0141）**: 3 値（内蔵 `paper` / moomoo `REAL` / moomoo `SIMULATE`）から選ぶ。
   **変更操作を持つ画面は SC-02 だけである**（SC-03 は参照専用）。理由必須・監査ログ・版の対象。
   **実弾（moomoo `REAL`）への切替は警告モーダルを必ず経由し、計画の 4 点**（①実資金で執行される旨／②切替先と現在の
   Stage の組み合わせ〔Stage 1 のままなら段階ゲートを飛ばしている旨〕／③現在の equity と統制値の実額／④チェックボックスの
   同意と「REAL」の文字入力）**を提示する。「OK」1 押しでは通過できない。** equity を取得できない場合は切替を許さない。
3-3. **内蔵 `paper` 稼働中の警告バナー（#334）**: 画面上部に常時表示（必須 2 文言。FR-12・共通規約）。
4. **変更履歴**: `SettingsChangeEntry[]` を新しい順に一覧（種別・アクター・理由・前後値・日時）。
5. **監視銘柄（変更可・#196/IADR-0090）**: `MonitoredSymbol[]`（銘柄コード・市場）の一覧・追加・削除。データ源は
   **別サービス** MarketMonitorService `/monitor/watchlist`（取得は OwnerOrService・変更は OwnerOnly）で、リスク設定の取得可否に
   連動せず**独立ロード/縮退**する。追加は理由必須（1 段）。削除は破壊的なため**明示確認**（削除理由必須＋確認ボタン）を要求
   （fail-safe）。市場は数値 enum を写像。監視銘柄の変更履歴（`MonitorSettingsChangeEntry[]`・changeType=追加/削除）を別表で一覧。

## データ取得・更新（BFF `/bff/*` 経由・`apiFetch`）

| 操作 | 呼び出し | 応答/エラー |
| --- | --- | --- |
| 初期表示 | `GET /risk-controls/settings` | `RiskManagementSettings`。404/失敗=縮退表示 |
| 履歴 | `GET /risk-controls/settings/history` | `SettingsChangeEntry[]`。失敗時は履歴領域のみ縮退 |
| 上限保存 | `PUT /risk-controls/settings/limits`（`{limits, reason}`。`limits` は **`*Ratio` キー＝比率**。画面の % を送信時に比率へ変換する・#362/IADR-0151） | 成功=再取得。**400=値域違反（`{error, details}`。サーバの `RiskLimitBounds` が実効させる）または検証**、409=競合（DbUpdateConcurrency）＋再取得を促す |
| 発注先の変更 | `PUT /risk-controls/settings/broker-provider`（`{provider, reason, acknowledgedLiveTrading, acknowledgement}`） | 成功=再取得＋段階ゲート飛ばしの警告。**実弾は同意と「REAL」の入力の両方が無ければ 400**（サーバ側も同じ関門・IADR-0141）。理由が空も 400 |
| equity・統制値の実額（モーダル③） | `GET /risk-controls/status` | `RiskStatusView`（`capital` / `maxOrderAmount` / `maxDailyOrderAmount` / `maxOpenPositions`）。失敗時は**実弾への切替を許さない** |
| ガード保存 | `PUT /risk-controls/settings/guard`（`{enabledProductTypes, enabledMarkets, bannedSymbols, preventSameDayReentry, prohibitManipulativeOrderPatterns, reason}`・全置換） | 成功=再取得。危険な緩和は確認必須。400=検証、409=競合＋再取得を促す（#188/IADR-0086） |
| 監視銘柄 一覧 | `GET /monitor/watchlist`（別サービス MarketMonitor・OwnerOrService） | `MonitoredSymbol[]`。404/失敗=独立縮退（「監視銘柄設定は利用できません。」） |
| 監視銘柄 履歴 | `GET /monitor/watchlist/history` | `MonitorSettingsChangeEntry[]`。失敗時は履歴領域のみ縮退 |
| 監視銘柄 追加 | `POST /monitor/watchlist`（`{symbol, market, reason}`） | 成功=再取得。理由必須。400=重複/空/未定義 market、409=競合＋再取得を促す（#196/IADR-0090） |
| 監視銘柄 削除 | `DELETE /monitor/watchlist`（body `{symbol, market, reason}`） | 明示確認（削除理由必須）後に実行。成功=再取得。400=不在、409=競合＋再取得を促す（#196/IADR-0090） |

## 振る舞い（安全既定）

- **入力検証（#362/IADR-0151 で強化）**: 空欄・非数値・**値域外**の上限がある間は保存を無効化し、該当項目と許容範囲を
  警告表示する。黙って `0` 送信しない。**範囲検証は画面とサーバの双方が行う**——画面は即時提示のため、
  サーバ（`RiskLimitBounds`）が実効のためである（画面だけの関門は API 直叩きで消える）。
- 理由未入力時は保存不可（送信ボタン無効）。保存成功後は現在値・履歴を再取得。
- 409/400 では破壊的な自動再試行をしない。競合時は「最新を取得して再試行」を促す。
- 取得不能・権限外・BFF 未登録は安全側（縮退・存在秘匿）へ倒す。
- `changeType` 等の数値 enum は表示ラベルへ写像し、未知値はフォールバック表示。

## スコープ外（後続）

段階の直接変更 UI（段階ゲート承認へ一元化）、監視の変動閾値・収集間隔の変更 UI（`PUT /monitor/settings`・#196 対象外）、
実 BFF の `/monitor/*` プロキシ結線（MSP 側合成点・risk-controls の MSP #287 と同様に別リポ後続）。

> 監視銘柄（watchlist）変更 UI は **#196（IADR-0090）で実装済み**（上表「監視銘柄」）。計画 `05_screens/01_screens.md` は監視銘柄を
> SC-01 の運用パラメータ節に置くが、所有サービス単位に画面を分ける方針（IADR-0084）と #196 の指定に従い SC-02 に載せた（環流対象）。

> **暫定結線の解消（#209/IADR-0095・2026-07-20）**: 従来 TradeDecision の定時サイクルは監視銘柄を構成ファイル（`TradeCycle:Watchlist`）
> から読む暫定実装で、本画面（SC-02）での変更が判断対象に反映されなかった。#209 で TradeDecision は権威源（本画面と同じ
> MarketMonitor `GET /monitor/watchlist`）を **s2s 同期照会**（`OwnerOrService`）するよう恒久化され、**本画面での監視銘柄変更は
> 以後の定時サイクルの判断対象に反映される**。供給不達時は構成ベース（既定 watchlist）へ fail-safe に倒す。詳細は
> [IADR-0095](../adr/IADR-0095_watchlist-authoritative-wiring.md)。

> **リスク上限の保存の復旧（#362・2026-08-05）**: [#329](https://github.com/endazon/ai-stock-trading/issues/329) の equity 比化以降、
> 本画面からのリスク上限の保存は **400 で拒否されていた**（PUT の本文が旧名＝金額キーのままだったため）。これは
> [#389](https://github.com/endazon/ai-stock-trading/issues/389) が意図的に維持した安全側の状態である——キー名だけを合わせると
> `35000` が **equity の 35,000 倍**として保存され、統制が無効化された状態で保存が成功するためである。
> #362 で**割合（%）入力・実額併記・値域バリデーション（画面とサーバの双方）**を同時に入れ、保存を復旧した。
> **サーバ側にはそれまでリスク上限の値域検証が一切存在しなかった**（400 は値の危険ではなくキー名の不一致で起きていた）。
> 詳細は [作業仕様書 20260805_362](../specs/20260805_362_sc02-ratio-input.md)・[IADR-0151](../adr/IADR-0151_risk-limit-percent-input-and-bounds.md)。
