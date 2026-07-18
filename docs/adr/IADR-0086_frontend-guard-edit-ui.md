---
title: IADR-0086 SC-02 のガード変更 UI は既存 PUT /settings/guard を全置換で消費し、危険な緩和は明示確認で fail-safe にする（監視銘柄はバックエンド未整備で分離・段階直接変更は開かない）
type: impl-adr
status: Accepted
related_ids: [FR-13, FR-19, UC-06, SC-02, ADR-0007, IADR-0080, IADR-0084]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# IADR-0086: SC-02 のガード変更 UI は既存 `PUT /settings/guard` を全置換で消費し、危険な緩和は明示確認で fail-safe にする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-13**（利用者が設定を変更できる）、**FR-19**（取引ガード・相場操縦禁止）、**UC-06**（設定変更）、
  ADR-0007（取引ガード・信用取引）
- 対象 Issue: [#188](https://github.com/endazon/ai-stock-trading/issues/188)（FR-13 残 UI・`Refs #106`）
- 関連する実装仕様書: [作業仕様](../specs/20260718_188_frontend-guard-edit-ui.md)、画面 [SC-02 リスク設定](../screens/20260718_SC-02_risk-settings.md)
- 前段: [IADR-0084](IADR-0084_frontend-risk-settings-and-control-status.md)（SC-02/SC-03 新設・リスク上限変更のみ・ガード/段階は参照）、
  [IADR-0080](IADR-0080_frontend-settings-screen.md)（frontend 新設・単独リポの @foundation スタブ＋vitest）
- 前提（develop マージ済み）: Risk `PUT /risk-controls/settings/guard`（OwnerOnly・[RiskControlEndpoints.cs](../../backend/Services/RiskManagementService/src/RiskManagementService.Worker/Foundation/Endpoints/RiskControlEndpoints.cs)）
- **採番について（IADR-0085 の欠番は意図的）**: 本 IADR は **0086** を用いる。**0085 は並行作業の #189 に先着で割り当て済み**（ユーザー調整による）で、
  本ブランチで 0085 を使うと #189 と衝突する。番号衝突の扱いは「先着尊重」（`iadr-number-collision-playbook`）に従い、当面 0085 を空けて 0086 とする。
  develop への #189 マージ時に 0085 が埋まり欠番は解消する（履歴不変・番号の再割り当てはしない）。

## 背景・課題

[IADR-0084](IADR-0084_frontend-risk-settings-and-control-status.md) 決定 2 は SC-02 で FR-13 の中核（リスク上限）だけを変更可能にし、
ガード・段階を参照表示へ留めた。#188 はその残スコープのうち **ガード変更 UI** を実装する。着手時の制約:

1. **ガード変更 API は既存**: `PUT /risk-controls/settings/guard`（`GuardUpdateRequest`）が既に存在し、認可は OwnerOnly。
   本作業はバックエンド無改修で消費するだけでよい（監視銘柄は API 未整備）。
2. **PUT は全置換**: エンドポイントは受領した集合・配列で `TradingGuardSettings` を差し替える（差分 PATCH ではない）。
3. **ガードの緩和は取引安全性を直接下げる**: 相場操縦パターン禁止の解除・同日再エントリー禁止の解除・禁止銘柄の削除・信用取引の
   有効化は、いずれも「守り」を外す方向の変更で、誤操作の被害が大きい。
4. **enum は数値表現**: Risk Worker は HTTP 応答に `JsonStringEnumConverter` を設定していないため、`ProductType`・`Market` は
   数値で往復する（IADR-0084 決定 4 と同じ前提）。

## 決定

### 1. 既存の `sc02-risk-settings` feature を再利用し、参照専用 `GuardView` を編集フォーム `GuardForm` へ置換する

新しい feature/route/nav は増やさない。SC-02 の 1 画面に「リスク上限の変更」フォームと並べて「取引ガードの変更」フォームを置く。
段階（`StageView`）は参照のまま（段階変更は段階ゲート承認フロー＝#20/#165 Bot に一元化する IADR-0084 の方針を維持し、UI から
`PUT /settings/stage` は開かない）。根拠: 所有サービス（RiskManagementService）が同一で、取得・保存・履歴・失敗縮退の経路を共有
できる。画面粒度（1 画面 = 1 feature）を保つ。

### 2. `PUT /settings/guard` を全置換で消費する。理由必須・400/409 は #186 の作法を踏襲する

現在値をフォーム初期値へ読み込み、編集後の全体（商品種別集合・市場集合・禁止銘柄配列・2 トグル）を `reason` とともに送る。
検証(400)・競合(409)・権限(403) は既存 `saveMessageOf` でメッセージへ写像し、**破壊的な自動再試行はしない**（安全既定）。
成功後は現在値・履歴を再取得して最新化する。商品種別・市場が空集合でも黙って送らず、実効な範囲検証はサーバ 400 が担う
（クライアントは「理由未入力」「危険確認未 ON」の明白な無効のみ抑止する・#186 と同方針）。禁止銘柄の追加は FR-19（禁止根拠の記録）
に沿い、銘柄コードと理由の双方が入るまで許可しない。フォームの再初期化はガード内容の**値シグネチャ**に依存させ、隣接するリスク上限
フォームの保存で `current` が再生成されてもガードの内容が同一なら初期化しない（編集中のガード編集を黙って破棄しない・fail-safe）。

### 3. 危険な緩和は「明示確認」を必須にする（fail-safe）

現在値と送信予定を比較し、次を「危険な緩和」と判定する:

- いずれかのトグル（相場操縦パターン禁止・同日再エントリー禁止）を**有効→無効**にする
- 禁止銘柄を**削除**する（登録済みを外す）
- 信用（Margin）を**新規に有効化**する

1 件以上該当する場合、専用の確認チェックボックスを ON にするまで保存ボタンを無効化し、確認文へ該当項目を列挙する。逆方向
（ガードを厳しくする＝禁止銘柄追加・トグル有効化・信用無効化）は確認不要。根拠: 統制を緩める変更にだけ二段の意思確認を課し、
誤操作面を最小化する（#188 スコープの「破壊的/危険な設定は安全側の確認」）。厳格化はいつでも無摩擦で行えるべきなので非対称にする。

### 4. 数値 enum ↔ 選択肢の写像はフロントに閉じ、未知値は安全側フォールバックにする（IADR-0084 決定 4 踏襲）

商品種別（0=現物/1=信用）・市場（0=日本/1=米国）の選択肢は既存 `productTypeLabel`/`marketLabel` を再利用する。表示は既存写像で
「不明(N)」へ安全側フォールバックする。編集チェックボックスは既知の enum 値（`contracts` の写像テーブルのキー）を選択肢として列挙し、
未知値がサーバから届いても（現在値としては表示しつつ）画面を壊さない。バックエンドの JSON 表現は変えない。

## 影響・トレードオフ

- **利点**: バックエンド・foundation とも無改修で FR-13 のガード変更を充足。危険な緩和にだけ確認を課し誤操作面を最小化。既存 feature
  へ最小追加で画面粒度を保つ。数値 enum の変化にフォールバックで頑健。
- **代償**: SC-02 のコンポーネントが上限フォーム＋ガードフォームで肥大化する（ただし 1 サービス 1 画面の範囲内）。危険判定は
  「現在値との差分」に依存するため、現在値取得前は保存不可（読み込み中は既存どおり抑止）。全置換 PUT のため、他クライアントが並行更新
  すると 409 になり得る（楽観排他は EF 側・#186 と同じ・再取得を促す）。
- **却下案**: (a) 監視銘柄設定を含める → 設定ストア側 API 未整備のため後続へ分離（バックエンド起票を先行）。(b) 段階変更ボタンを置く
  → #20/#165 と二重化し統制入口が分散するため却下（参照のまま）。(c) 危険確認を全変更に課す → 厳格化まで摩擦を増やすため、緩和方向
  にのみ非対称に課す。

## 検証

`cd frontend && npm ci && npm run typecheck && npm run lint && npm run test`（Vitest）が緑。実ブラウザ E2E（Playwright）・実 BFF/Keycloak
疎通は実基盤依存として後続へ分離（フォローアップ issue・優先度ラベル付き）。CI は既存 `frontend` ジョブで実行（`ci.yml` は追加のみ）。
