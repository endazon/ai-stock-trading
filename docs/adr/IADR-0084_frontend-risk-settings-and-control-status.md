---
title: IADR-0084 FR-13 リスク設定と #20 統制状態は Risk の既存 OwnerOnly 契約を消費する参照優先の別 feature とし、破壊的操作は Bot 側に委ね、数値 enum の写像はフロントに閉じる
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-13, FR-19, FR-20, UC-06, SC-02, SC-03, ADR-0003, ADR-0007, ADR-0008, ADR-0009]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0084: FR-13 リスク設定と #20 統制状態は Risk の既存 OwnerOnly 契約を消費する参照優先の別 feature とし、破壊的操作は Bot 側に委ね、数値 enum の写像はフロントに閉じる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-13**（利用者が設定を変更できる）、**FR-10/FR-19/FR-20**（リスク統制・相場操縦ガード・段階ゲート）、
  **UC-06**（設定変更・一時停止・緊急停止。SC-03 の統制状態参照は本 UC で変更する統制の**閲覧面**として位置づける）、
  ADR-0003（AI 判断のガードレール。FR-10 のリスク上限の根拠）、ADR-0007（取引ガード・信用取引。FR-19 の根拠）、
  ADR-0008（段階ゲート・バックテスト）、ADR-0009（取引統制の優先順位・既存慣行 ID）
- **計画の未定義・未検証参照（計画環流対象）**:
  - 計画リポジトリ `05_screens/` は空で SC-02/SC-03 は未定義（素案）。
  - 「稼働状態の確認」に対応する UC は計画に未定義。計画 **UC-07 は「取引履歴・判断根拠の参照」**（RAG・別概念）であり、
    当初 SC-03 の起点に誤って UC-07 を挙げていたのを **UC-06 の閲覧面**へ是正した。稼働状態確認 UC の新設を計画へ提案する。
  - 計画 **ADR-0009** は既存バックエンド（`RiskStatusService`/`RiskControlEndpoints`）および `IADR-0075` が「取引統制の優先順位」
    の根拠として参照する既存慣行 ID だが、pin 済み planning submodule の `07_adr/` には ADR-0001〜0008 しか存在せず ADR-0009 の
    ファイルは未在（plan_refs には含めない）。ADR-0009 の実在・採番確認を計画へ提案する。
  - 上記は `/plan-feedback` で **planning#33**（#31 後続）へ環流済み（SC-02/SC-03 の採番確定・UC-07 誤参照の是正・
    ADR-0009 の実在確認を提案）。
