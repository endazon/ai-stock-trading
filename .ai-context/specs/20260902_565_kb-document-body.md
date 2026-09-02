---
title: 確定報告書の本文を POST /documents の Body として渡し RAG 検索対象にする（＋#564 棚卸し）
type: work
status: review
related_ids: [FR-08, ADR-0001, ADR-0010]
author: claude (Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 作業仕様書: KB 文書の本文を Body として渡す（#565）＋ #564 棚卸し

> Issue [#565](https://github.com/endazon/ai-stock-trading/issues/565)（FR-08 確定報告書の本文が RAG 検索でヒットしない）
> の実装と、[#564](https://github.com/endazon/ai-stock-trading/issues/564)（情報収集の縮退の fail-open）の
> 実コード監査（PR #605 / [IADR-0267](../adr/IADR-0267_information-degradation-state-heartbeat-and-fail-closed.md)
> による解消済みの確認）を対象とする。設計判断は
> [IADR-0272](../adr/IADR-0272_kb-document-body-forwarding.md)。

## 前提の確認結果（着手前調査）

### #565: 基盤側の本文取り込み経路

MSP `DocumentService`（`src/knowledge/backend/Services/DocumentService/`）に **FR-21（本文の直接受け入れ）が
既に実装済み**であることを確認した。

- `Features/Documents/DocumentEndpoints.cs`: `CreateDocumentRequest` に `string? Body = null`（任意・末尾）が
  追加済み。`POST /documents` は Body が非空ならオブジェクトストレージ（MinIO）へ格納し `Document.MarkdownUri`
  を設定してから `DocumentUpdated` を発行する（`Ingestion` 側の取り込み分岐は `MarkdownUri` の有無で起動する
  ため、この経路だけで RAG 索引まで届く）。`PUT /documents/{id}/body`（既存文書への投入）も新設されているが、
  **AST の書き込みは常に新規作成**のため本 PR では未使用。
- `Domain/DocumentBodyIntake.cs`: 上限は **1 MB（`MaxBytes = 1024 * 1024`）・UTF-8 バイト数**で判定
  （`ExceedsLimit`）。超過は **413 で拒否し、切り詰めて成功を返さない**。
- **`POST /documents`（作成時の Body 添付）は ABAC の `CanWrite`（所有者チェック）を経由しない。** その判定は
  `PUT /documents/{id}/body`（既存文書への追記）専用であり、作成時は `write` グループのロール要件
  （`AdminRole` または `OperatorRole`）を満たせば Body を送れる。AST の s2s クライアント
  `ai-stock-trading-kb-writer` は `platform-operator` ロールを持つ（IADR-0093）ため、**追加のロール付与なしに
  Body 付き POST が通る**（実測で確認。後述「実環境確認」）。
- **所有者（`owner`）属性はサーバー側で常に上書きされる。** `DocumentBodyIntake.WithOwner` は要求由来の
  `owner` を無条件で捨て、認証済み主体（`http.User.Identity?.Name`）から入れ直す（ADR-0060 決定3・#1057）。
  したがって `HttpKnowledgeBaseWriter.BuildAttributes` が補完する `owner=system`（予約値。#520）は
  **POST /documents では実効を持たない**（サーバーが `service-account-ai-stock-trading-kb-writer` 相当へ
  上書きする）。この上書きは AST 側の変更を要しない（既存の #520 実装はそのままでよい）。

結論: **本文取り込み経路は基盤側に既に存在する。基盤への新規起票は不要。** IADR-0069 が引いたスコープ境界
（「本文は受け取らない」）は基盤側の FR-21 実装で解消済みであり、issue #565 の前提だった「基盤側に経路が
無い」という状況は変わっている。

### #564: PR #605 / IADR-0267 による解消状況

issue #564 の受け入れ基準 3 点を実コード・実テストで突合した（詳細は後述）。**すべて実装済み・テストで固定
済み**であることを確認したため、issue へ突合結果をコメントし `completed` でクローズする。

## 対象範囲

- 対象:
  - `AiStockTrading.Shared.KnowledgeBase.Adapters.HttpKnowledgeBaseWriter`（`CreateDocumentBody` へ `Body` を追加）
  - `AiStockTrading.Shared.KnowledgeBase.KnowledgeModels`（`KnowledgeBodyLimits` 純関数の新設・陳腐化コメントの是正）
  - 対応する単体テスト（`HttpKnowledgeBaseWriterTests` / 新設 `KnowledgeBodyLimitsTests`）
  - issue #564 の棚卸し（コメント投稿・クローズ）
- 対象外（本 PR では実装しない。理由は後述「実環境確認」「未決事項」）:
  - `KnowledgeBase:Auth:Authority` の realm 名修正（`microservices-platform` → `platform`）
  - Istio mTLS / サイドカー注入のクラスタ構成変更
  - RAG 検索でヒットすることの**結合テスト**（実 KB 接続が要るため実環境残件のまま。issue #565 受け入れ基準③も
    「実環境残件」と明記している）

## 設計

### Body の送信（#565 中核）

`HttpKnowledgeBaseWriter.SaveAsync` の `CreateDocumentBody`（内部 record・platform `CreateDocumentRequest` と
JSON 互換の送信形状）へ `string? Body = null` を**末尾**に追加し、`document.Content` を渡す。

```csharp
private sealed record CreateDocumentBody(
    string Title,
    string? OriginalUri,
    string? ContentType,
    Dictionary<string, string> Attributes,
    List<string> Tags,
    string? Body = null);
```

### 1 MB 超の扱い（#565 受け入れ基準を満たすための判断）

**採用: 本文なしで登録し、メタデータの保存は維持する。** 理由:

1. **切り詰めない。** platform 側 `DocumentBodyIntake.ExceedsLimit` は切り詰めを許さず 413 で拒否する
   （「全文が索引される」という FR-21 の受け入れ基準を守るため）。送信側で先に切り詰めて送ると、
   **「保存はできたが本文の一部だけが索引される」という、platform 側が明示的に拒んでいる中途半端な状態**を
   AST 側が作ってしまう。
2. **「未保存に倒す」（既存の fail-safe と同じ選択）は採らない。** 1 MB 超の本文（確定報告書・週報/月報の
   長大な散文）を理由に**メタデータの保存ごと失敗させる**と、収集情報・確定報告書の記録そのものが欠落する。
   本文がヒットしないだけの縮退（現行と同じ状態）のほうが、記録が消える縮退より安全側である。
3. 判定は **UTF-8 バイト数**（`KnowledgeBodyLimits.Exceeds`）。文字数で測ると日本語本文が実サイズの
   3 分の 1 で通り、上限が事実上 3 MB へ化ける（platform 側コメントと同じ理由）。上限値は
   platform `DocumentBodyIntake.MaxBytes` と同値（1 MB）にし、送信側で緩く判定して無駄な 413 往復を
   起こさないようにする。
4. 縮退時は `LogWarning` を残す（「本文が上限を超えるため本文なしで登録します」）。監査台帳への記録は
   本経路の対象外（KB 保存は監査 Consumer の対象イベントを持たない。既存どおり）。

### KnowledgeModels.cs の陳腐化コメント是正

`KnowledgeDocument.Content` のコメントが「現行の POST /documents は本文を受けない」のまま残っていた
（IADR-0069 時点の記述）。実際には基盤側に FR-21 が実装済みで本文を受け取れるため、「Body として送る」
「1 MB 超は送らない」に更新した。

## 実環境確認（ローカル k3s。namespace `ai-stock-trading` / `microservices-platform`）

### KB 保存が現在失敗している原因（実測）

`kubectl logs deploy/information-collection-service -n ai-stock-trading` に
`KB 保存で例外。未保存に倒します` が継続的に出ており、内側の例外は一貫して

```
System.Net.Http.HttpRequestException: An error occurred while sending the request.
 ---> System.IO.IOException: Unable to read data from the transport connection: Connection reset by peer.
```

（`HttpKnowledgeBaseWriter.SaveAsync` の `PostAsJsonAsync("/documents", ...)` 呼び出しで発生）。

**実測で特定した根本原因は 2 つ、独立に存在する。**

#### 原因 1（最も手前で塞いでいる）: Istio mTLS STRICT ＋ AST 側サイドカー未注入

- `microservices-platform` namespace は `istio-injection: enabled` でメッシュに参加しており、
  `PeerAuthentication microservices-platform-mtls` の**現在の実効モード（live spec）は `STRICT`**
  （`kubectl get peerauthentication -o yaml` で確認。ただし `kubectl.kubernetes.io/last-applied-configuration`
  アノテーションは `PERMISSIVE` のままであり、**宣言済み設定と実効状態がドリフトしている**）。
- `ai-stock-trading` namespace には `istio-injection` ラベルが**無く**、同 namespace の Pod は
  すべて `1/1`（サイドカー無し）——一方 `microservices-platform` の Pod は `2/2`（サイドカー有り）。
- **再現を確認した**: `ai-stock-trading` namespace 上の使い捨て Pod（サイドカー無し）から
  `document-service.microservices-platform:8080` へ素の HTTP で接続すると、リクエスト送信直後に
  `Recv failure: Connection reset by peer`（curl exit 56）が発生し、本番ログと**完全に一致**した。
- **本 PR の変更（Body 追加）はこの原因に無関係**であり、直しても影響しない。**AST/MSP いずれかの
  クラスタ構成（メッシュ注入の追加、または該当 `PeerAuthentication` を宣言どおり `PERMISSIVE` へ戻す）
  を変更しないと、KB 保存は現状のローカル環境では恒久的に成立しない。**

#### 原因 2（原因 1 を塞いでも残る）: KnowledgeBase:Auth:Authority の realm 名が誤っている

- AST 側の設定（`deploy/helm/ai-stock-trading/values-local.yaml:114,149`、
  `KnowledgeBaseAuthExtensions.cs` のコメント、`IADR-0093` 本文）はいずれも
  MSP レルムを `http://keycloak:8080/realms/microservices-platform` と想定している。
- しかし **実際に稼働している MSP の realm id は `platform` である**（`document-service` の
  `Auth__Authority = http://keycloak:8080/realms/platform`。MSP リポジトリの realm 定義
  `deploy/keycloak/microservices-platform-realm.json` 内の `"realm"` フィールドも `"platform"`——
  **ファイル名が `microservices-platform-realm.json` であるにもかかわらず realm id は `platform`**）。
- 実測: メッシュ内（`microservices-platform` namespace）の使い捨て Pod から
  `POST http://keycloak:8080/realms/microservices-platform/protocol/openid-connect/token` は
  **404 `Realm does not exist`**。同じ資格情報で `POST .../realms/platform/protocol/openid-connect/token`
  は **200** でトークンを取得できた。
- **この不一致は IADR-0093 が書かれた時点（2026-07-19）から MSP 側の realm id が変わった（または
  最初から `platform.json` の内容と `microservices-platform-realm.json` というファイル名が食い違って
  いた）ことによるドリフトであり、AST 側の追随漏れである。** 原因 1 が塞がっていても、この設定のままでは
  token エンドポイントが 404 になり、無トークンで送信 → `PlatformAuthPolicies` の Role 要求で 401 に
  倒れて `NotSaved` が続く。

**両方とも本 PR の範囲外**（コード変更ではなくクラスタ構成・デプロイ設定の是正であり、`.ai-context/specs`
が扱う「この PR の実装」の外側にある）。次のいずれかの形でオーケストレータへ引き継ぐことを推奨する。

- 起票候補 A: 「ローカル k3s の `microservices-platform` namespace で Istio mTLS が STRICT にドリフトしており
  `ai-stock-trading` namespace（メッシュ非参加）からの通信を全断している」
- 起票候補 B: 「AST 側 `KnowledgeBase:Auth:Authority` の realm 名が実際の MSP realm id（`platform`）と
  食い違っている（`microservices-platform` を書いている箇所: `values-local.yaml:114,149`、
  `KnowledgeBaseAuthExtensions.cs` コメント、`IADR-0093`、`appsettings.Development.json` コメント各所）」

### 基盤側の経路が通ることの実証（原因 1・2 を避けたメッシュ内 Pod から直接確認）

`microservices-platform` namespace 内に使い捨て Pod（サイドカー自動注入つき）を起動し、
`ai-stock-trading-kb-writer` の client_credentials で `platform` realm から取得したトークンを用いて
直接 `document-service` へ `POST /documents`（Body 付き）を実行した。

| 手順 | 結果 |
| --- | --- |
| `POST /realms/platform/protocol/openid-connect/token`（client_credentials） | 200・トークン取得成功 |
| `POST /documents`（Body 付き。`attributes.confidentiality=internal`） | **201 Created**。応答に `markdownUri: storage://knowledge-normalized/documents/<id>/body.md` が設定される（本文がオブジェクトストレージへ格納された証跡） |
| `ingestion-service` ログ | `DocumentUpdatedConsumer` が起動し、`storage://.../body.md` から **本文を正しく取得**（送信した本文と同じ 89 文字）。埋め込み（embedding）呼び出しへ進んだ |
| `POST /search`（`retrieval-service`。ユニークトークン `ZZQVERIFY565` で検索） | `totalHits: 0`（埋め込みが未完了のため） |

**埋め込みが完了しなかった理由は AST/本 PR と無関係な第三の環境制約**: `llmgateway-service` のログに
`System.InvalidOperationException: Voyage AI の API キーが未設定です（Embedding:Voyage:ApiKey）` が
継続的に出ている。このローカルクラスタには埋め込み用 API キーが投入されておらず、**索引化（RAG 検索で
実際にヒットする状態）まではこの環境では確認できない**。

**しかし、本 PR が触る範囲（Body の送信 → platform 側のオブジェクトストレージ格納 → Ingestion 消費者が
正しい本文を読む）はここまで実測で確認できており、「基盤側の経路が通る」ことは示せた。** 残り（埋め込み
API キー投入・RAG 検索の実ヒット確認）は原因 1・2 の是正とあわせて後続の環境整備が要る。

検証で作成した一時文書（`6cdfee53-982e-449c-9541-ffd9a707db27`。タイトル「FR-08 565 verification doc」）は
削除しようとしたが、`ai-stock-trading-kb-writer` は `platform-operator` ロールのみで `DELETE /documents/{id}`
（`AdminOnly`）を実行できず 403 だった。**クラスタに残置している**（管理者権限での削除、または定期棚卸しでの
削除を推奨）。

### 再デプロイ後の検証手順（オーケストレータへ引き継ぐ）

1. 原因 1・2 を是正する（クラスタ構成・`values-local.yaml` の realm 名）。
2. 本 PR をマージした AST イメージを再デプロイする。
3. `information-collection-service` の収集サイクルを 1 回走らせる（または `report-service` の確定報告書
   保存を発火させる）。
4. `kubectl logs deploy/information-collection-service -n ai-stock-trading` で
   `KB 保存: N/N 件を platform 文書管理へ登録` の N が 0 でないことを確認する（現状は常に `0/N`）。
5. `document-service` の応答（または DB）で当該文書の `markdownUri` が設定されていることを確認する。
6. 数分待って `retrieval-service` の `POST /search` に本文中のユニークな語句を投げ、`totalHits > 0` を確認する
   （埋め込みの非同期処理が完了するまで数十秒〜数分かかる。ingestion-service のログで
   `DocumentUpdatedConsumer` の完了を確認できる）。

## #564 棚卸し（PR #605 / IADR-0267 との突合）

issue #564 の受け入れ基準 3 点を実コード・実テストで突合した。

| 受け入れ基準 | 実装 | テストでの固定 |
| --- | --- | --- |
| ① 縮退継続中にリスク管理サービスが再起動しても、新規建ての停止が復元される | `InformationCollectionService.Hosted.DegradationStateTracker.Observe` が**遷移の有無にかかわらず毎巡回 1 件** `InformationSourceStateObserved` を発行し、`RiskManagementService.Infrastructure.Persistence.InMemoryInformationDegradationStore.ApplyObservation` が鮮度（`_observedAt`/`_validFor`）で保持する | `DegradationStateTrackerTests.縮退が続く巡回でも現況観測は毎回出る` / `.縮退が無い巡回でも空の現況観測が出る`、`InformationDegradationStoreFreshnessTests.健全な観測を受け取れば新規建ては通る_対の肯定形` |
| ② 復元できない場合は新規建てを止める側に倒す（否定形固定・対の肯定形を添える） | `InMemoryInformationDegradationStore.BlocksNewEntries` が `_degraded.Count > 0 \|\| _observedAt is not {} \|\| now - observedAt > _validFor` の OR 判定（②③が「不明なら止める」） | `InformationDegradationStoreFreshnessTests.観測を一度も受け取っていなければ新規建てを止める_否定形`（対: `.健全な観測を受け取れば新規建ては通る_対の肯定形`）。**変異試験で実証**: 既定を fail-open へ戻す変異で **11 件が赤化**（IADR-0267 対照実験 #1） |
| ③ 決済（手仕舞い・損切り）は引き続き止まらない（既存の否定形テストの回帰） | `RiskManagementService.Domain.RiskEvaluator` は `isEntry &&` の短絡で `InformationSourceDegraded` 理由を新規建てにのみ適用。`RiskEvaluator.cs` 自体は本件で無変更（IADR-0267 決定6） | `InformationDegradationEvaluationTests.縮退中でも手仕舞いは承認される_否定形` / `.他統制との任意の組み合わせでも手仕舞いは縮退理由で止まらない`（プロパティベース。他統制 2 種との全組み合わせ） |

3 点すべて実装済み・テストで固定済みであることを確認した。issue #564 へ本表を投稿し `completed` で
クローズする。

## 受け入れ基準（本 PR）

- [x] `HttpKnowledgeBaseWriter` が `KnowledgeDocument.Content` を `POST /documents` の `Body` として送る
- [x] 1 MB（UTF-8 バイト数）超の本文は送らず、メタデータのみで登録する（切り詰めない）
- [x] 上限判定は純関数（`KnowledgeBodyLimits.Exceeds`）としてテストで境界値固定
- [x] `HttpKnowledgeBaseWriterTests` に Body 送信の肯定形・否定形テストを追加
- [x] `KnowledgeModels.cs` の陳腐化コメントを是正
- [x] MSP 側 `CanWrite`/owner 属性補完の挙動を確認し本仕様書へ記録（作成時は関与しないことを明記）
- [ ] 確定報告書の本文が RAG 検索でヒットすることの結合テスト — **実環境残件のまま**（issue #565 が明記する
  実環境残件。原因 1・2 の是正と埋め込み API キー投入が前提）
- [x] #564 の受け入れ基準 3 点を実コード・実テストで突合し、issue へ記録してクローズする

## 計画書との差異

- 差異: なし。FR-08 の受け入れ基準（本文が RAG 検索でヒットする）に向けた前進であり、基盤側 FR-21 の
  実装を利用する形で計画に反する変更は行っていない。

## 未決事項

- realm 名の是正・Istio メッシュ構成の是正は、インフラ/デプロイ設定の変更であり実装 PR の範囲外と判断した。
  オーケストレータが起票するか、別途デプロイ設定 PR で対応するかの判断を要する。
- ローカルクラスタの `Embedding:Voyage:ApiKey` 未設定は、RAG 検索の実ヒット確認ができない別要因として記録する
  （AST リポジトリの管轄外）。
