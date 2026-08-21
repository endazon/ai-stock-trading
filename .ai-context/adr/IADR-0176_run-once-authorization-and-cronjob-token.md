---
title: IADR-0176 run-once は OwnerOrService で守り、CronJob 側に client_credentials を足す。無認証の経路は許可リストで構造固定する
type: impl-adr
status: Accepted
related_ids: [NFR, FR-02, UC-01, ADR-0004, IADR-0011, IADR-0023, IADR-0051, IADR-0164, IADR-0175]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
---

# IADR-0176: run-once の認可と CronJob のトークン取得

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-07
- 決定者: 実装（Claude Code）／ 起点 [#456](https://github.com/endazon/ai-stock-trading/issues/456)
- 作業仕様書: [20260807_456_run-once-authorization](../specs/20260807_456_run-once-authorization.md)

## コンテキストと課題

`POST /internal/collection/run-once`（#121 / [IADR-0023](./IADR-0023_trading-cycle-scheduling-and-merge.md)）が**無認証で公開されていた**。`InformationCollectionService.Api` は `AddAiStockTradingAuth` を呼んでおらず、認可の層が 1 つも無い。

**発覚は [#450](https://github.com/endazon/ai-stock-trading/issues/450)（セキュリティ仕様書を実測で記入する）である。** 機能の不具合ではなく**横断調査**で見つかった —— つまり**誰も踏んでいないし、誰も気付かなかった**。

**踏まれると収集サイクルを任意に起動できる。** 発注はしない（判断は下流の統制を通る）が、**LLM 費用の消費と外部 API のレート制限の枯渇**が起こる。

### 経緯 —— **エンドポイントを足したときに認可が付かなかった**

本サービスは長らく `/health/*` と `/internal/introspection` しか持たず、**認証を登録しない前提が正しかった**。#121 で run-once を足したときに、**その前提が崩れたことに気付かないまま**エンドポイントだけが増えた。

**「認証を登録していないサービス」に業務エンドポイントを足すのは、既存コードのどこにも赤を出さない。**

### 🔴 着手前の実測で分かったこと —— **`RequireAuthorization` を足すだけでは壊れる**

`deploy/helm/ai-stock-trading/templates/cronjob.yaml` の呼び出しは**素の `curl`** である。

```yaml
image: curlimages/curl:8.11.1
args: ["-fsS", "-m", "20", "-X", "POST", "http://information-collection-service:8080/internal/collection/run-once"]
```

**トークンを持たない。** 認可だけ足すと、`tradingCycle.cronjob.enabled=true` にした瞬間に毎回 401 で落ちる。

既定は `false` のため**今は誰も踏んでいない**が、**「今は無効だから壊れない」は理由にならない** —— 無効な機能が壊れていることは、**有効化するまで誰も気付かない**。[IADR-0166](./IADR-0166_plan-source-digest.md) が名付けた「緑だが検査されていない」と同じ形である。

## 決定

### 決定1: **`OwnerOrService` で守る**（`OwnerOnly` にしない）

[IADR-0051](./IADR-0051_service-to-service-auth.md) は `OwnerOrService` を**読み取り系の同期照会**に限り、**書き込み系は `OwnerOnly` 据え置き＝サービスへ書き込み権限を与えない**と定めている。**run-once は照会ではない**ため、字面では `OwnerOnly` 側である。

**しかし `OwnerOnly` にすると CronJob（無人サービス）が呼べない。** `OwnerOnly` は「**人間の明示的判断を要求する**」ための枠であり（kill switch・リスク設定変更・段階昇格）、**定時起動に人間を要求すると運用が成立しない**。

**そこで「書き込みか否か」ではなく「何が起きるか」で測る。**

| 問い | run-once |
| --- | --- |
| 発注できるか | **できない**。収集サイクルを 1 巡起こすだけで、発注は下流の統制（費用停止・市場カレンダー・リスク評価・ブローカ階層の閂）を通る |
| 統制を緩められるか | **できない**。設定は一切変えない |
| 何が起こせるか | **LLM 費用の消費と外部 API のレート制限の消費** |

**`trading-service` に渡すのは「サイクルを起こす権限」であって「発注する権限」ではない。** これは IADR-0051 の原則に対する**意図した例外**であり、**字面の分類（書き込み）ではなく到達できる結果で線を引いた**ことを記録する。

> **将来この判断が危うくなる条件**: run-once が**設定を変える**・**統制を迂回する**・**発注を直接起こす**ようになったら、`OwnerOnly` へ移す。**「収集サイクルを起こすだけ」であることが本決定の前提である。**

### 決定2: **CronJob は 2 段（token → run-once）にし、fail-closed にする**

`client_credentials` でトークンを取得してから `Bearer` を付けて叩く。資格情報は `ast-secrets` の `service-auth-client-id` / `service-auth-client-secret`（**他サービスの s2s と同じもの**）。

**`curlimages/curl` に `jq` は無い**ため `access_token` は `sed` で抜く。**抜けなければ空文字列になり、直後の判定で落ちる。**

**fail-closed を実測で確かめた**（シェルを実際に走らせた）。

| 状況 | 結果 |
| --- | --- |
| 資格情報が未設定 | **exit 1**（トークン取得を試みる前に落ちる） |
| token エンドポイントがエラーを返す（`{"error":"invalid_client"}`） | **exit 1**（`access_token` を抜けない） |
| 正常 | **exit 0**（`Bearer` を付けて POST） |

**「黙って無認証で通る」経路は無い。** `set -eu` ＋ `curl -f` ＋ 空トークン判定の 3 つで塞いである。

#### token エンドポイントは **`global.authAuthority` から導出する**（values にリテラルを置かない）

> **⚠️ 本 ADR の初版は、リポジトリが 2 度書き留めていた教訓をそのまま踏んだ**（PR #458 のレビュー指摘で判明・是正済み）。
>
> 初版は `values.yaml` に `tokenEndpoint:` のリテラルを置いた。**`deployment.yaml`（#226 / IADR-0098）には
> まったく同じ状況——`auth: true` を持たないコンポーネントが s2s の token エンドポイントを要する——に対する
> 確立済みの型があり、そこには次のコメントが付いていた。**
>
> > `values` にリテラルを置くと `authAuthority` を `--set` で変えても追随しないため、テンプレート側で
> > `Auth__Authority` と同一ソース（`$g.authAuthority`）から導出する。
>
> **`values.yaml` にも同趣旨の警告が別途書かれていた。** つまり**同じ落とし穴が 2 箇所に明記されていたのに、
> 3 度目を作った**。「既存パターンを探す」より先に「動く形」を書いたのが原因である。
>
> **壊れ方**: `--set global.authAuthority=...` でレルムやホストを移すと、**他サービスは追随するのに CronJob だけ
> 古い Keycloak を叩き続ける**。token 取得は必ず失敗し、fail-closed により Job は赤くなる——**止まるので
> 危険側ではない**が、**「認可を掛けた」という前提は静かに空振りする**（統制が効いているのではなく、
> 呼び出しが届いていないだけの状態になる）。
>
> 現在は `printf "%s/protocol/openid-connect/token" (trimSuffix "/" $g.authAuthority)` で導出し、
> `deployment.yaml` と**同一ソース**にしてある。

### 決定3: **`ValidateAudience` / `RequireHttpsMetadata` は変えない。判断と前提を書く**

`AuthExtensions.cs` の 2 設定は**値を変えない**。

| 設定 | 現状 | 理由 |
| --- | --- | --- |
| `ValidateAudience = false` | 維持 | `aud` に何を期待するかは**基盤（microservices-platform）の Keycloak クライアント構成と揃っている必要がある**。本リポジトリからは基盤側を確認できない（`github_token` のスコープ外）。**片側だけ厳しくすると全サービスが 401 になる** |
| `RequireHttpsMetadata = false` | 維持 | 同上。クラスタ内が平文である現状（NetworkPolicy も mTLS も無い・[#24](https://github.com/endazon/ai-stock-trading/issues/24)）では、**HTTPS を要求してもメタデータ取得先が HTTPS で提供されていない** |

**#456 の指摘は「意図か見落としか判断できない」であり、それは値ではなく記述の欠落に対するものである。** よって**記述で閉じる**。

> **前提が崩れる条件**（この時点で再判断する）:
> 1. **Ingress が入り、外部から到達可能になる** —— `aud` を検証しないと、同レルムの別クライアント向けトークンが通る
> 2. **マルチテナント化する**（現状は単独利用者）
> 3. **基盤側の Keycloak クライアント構成が確認できるようになる** —— 揃えられるなら揃える

### 決定4: **無認証の経路を許可リストで構造固定する**

**振る舞いのテスト（401/403）は「今あるエンドポイント」しか守らない。** #456 が生まれた原因は「**次に足されたエンドポイント**に認可を忘れた」ことであり、**それを止められるのは構造テストだけである。**

`UnauthenticatedEndpointsNotAllowedTests` が `EndpointDataSource` からルート表を読み、**認可メタデータを持たないエンドポイントが許可リスト（`/health/live`・`/health/ready`・`/internal/introspection`）以外に無いこと**を固定する。[IADR-0164](./IADR-0164_stage1-trade-count-setting-and-monitor-parameter-relocation.md) 決定1（収集間隔の変更経路が存在しないことを構造で固定）と同じ型である。

**許可リスト自身にも対照テストを置く。** 「許可リストの経路がすべて実在する」を検査しないと、**綴りを間違えた許可リストは「違反 0 件」ではなく「母集合が空」を作る** —— **何も許していないのに何も検査していない状態**になる。

## 理由

- **決定1 は「字面の分類」ではなく「到達できる結果」で線を引いた。** 分類に従うと運用が壊れ、運用に従うと分類が壊れる場面では、**何が起こせるかを数えるのが唯一の解き方**である。
- **決定2 は塞ぐ側と呼ぶ側を同じ PR に入れた。** 片方だけ入れると「有効化したら動かない」地雷になる。**塞いだことの証拠は、塞いだ後も正しく通れることである。**
- **決定3 は「直せないから放置」ではなく「直せない理由と、直すべき時点」を書いた。** [IADR-0175](./IADR-0175_security-spec-absence-notation.md) 決定1 の「未確認には何を見れば分かるかを添える」を、設定値に適用したものである。
- **決定4 が本作業の本命である。** 他の 3 つは今の穴を塞ぐが、決定4 だけが**次の穴**を塞ぐ。

## 結果

- run-once が認可を要求する。**CronJob は同じ PR で追随済み**であり、有効化しても動く。
- **無認証のエンドポイントが増えるとテストが落ちる。**
- `docs/security/security.md` の T-2 が「🔴 未対策」から対策の記述へ変わる。**T-3（JWT の緩さ）は残る** —— 決定3 のとおり判断のみで、値は変えていない。

### 悪い影響（記録する）

- 🔴 **CronJob のトークン取得はローカルで実走できない**（Keycloak も k8s も無い）。**確かめたのはシェルの論理（sed 抽出・fail-closed の 3 経路）と YAML 構造までであり、実 Keycloak との疎通は未検証である。** `helm template --set tradingCycle.cronjob.enabled=true` は CI が回す（[IADR-0058](./IADR-0058_helm-chart-ci-gate.md)）が、**描画が通ることと動くことは別である。** 経路B の実機確認が要る。
- 🔴 **`ValidateAudience = false` は残る。** 決定3 は判断を書いただけで、**穴の性質は変わっていない**。基盤側と揃える作業は別に要る。
- **構造テスト（決定4）は本サービスにしか無い。** 他サービス（Notification / TradeDecision / OrderExecution / Backtest）が業務エンドポイントを足したときは検知できない。**現時点でそれらは `/health/*` と introspection しか持たないため守るものが無いが、同じ経緯（#121）が別サービスで起きたら同じ穴が開く。**
- **`trading-service` ロールに「サイクルを起こす権限」が付く。** 発注はできないが、**LLM 費用と外部 API レート制限の消費は起こせる**（認可が付いただけで、この性質は変わらない）。
- **許可リストは人が保守する。** インフラ用の経路を足すときに**理由つきで**追記する規律に依存しており、**機械は「許可リストに足したこと」の妥当性までは見ない**。

## 関連

- 起点 issue: [#456](https://github.com/endazon/ai-stock-trading/issues/456)（由来: [#450](https://github.com/endazon/ai-stock-trading/issues/450) / [IADR-0175](./IADR-0175_security-spec-absence-notation.md)）
- [IADR-0051](./IADR-0051_service-to-service-auth.md)（`OwnerOrService` と最小権限。**決定1 はその原則に対する意図した例外である**）
- [IADR-0011](./IADR-0011_foundation-min-port.md)（Keycloak 認証の最小移植。決定3 の 2 設定の出所）
- [IADR-0023](./IADR-0023_trading-cycle-scheduling-and-merge.md)（run-once トリガと市場カレンダー）
- [IADR-0164](./IADR-0164_stage1-trade-count-setting-and-monitor-parameter-relocation.md) 決定1（**構造テストで「経路が存在しないこと」を固定する型**）
- [IADR-0058](./IADR-0058_helm-chart-ci-gate.md)（chart の CI ゲート。`tradingCycle.cronjob.enabled=true` の描画を回す）
- セキュリティ仕様書: [security.md](../../docs/security/security.md)（T-2 / T-3）
- ネットワーク層（NetworkPolicy・mTLS・TLS 終端）: [#24](https://github.com/endazon/ai-stock-trading/issues/24)
- 作業仕様書: [20260807_456_run-once-authorization](../specs/20260807_456_run-once-authorization.md)