- 対象 Issue: [#106](https://github.com/endazon/ai-stock-trading/issues/106)（T1 残スライス 1b/1c）
- 関連する実装仕様書: [作業仕様](../specs/20260718_106_frontend-risk-settings-and-controls.md)、
  画面 [SC-02 リスク設定](../screens/20260718_SC-02_risk-settings.md)・[SC-03 統制状態参照](../screens/20260718_SC-03_control-status.md)
- 前段: [IADR-0080](IADR-0080_frontend-settings-screen.md)（frontend 新設・単独リポの @foundation スタブ＋vitest・SC-01 FR-17）
- 前提（develop マージ済み）: #20 段階ゲート（IADR-0070）・#152 pause（IADR-0075）・Risk `/risk-controls/*`（OwnerOnly）

## 背景・課題

[IADR-0080](IADR-0080_frontend-settings-screen.md) は frontend を新設し SC-01（FR-17 全体前提条件の閲覧/変更）に限定した。
T1 残スライスは **1b: FR-13 個別設定**と **1c: #20 承認・統制状態参照**である。SC-01 の画面仕様書は「FR-13 は同画面へ節追加」
と記していたが、着手時に次が判明した:

1. **データ源が別サービス**: FR-17（全体前提条件）は ConfigurationService `/assumptions`。一方 FR-13 の「閾値・リスク上限」と
   #20 の統制/段階は **RiskManagementService `/risk-controls/*`**（OwnerOnly）。取得・保存・履歴の経路と失敗縮退が SC-01 と独立。
2. **監視銘柄（watchlist）にエンドポイントが無い**: FR-13 は監視銘柄も含むが、設定ストア側に変更 API が未整備。
3. **破壊的操作の二重化リスク**: pause/resume・kill switch・段階遷移承認は #165 の Discord Bot が既に担う（IADR-0081）。
   画面にも同じ操作を置くと統制の二重実装・責務分散になる。
4. **enum の HTTP 表現**: Risk Worker は HTTP 応答に `JsonStringEnumConverter` を設定していないため、`activeControl`・`stage`・
   段階遷移 `kind`・`changeType` 等は**数値**で届く。

## 決定

### 1. FR-13 リスク設定と #20 統制状態は SC-01 とは別の feature/route にする（SC-01 の「同画面」注記から逸脱）

`sc01-settings`（FR-17・ConfigurationService）に対し、本スライスは 2 つの feature を新設する:

- `sc02-risk-settings`（route `settings/risk`・nav「リスク設定」）: FR-13/FR-19/FR-20。RiskManagementService `/risk-controls/settings`。
- `sc03-controls`（route `controls`・nav「統制状態」）: FR-10/FR-20/UC-06。`/risk-controls/status` ＋ `/risk-controls/stage-gate`。

根拠: 消費するサービス・認可・失敗縮退が SC-01 と独立で、`FeatureModule`（1 画面 = 1 モジュール）の粒度に沿う。単一の巨大
コンポーネントに畳むより、所有サービス単位で分離した方がテスト・保守・存在秘匿の出し分けが明快。SC-01 画面仕様書の「同画面へ
節追加」注記からは逸脱するため、計画側 `05_screens/` の SC-02/SC-03 定義を `/plan-feedback`（planning#31）で提案する。

### 2. 参照優先。破壊的操作は #165 の Bot 側に委ねる（安全既定・責務非分散）

`sc03-controls` は **参照専用**（statusと段階ゲートの閲覧のみ）。pause/resume・kill switch・段階遷移承認のボタンは置かない。
これらは #165（IADR-0081）の Discord Bot が OwnerOnly エンドポイントを駆動する。`sc02-risk-settings` は FR-13 の中核である
**リスク上限（`limits`）の変更のみ**を許し、ガード（`guard`）・段階（`stage`）は参照表示に留める（段階変更は段階ゲート承認フロー
＝Bot 側と重複するため、直接 `PUT /settings/stage` は本スライスで開かない）。根拠: 統制操作の入口を一元化し、UI からの誤操作面を
最小化する（fail-safe）。

### 3. 単独リポの自己完結・依存規則は IADR-0080 を踏襲する（新しい foundation サーフェスを増やさない）

feature は `@foundation/*`（`apiFetch`/`ApiError`/`RequireRole`/`FeatureModule`）と既存の `TradingRole` のみを使う。テスト/型検査は
`test/foundation-stub` ＋ローカル vitest エイリアスで platform 非依存に完結させる（[IADR-0080](IADR-0080_frontend-settings-screen.md)
決定 2）。BFF は `vi.mock('@foundation/api/apiClient')` でモックし実疎通に依存しない。新規の foundation スタブは追加不要
（既存サーフェスで足りる）。

### 4. 数値 enum は表示ラベルへ写像し、未知値はフォールバック表示にする（フロントに閉じる・fail-safe）

`activeControl`（0=なし/1=緊急停止/2=日次損失ロックアウト/3=一時停止）、`stage`（0..3）、段階遷移 `kind`（0=昇格/1=差し戻し）、
`changeType`（設定変更種別）等の数値を、フロント側の写像テーブルで日本語ラベルへ変換する。テーブルに無い値は「不明(N)」等の
安全側フォールバックで表示し、画面を壊さない。根拠: バックエンドの JSON 表現（数値 enum）を変えずに、表示の堅牢性をフロントに閉じて担保する。

## 影響・トレードオフ

- **利点**: SC-01 と疎結合な 2 画面を最小サーフェスで追加。破壊的操作を Bot に一元化し UI 誤操作面を排除。数値 enum の変化に
  フォールバックで頑健。バックエンド・foundation とも無改修。
- **代償**: nav が「設定/リスク設定/統制状態」に増える。enum 写像テーブルはバックエンド enum 追加時に追随が要る（未知値は安全側に
  倒れるため壊れはしない）。SC-01 画面仕様の「同画面」注記から逸脱するため計画環流が要る。
- **却下案**: (a) FR-13 を SC-01 の同一コンポーネントへ畳む → 別サービス・別失敗縮退で肥大化のため却下。(b) 統制操作ボタンを
  画面に置く → #165 と二重化し統制入口が分散するため却下（参照に留める）。(c) 監視銘柄設定を含める → 設定ストア側 API 未整備の
  ため後続へ分離。

## 検証

`cd frontend && npm ci && npm run typecheck && npm run lint && npm run test`（Vitest）が緑。実ブラウザ E2E（Playwright・1d）と
実 BFF/Keycloak 疎通は実基盤依存として後続へ分離（フォローアップ issue・優先度ラベル付き）。CI は既存 `frontend` ジョブで実行。
