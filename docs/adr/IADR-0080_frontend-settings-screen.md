---
title: IADR-0080 フロントエンドは platform unit-template 規約に準拠し、単独リポの型検査/テストを @foundation スタブ＋ローカル vitest で自己完結させ、設定画面は FR-17 前提条件の閲覧/変更に限定する
type: impl-adr
status: Accepted
related_ids: [FR-13, FR-17, UC-06, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0080: フロントエンドは platform unit-template 規約に準拠し、単独リポの型検査/テストを @foundation スタブ＋ローカル vitest で自己完結させ、設定画面は FR-17 前提条件の閲覧/変更に限定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-13**（監視銘柄・閾値・リスク上限などの設定を利用者が変更できる）、**FR-17**（税・手数料・為替・
  計算方針などの全体前提条件をバージョン管理し、変更は利用者のみ）、**UC-06**（設定変更・一時停止・緊急停止。専用「設定画面」が
  明記された唯一のユースケース）、**ADR-0001**（platform 再利用・`backend/`＋必要に応じ `frontend/`・合成点経由で組み込む）、
  platform ADR-0004（Keycloak 認証）・FR-17（変更は利用者のみ）
- 対象 Issue: [#106](https://github.com/endazon/ai-stock-trading/issues/106)
- 関連する実装仕様書: [20260718_frontend-settings-screen](../specs/20260718_frontend-settings-screen.md)、
  画面仕様 [SC-01 設定画面](../screens/20260718_SC-01_settings.md)
- 前提（develop マージ済み）: #22（introspection/pipeline 宣言）・#19（ConfigurationService `/assumptions` API・IADR-0021/0063）
- 計画環流: 計画リポジトリ `05_screens/` は空（SC 未定義）。設定画面 **SC-01** の定義を `/plan-feedback` で提案する
  （`draft/feedback/20260718_ai-stock-trading-sc01-settings-screen.md`）。本実装は SC-01（環流中）を参照する。

## 背景・課題

本ユニットには `frontend/` が存在せず、フロントエンド実装が皆無だった。計画（ADR-0001・01_architecture-overview）は
「画面 features を持つ場合のみ `frontend/`（`package.json` ＋ `src/features/`。npm workspaces `*/frontend` で自動認識）を置き、
platform SPA の features 合成点・BFF 合成点経由で組み込む」と定める。一方、計画の `05_screens/` は空で SC-xx が未定義。

課題は 3 点:

1. **単独ビルド/テストの成立**: 本ユニットは platform へ submodule 配置された時に `src/<unit>/frontend` として実 `@foundation`
   （`../../platform/frontend/src/foundation`）が解決される。しかし**単独リポ（本リポ）には platform が存在しない**ため、
   `@foundation/*` を素直に import すると型検査・テストが解決不能になる。ユニット単独 CI を platform 非依存で緑にする必要がある。
2. **画面の起点 SC が未定義**: 実装は SC 起点（CLAUDE.md）だが SC が無い。
3. **スコープの肥大**: 設定画面は FR-13（監視銘柄/閾値/リスク上限）＋FR-17（全体前提条件）＋#20 承認・統制状態参照まで含み得る。

## 決定

### 1. platform unit-template 規約に厳密準拠する（独自スタックを持ち込まない）

`templates/unit-template/frontend` および既存 `knowledge/frontend` の確立済み規約に合わせる:
React 18 + TypeScript + `react-router-dom` + `oidc-client-ts`、テストは Vitest + Testing Library、E2E は Playwright。
feature は `FeatureModule`（`@foundation/routing/featureRegistry`）を 1 つ公開し `src/features/index.ts` に登録する。
BFF アクセスは `apiFetch`（`@foundation/api/apiClient`・`/bff/*`）経由のみ。ロールの表示制御は `RequireRole`
（実効認可はサーバ側の 403/404 に置く。存在秘匿は IADR-0009 と整合）。

### 2. 単独リポの型検査/テストは「@foundation スタブ＋ローカル vitest エイリアス」で自己完結させる（fail-safe・非侵襲）

- `frontend/test/foundation-stub/` に**テスト/型検査専用**の最小 `@foundation` サーフェス（`FeatureModule` 型、`apiFetch`/`ApiError`、
  `RequireRole`、`roles` の `useHasAnyRole` 等）を置く。**本番 feature コードは `@foundation/*` を import したまま**にし、
  スタブは `frontend/tsconfig.json` の `paths` と `frontend/vitest.config.ts` の `resolve.alias` からのみ参照される。
- platform へ合成された時は、platform の `src/vitest.config.ts`・`vite.config.ts` が `@foundation` を**実 foundation**へ解決し、
  同じテストが実 foundation 上で走る（スタブは platform ビルドの対象外＝`test/` 配下）。
- テストは platform の作法どおり `vi.mock('@foundation/api/apiClient')` で BFF をモックする（実 BFF 疎通に依存しない）。
- 根拠: ユニットを単独で検証可能にしつつ、合成時は実 foundation を使う二重性を、**実装コードを一切分岐させずに**
  ビルドツールのエイリアス層だけで吸収する。スタブは「テストダブル」であり本番経路に載らない（フェイルセーフ）。

### 3. 第1スライスは FR-17（全体前提条件）の閲覧/変更に限定する

バックエンド契約が既に存在する FR-17（`GET /assumptions`＝OwnerOrService、`GET /assumptions/history`・`PUT /assumptions`＝
OwnerOnly。IADR-0021/0063）のみを結線する。画面は `trading-owner` ロールに限定（`RequireRole` で存在秘匿）、変更は
**楽観排他（ExpectedVersion）＋理由必須**、検証失敗（400）・競合（409）はメッセージ表示し**破壊的な自動再試行はしない**。
FR-13（監視銘柄/閾値/リスク上限＝設定ストア側エンドポイント未整備）・#20 承認 UI・統制状態参照（SC 未定義）・
Playwright E2E＋platform 合成点への登録（platform リポ側変更）は**後続スライスへ分離**する（`Refs #106`）。

### 4. owner ロールはユニット固有定数として持つ（foundation を改修しない）

`@foundation/auth/roles` の `PlatformRole` は platform 管理者/運用者向け。本ユニットの利用者ロールは既存バックエンド準拠で
`trading-owner`。`RequireRole` は `anyOf: string[]` を受けるため、ユニット側に `TradingRole = { Owner: 'trading-owner' }`
定数を置いて渡す（foundation 無改修）。

## 影響・トレードオフ

- **利点**: 単独 CI が platform 非依存で緑になり、合成時は実 foundation で同一テストが走る。本番コードに `#if TEST` 相当の分岐が無い。
- **代償**: `@foundation` スタブの維持が要る。ただしスタブは feature が実際に使う極小サーフェスに限り、foundation の変更に追随する
  範囲は狭い。スタブと実 foundation の乖離は、合成時 CI（platform）が最終的に検出する。
- **却下案**: (a) platform を submodule/依存として単独リポへ取り込む → 一方向依存規則（IADR-0057）と重量の点で却下。
  (b) feature を `@foundation` に依存させず自前 API 層を持つ → 合成時に二重実装となり規約逸脱のため却下。

## 検証

`cd frontend && npm ci && npm run typecheck && npm run lint && npm run test`（Vitest）が緑。実ブラウザ E2E（Playwright）と
実 BFF/Keycloak 疎通は実基盤依存として後続へ分離（本 IADR の決定 3）。CI は `ci.yml` に `frontend` ジョブを追加（既存ジョブは不変）。
