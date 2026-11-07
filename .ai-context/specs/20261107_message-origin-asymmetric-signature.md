---
title: 統制の入力になるメッセージの発行元の印を非対称の署名（ECDSA P-256）にし、受け手は確かめる鍵（公開の鍵）だけを持つ
type: spec
status: accepted
related_ids: [NFR, NFR-05, FR-10, FR-11, ADR-0009, ADR-0080, IADR-0633, IADR-0627, IADR-0610, IADR-0623]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0080_cloud-poc-gate-in-cluster-credentials-and-pre-secret-reachability.md (決定 1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10・FR-11・NFR-05)
---

# 仕様書: 発行元の印を非対称の署名にする

## 起点

- 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」）。クラウド移行の前の堅牢化（非機能 `NFR`）。
- 置き換える決定: [IADR-0627](../adr/IADR-0627_message-origin-signing-for-control-inputs.md)（発行元ごとの共有の鍵の HMAC）。新しい決定は [IADR-0633](../adr/IADR-0633_message-origin-asymmetric-signature.md)。
- 目的: **発行元は秘密の鍵で署名し、受け手は確かめる鍵（公開の鍵）だけを持つ。** 受け手の Pod の中身が漏れても、その Pod は他のサービスを発行元として名乗れない。

## 1. 母集合（規則 9: 誤りの側の文字列で全文書を走査した）

`git grep -i -E 'messageOrigin|message-origin'`（develop `08634c6ab`）の当たり 37 ファイルから CHANGELOG を除いたもの。直す先:

| 区分 | ファイル | 直すこと |
| --- | --- | --- |
| コード | `PlatformShim/Foundation/Messaging/MessageOrigin{Catalog,Options,Signature,Verifier,Wiring}.cs` | 署名を ECDSA P-256 に。旧 HMAC は `MessageOriginLegacyHmac.cs` へ分け、段 1 でだけ受ける |
| 試験 | `PlatformShim.Tests/MessageOrigin{Verification,Wiring,Chart,CatalogCompleteness}Tests.cs` | 鍵の形・往復・偽装・改竄・再送・公開の鍵では署名できない・旧い印の段ごとの扱い |
| chart | `values.yaml`・`values-cloud.yaml`・`templates/deployment.yaml`・`templates/external-secrets.yaml`・`README.md` | `signers` / `verifiers` / 2 つの Secret / 旧い鍵の段 1 だけの opt-in |
| CI | `.github/workflows/helm.yml` | env の名前・件数（監査を足して 9）・optional の検査 |
| 運用 | `docs/operations/cloud-single-node-migration-runbook.md` §5.1・§5.4 | 鍵の組を作るスクリプトの手順 |
| 監視 | `deploy/observability/README.md`・`alerts/ai-stock-trading-alerts.yaml` | 結果 `legacy` と監査の記録だけの確かめ |
| スクリプト | `scripts/ast-message-origin-keygen.sh`（新）＋試験・`scripts/scripts.repo.test.js` | 鍵の組の生成と OpenBao への投入（値を表示しない） |

除外: `Program.cs`（11 サービス）は呼び出しの形（`UseAiStockTradingMessageOrigin(ServiceName, builder.Configuration)`）を変えないので触らない。`BusinessMetrics` は結果の名前を文字列で受けるので触らない。確定済みの作業仕様書（`20261107_message-origin-signing-for-control-inputs.md`）は凍結記録なので書き換えない。

## 2. 鍵の形と置き場

| 鍵 | 形 | 持つ者 | OpenBao | Secret | env |
| --- | --- | --- | --- | --- | --- |
| 署名の鍵（秘密） | PKCS#8 DER の base64（P-256） | 発行元 7 サービスが自分の分だけ | `cloud/ast/ast-message-origin-signing-keys`（property ＝ 発行元） | `ast-message-origin-signing-keys` | `MessageOrigin__SigningKey` |
| 確かめる鍵（公開） | SubjectPublicKeyInfo DER の base64（P-256） | 受け手が確かめる発行元の分だけ | `cloud/ast/ast-message-origin-verify-keys` | `ast-message-origin-verify-keys` | `MessageOrigin__VerifyKeys__<発行元>` |
| 前の公開の鍵（入れ替えの間だけ） | 同上 | 受け手のうち、`previousVerify` に載った発行元を確かめるもの | `cloud/ast/ast-message-origin-previous-verify-keys`（`--rotate` が残す） | `ast-message-origin-previous-verify-keys` | `MessageOrigin__PreviousVerifyKeys__<発行元>`（optional） |
| 旧い共有の鍵（HMAC） | 32 バイト以上の base64 | 段 1 で `legacyHmac.enabled` のときだけ | `cloud/ast/ast-message-origin-keys`（既存） | `ast-message-origin-keys` | `MessageOrigin__Keys__<発行元>` |

