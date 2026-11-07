---
title: IADR-0636 サービス間の認証の身元をサービスごとの機密クライアントに分け、受け側は宛先（aud）を確かめ、口ごとに呼んでよいクライアント（azp）の一覧で限る。段は Off（既定）・Report・Enforce、移行の段だけ旧の共有クライアントを受ける
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-05, NFR-06, FR-14, ADR-0047, ADR-0080, IADR-0051, IADR-0611, IADR-0623, IADR-0626, MSP:IADR-0689]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-05・NFR-06)
---

# IADR-0636: サービスごとの機密クライアントと口ごとの呼び手の一覧

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」）・claude（方式）

## 起点・関連

- 要求 **NFR-05**（秘匿は OpenBao）・**NFR-06**（危険な操作の第 2 要素）。身元の分離の番号は計画に無いので裸の `NFR` を併記する。
- 基盤の側: realm にサービスごとの機密クライアントを足し、秘密を OpenBao の中で作る（基盤の作業仕様書 `20261107_s2s-per-service-clients`。MSP:IADR-0689 の延長）。
- 改めるもの: IADR-0611「サービスの分岐は aud / azp を見ない」（Enforce の段で見る）・IADR-0626 残る制約 4（確かめの口はどのサービスでも呼べる）。
- 作業仕様書: `.ai-context/specs/20261107_s2s-per-service-clients.md`（呼び出しの母集合）。

## コンテキストと課題

- クラウドでは ServiceAuth を持つ 7 つのサービスが 1 つの機密クライアント（`ai-stock-trading-svc`）を共有し、受け側はサービスのロールなら宛先も呼び手も見ずに通していた。
  どのサービスの資格でも、サービスに開いたすべての口（第 2 要素の確かめの口を含む）を呼べる。

## 決定

### 決定 1 — 身元はサービスごとの機密クライアント

- clientId は `ai-stock-trading-svc-<サービス>`。クラウドの 7 つ（backtest・cost-control・information-collection・market-monitor・order-execution・report・trade-decision）。
  作り・秘密の生成・写しは基盤（realm-for-cloud.js の `SERVICE_CLIENTS`・seed-secrets.sh・ESO）。
- chart: `services.<名前>.serviceAuthClient.clientId` を持つサービスごとに ExternalSecret `ast-service-auth-<名前>`（client-id は字面、client-secret は
  `externalSecrets.serviceAuthClients.remoteKey` の同じ clientId のプロパティだけ）。同じ clientId の 2 重は描画で止める。
- values-cloud の ServiceAuth の env は自分の Secret を **optional なしで**読む（無ければ Pod は起動しない。共有の資格へ黙って戻らない）。

### 決定 2 — 受け側は宛先（aud）を認証で確かめる（Enforce の段）

- `Auth:ServiceToken:Mode=Enforce` で JwtBearer の宛先の確かめを入れる（`Auth:ServiceToken:Audiences`。既定 `ai-stock-trading-api`）。合わなければ 401。
  所有者のトークンにも掛かる（所有者のクライアントは既に同じ宛先を持つ。IADR-0611）。

### 決定 3 — 口ごとに呼んでよいクライアントの一覧

- 口に名前を付ける（REST `.RequireServiceCallers(名前)`・gRPC はサービスの型に `[ServiceCallers(名前)]`。名前の定数は `ServiceCallerNames`）。
- 一覧の既定は `ServiceCallerNames.DefaultCallers`（呼び手のサービスの名前。clientId は接頭辞 `ai-stock-trading-svc-` ＋名前）。構成 `Auth:ServiceCallers:<名前>`（配列）が口ごとに置き換える。
  base appsettings.json は Auth の鍵を持てない（validate-runtime-scaffold・IADR-0048）ので、既定はコードに置く（OwnerTokenBinding の既定と同じ形）。
- 判定（要件 `ServiceCallerRequirement`。OwnerOrService・GrpcOwnerOrService・`.RequireServiceCallers` の口）: サービスのロールのトークンは、azp がちょうど 1 つで
  一覧に序数一致で在るときだけ通す。名前の無い口はサービスに開かない。所有者のトークンは関わらない（IADR-0611・0622 のまま）。
- 第 2 要素の確かめの口（IADR-0626）の一覧は報告書のクライアントだけ。

### 決定 4 — 段と移行

- 段 `Auth:ServiceToken:Mode`: **Off（既定。ローカル・CI・経路 B。従前どおり）**・Report（合わないものを記録して通す）・Enforce。chart は
  `global.serviceTokenBinding.mode`（空なら描かない）。values-cloud は Enforce。
- 移行の段 `Auth:ServiceToken:LegacyClients`（chart `global.serviceTokenBinding.legacyClients`）: 在る azp（旧の共有クライアント）は宛先を問わず、サービスに開いた口を
  通し、受けた回を Warning で残す。values-cloud は移行の段（`[ai-stock-trading-svc]`）。全サービスが自分のクライアントで動き、そのログが出ないことを確かめてから外す
  （手順書の段 3。外すのは別の変更）。
- 1 つの値（配列でない）で書いた構成・未知の段・Enforce で空の宛先は起動で止める。

## 結果

- 描画の検査 `scripts/helm-service-auth-clients.test.js`（helm.yml）が、7 サービスの別々の Secret と clientId、受け側の段、口の一覧の呼び手の実在を固定する。
- 試験: `ServiceCallerPolicyTests`（本物の JwtBearer）・`ServiceCallerAllowlistTests`（リスク管理の実際の一覧）。

## 残る制約

1. gRPC の口は型ごとの一覧（メソッドごとでない）。クラウドは gRPC を結線しない。
2. CronJob（run-once）は一覧を持たない（クラウドでは無効）。最終の段では通らない。
3. 一覧の既定はイメージの中のコード。変えるにはイメージを作り直すか env で上書きする。
4. 口を足すとき `.RequireServiceCallers` と一覧を書き忘れると、Enforce でサービスから 403 になる（閉じる向き。機械の検査は描画の検査の「一覧の呼び手が描画に在る」だけ）。
