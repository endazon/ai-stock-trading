---
title: 無認証の /internal/collection/run-once に認可を掛け、CronJob 側にトークン取得を足す
type: spec
status: review
related_ids: [NFR, FR-02, UC-01, ADR-0004, IADR-0011, IADR-0023, IADR-0051, IADR-0164, IADR-0175, IADR-0176]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
---

# 仕様書: run-once の認可

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: **NFR（セキュリティ）** —— 発注機能へのアクセスは本人のみ・外部公開しない
- 機能要求: **FR-02**（取引サイクル）／ユースケース **UC-01**（定時取引サイクル）—— 計画の対応表が `FR-02 → UC-01` と定めている（`02_requirements/01_requirements.md`）。**run-once は UC-01 基本フロー1「スケジューラが起動する」の実装そのもの**である
- 起点 issue: [#456](https://github.com/endazon/ai-stock-trading/issues/456)（由来: [#450](https://github.com/endazon/ai-stock-trading/issues/450) / [IADR-0175](../adr/IADR-0175_security-spec-absence-notation.md)）

## 着手前の実測（2026-08-07・`789afd4`）

**issue に自分で書いた「CronJob 側がトークンを取得できるかを先に確認すること」を実行した。結果は「取得できない」であり、本作業の形が変わった。**

### 🔴 CronJob は素の `curl` であり、トークンを持たない

`deploy/helm/ai-stock-trading/templates/cronjob.yaml:31`

```yaml
image: curlimages/curl:8.11.1
args: ["-fsS", "-m", "20", "-X", "POST", "http://information-collection-service:8080/internal/collection/run-once"]
```

**`RequireAuthorization` を足すだけでは、CronJob を有効化した瞬間に 401 で毎回落ちる。** `tradingCycle.cronjob.enabled` は既定 `false` のため**今は誰も踏んでいない**が、**塞いだ側だけ先に入れると「有効化したら動かない」地雷になる**。

> **「今は無効だから壊れない」は理由にならない。** 無効な機能が壊れていることは、有効化するまで誰も気付かない —— [IADR-0166](../adr/IADR-0166_plan-source-digest.md) が名付けた「緑だが検査されていない」と同じ形である。

### s2s の機構は既に在る

| 部品 | 所在 |
| --- | --- |
| `ServiceAuthOptions`（`ClientId` / `ClientSecret` / `TokenEndpoint` / `IsEnabled`） | `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Auth/ServiceAuthOptions.cs` |
| `ClientCredentialsTokenProvider` | 同 `Foundation/Auth/ClientCredentialsTokenProvider.cs` |
| 消費側の配線 `AddAiStockTradingServiceToken` | 同 `Foundation/Auth/ServiceAuthExtensions.cs` |
| 秘密の供給 | `ast-secrets` の `service-auth-client-id` / `service-auth-client-secret`（`values-local.yaml` に既出） |
| ロール | `trading-service`（[IADR-0051](../adr/IADR-0051_service-to-service-auth.md)。**読み取り系のみ**の最小権限） |

**足りないのは「CronJob（シェル）から同じことをする経路」だけである。**

### 🟡 `run-once` は読み取りではない

[IADR-0051](../adr/IADR-0051_service-to-service-auth.md) は `OwnerOrService` を**読み取り系の同期照会**に限り、**書き込み系は `OwnerOnly` 据え置き＝サービスへ書き込み権限を与えない**と定めている。

**`run-once` は照会ではなく「収集サイクルを起動する」操作**であり、素直に読めば `OwnerOnly` 側である。**しかし `OwnerOnly` にすると CronJob（サービス）が呼べない** —— これは本作業で決める点であり、下記「実装上の判断」1 で扱う。

### 認証が無いのは本サービスだけではない

`InformationCollectionService.Api` は `AddAiStockTradingAuth` を呼んでいない（`Program.cs`）。Notification / TradeDecision / OrderExecution も同様だが、**それらは HTTP に `/health/*` と `/internal/introspection` しか持たない**。**業務操作を HTTP で公開しているのは本サービスだけである。**

## 対象範囲

### 対象

| # | 変更 | 内容 |
| --- | --- | --- |
| 1 | `InformationCollectionService.Api/Program.cs` | `AddAiStockTradingAuth` を登録し、`run-once` に `RequireAuthorization` を掛ける |
| 2 | `deploy/helm/.../cronjob.yaml` ＋ `values.yaml` | **トークンを取得してから run-once を叩く**。資格情報は `ast-secrets` から `secretKeyRef` |
| 3 | `InformationCollectionService.Api.Tests`（構造テスト） | **認可メタデータを持たないエンドポイントが health / introspection 以外に無い**ことを固定する |
| 4 | 同（否定形テスト） | トークン無し・ロール無しで **401 / 403** になることを固定する |
| 5 | [IADR-0176](../adr/IADR-0176_run-once-authorization-and-cronjob-token.md)（新設） | 上記の判断と、`ValidateAudience` の結論を残す |
| 6 | `docs/security/security.md` | T-2 / T-3 を更新する（🔴 未対策 → 対策の記述へ） |

### 対象外（意図的にやらない）

- **NetworkPolicy / mTLS / TLS 終端** → [#24](https://github.com/endazon/ai-stock-trading/issues/24)。**本作業はアプリ側の認可で閉じる** —— ネットワーク層を待つと、待っている間ずっと開いたままになる。
- **`ValidateAudience` / `RequireHttpsMetadata` の**設定変更**。** 判断は下すが**値は変えない**（理由は下記 3）。
- **他 4 サービスへの `AddAiStockTradingAuth` 追加。** HTTP に業務操作が無く、足しても守るものが無い。**構造テスト（対象 3）は本サービスにだけ置く。**

## 実装上の判断

| # | 判断 | 内容 |
| --- | --- | --- |
| 1 | **`OwnerOrService` を使う**（`OwnerOnly` にしない） | `run-once` は書き込みだが、**呼ぶ主体が CronJob（無人サービス）である**。`OwnerOnly` は「人間の明示的判断を要求する」ための枠であり、**定時起動に人間を要求すると運用が成立しない**。**代わりに「何が起きるか」で妥当性を測る** —— run-once は**収集サイクルを 1 巡起こすだけ**で、発注は下流の統制（費用停止・市場カレンダー・リスク評価・ブローカ階層の閂）を通る。**`trading-service` に渡すのは「サイクルを起こす権限」であって「発注する権限」ではない。** この非対称を IADR に明記する |
| 2 | **CronJob は 2 段（token → run-once）にする** | `curlimages/curl` に `jq` は無いため、`access_token` は `sed` で抜く。**抜けなければ空文字列になり、`-f` の付いた POST が 401 で失敗して Job が赤くなる** —— **黙って無認証で通ることはない**（fail-closed）。資格情報が未設定なら**トークン取得の時点で失敗する** |
| 3 | **`ValidateAudience` / `RequireHttpsMetadata` は変えない。判断と前提を書く** | 両者は [IADR-0011](../adr/IADR-0011_foundation-min-port.md)（platform ADR-0004 の最小移植）由来で、**基盤（microservices-platform）の Keycloak クライアント構成と揃っている必要がある**。本リポジトリからは基盤側の `aud` を確認できない（`github_token` のスコープ外）。**片側だけ厳しくすると全サービスが 401 になる。** よって**現状維持とし、「なぜ許容できるか」と「前提が崩れる条件」を IADR とコードコメントに書く**。**「意図か見落としか判断できない」という #456 の指摘は、値ではなく記述の欠落に対するものであり、記述で閉じる** |
| 4 | **構造テストは先例に合わせる** | `CollectionIntervalNotConfigurableTests`（[IADR-0164](../adr/IADR-0164_stage1-trade-count-setting-and-monitor-parameter-relocation.md) 決定1）と**同じ `EndpointDataSource` 経路**で書く。**「今は無い」を人の記憶に委ねると、次の PR で足された瞬間に誰も止められない** |

## 受け入れ基準

- [x] `run-once` が**認可を要求する**（`OwnerOrService`）
- [x] **否定形**: トークン無し → **401**／`trading-owner`・`trading-service` いずれも持たないトークン → **403**
- [x] **構造テスト**: 認可メタデータを持たないエンドポイントが `/health/*`・`/internal/introspection` 以外に無い
- [x] **対照（肯定形）**: 検査器が空振りしていない（ルートが 1 本も取れなければ落ちる）
- [x] CronJob が**トークンを取得してから**叩き、**取得に失敗したら Job が赤くなる**（黙って無認証で通らない）
- [x] `ValidateAudience` / `RequireHttpsMetadata` の**結論と前提が崩れる条件**が IADR とコードコメントに残る
- [x] `docs/security/security.md` の **T-2 / T-3 が更新**されている
- [x] **ミューテーション**: `RequireAuthorization` を外すと構造テストと否定形テストが**赤**になる（**実測**: 変更前 25 件緑 → 削除後 6 件 Failed。振る舞い側 4・**構造側 2**）
- [x] `dotnet build` / `dotnet test` / `check-doc-links.js` / `helm lint`（**helm はローカルに導入できず**〔DL が 403〕、CI の `Lint and render chart` が `--set tradingCycle.cronjob.enabled=true` で描画するのを正とした —— **実測で緑**）

## テスト方針

**この統制は「緩む方向」に壊れても静かである** —— 認可を外しても機能は動き続け、何も赤くならない。したがって**両側で固定する**。

| 観点 | 固定する内容 |
| --- | --- |
| **否定形**（振る舞い） | 認証なし → 401／権限なし → 403 |
| **構造**（経路の不在） | **認可メタデータを持たないエンドポイントが増えたら落ちる** |

**構造テストが本命である。** 否定形テストは「今あるエンドポイント」しか守らないが、**構造テストは「次に足されるエンドポイント」を守る**。#456 が生まれた原因はまさに「エンドポイントを足したときに認可を忘れた」ことであり、**同じ再発を止められるのは後者だけである。**

**ミューテーションで効きを実測する**（変更前は緑・変更後は赤）。

## 残余リスク

1. **CronJob のトークン取得はローカルでは実走できない**（Keycloak も k8s も無い）。**`helm lint` と描画結果の目視までが本作業の検証であり、実際の疎通は経路B の実機確認が要る。** これは**本作業で閉じない**ことを明示する。
2. **`ValidateAudience=false` は残る。** 判断を書いただけで、値は変えていない。**基盤側と揃える作業は別に要る。**
3. **構造テストは本サービスにしか無い。** 他サービスが業務エンドポイントを足したときは検知できない。
4. **`trading-service` ロールに「サイクルを起こす権限」を渡す。** 発注は下流の統制を通るが、**LLM 費用の消費と外部 API のレート制限の枯渇は起こせる**（本作業でこの性質は変わらない。認可が付くだけである）。