受け手の確かめる発行元（`MessageOriginCatalog.VerifiedBy` から計算。chart の `verifiers` と試験で突き合わせる）:

| 受け手 | 確かめる発行元 |
| --- | --- |
| trade-decision | information-collection, market-monitor |
| order-execution | market-monitor, risk-management |
| market-monitor | trade-decision |
| risk-management | backtest, information-collection, order-execution, risk-management, trade-decision |
| cost-control | report, trade-decision |
| audit（記録だけ。§4） | 7 つすべて |

## 3. 印と段

- ヘッダー: 新しい署名は `ast-origin-sig2`（`v2.<base64(ECDSA P-256 / SHA-256 の IEEE P1363 形式 64 バイト)>`）。旧い HMAC は従来の `ast-origin-sig`（`v1.`）。発行元・ID・時刻のヘッダーは共通。
- 正準形は従来と同じ項目（型の完全名・ID・発行元・時刻・本文の正規形）で、先頭の版だけ `v2` にする（版をまたいだ流用を防ぐ）。
- 受け手の判定:
  - `ast-origin-sig2` が在る → 新しい署名だけで判定する（合わなければ `Mismatch`。旧い印へ倒さない）。
  - 無く、`ast-origin-sig` が在る → 旧い鍵で合えば `Legacy`、合わなければ `Mismatch`、旧い鍵が無ければ `Unverifiable`。
  - どちらも無い → `Missing`。
- 段 1（Report）: 合わないもの（`Legacy` を含む）は記録して通す。段 2（Enforce）: `Valid` 以外は処理しない（旧い印は受けない）。
- 段 2 の起動の止め（fail-closed）: 発行元なのに署名の鍵が無い／確かめる発行元の公開の鍵が 1 つでも無い／旧い共有の鍵が構成されている、のどれかで止める。
- 段に依らず止める: 読めない鍵・P-256 でない鍵・署名の鍵の欄に公開の鍵（またはその逆）・発行しないサービスに署名の鍵。

## 4. 監査の確かめ（記録だけ）

- 監査は受けた事実の台帳なので、確かめられないメッセージも**捨てない**（捨てると証跡が欠ける）。表の型を受けるハンドラの前で確かめ、`Valid` 以外を構造化した Warning（EventId 16261）とメトリクス（`outcome=recorded`）に残して処理を続ける。段 2 でも拒まない。
- 監査に要るのは公開の鍵だけ（秘密を増やさない）。段 2 で公開の鍵が欠けていれば起動で止める（構成の誤りを黙って通さない）。
- 台帳の列への記録は本 PR では入れない（台帳の形の変更・移行を伴う。IADR-0633 §残余）。
- 通知は対象外のまま（理由は IADR-0633）。

## 5. 受け入れ基準

1. 24 型すべてで、発行元の口で署名した封筒が段 2 で `Valid`。Wolverine の直列化の往復でも `Valid`。
2. 24 型すべてで、印なし（`Missing`）・許されない発行元（`ForbiddenIssuer`）・別の鍵の署名（`Mismatch`）・本文の改竄（`Mismatch`）・処理し終えた ID の再送（`Replayed`）が段 2 で拒まれる。段 1 では記録だけで通る。
3. 受け手の構成（公開の鍵だけ）からは署名できない（`Sign` が null）。公開の鍵から作った鍵の実体で署名を試みると例外。
4. 旧い HMAC の印: 段 1 は `Legacy` として通す、段 2 は拒む。段 2 で旧い鍵を構成すると起動で止まる。
5. 段 2 で要る鍵（自分の署名の鍵・確かめる発行元の公開の鍵）が 1 つでも無ければ起動で止まる（8＋監査の 9 サービス）。
6. 監査は段 2 でも確かめられないメッセージを処理する（記録だけ）。
7. chart: 各サービスへ渡す鍵がコードの要る鍵と一致する（試験）。values-cloud は段 2 で optional なし・旧い鍵の env なし。既定は段 1 で optional。旧い鍵の opt-in は段 2 と併用すると描画で止まる。
8. 鍵の生成のスクリプトは値を表示せず・引数に載せず、標準入力で基盤の口へ渡す（スタブの試験）。
9. 変異（確かめを外す・旧い印を段 2 で通す・公開の鍵を確かめに使わない）で試験が赤になる。

## 6. 試験

- `dotnet test backend/TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests`（型ごとの往復・偽装・改竄・再送・公開の鍵・旧い印・配線）。
- 受け手のサービスの試験のうち、Program の配線を通す試験（risk-management・order-execution・audit）。
- `helm.yml` の段・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
