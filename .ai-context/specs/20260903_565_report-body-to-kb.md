---
title: 確定報告書の本文（TradingReport.Body）を KB へ実際に渡す（ReportKnowledgeMapper の追随）
type: work
status: review
related_ids: [FR-08, ADR-0001, ADR-0010]
author: claude (Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 作業仕様書: ReportKnowledgeMapper が本文を送るようにする（#565 残作業）

> Issue [#565](https://github.com/endazon/ai-stock-trading/issues/565)（FR-08 確定報告書の本文が
> RAG 検索でヒットしない）の**未着手だった側**を対象とする。送信側の共有アダプタ
> （`HttpKnowledgeBaseWriter`）は既に PR #623 / [IADR-0274](../adr/IADR-0274_kb-document-body-forwarding.md)
> で `Body` を送るようになっているが、**呼び出し元の `ReportKnowledgeMapper` が
> `Content: null` を明示的に渡したままだった**ため、確定報告書の本文は依然として 1 バイトも
> 送られていなかった。本仕様書はこの欠落を埋める。

## 着手前の実測確認（前提の訂正）

- `.ai-context/specs/20260902_565_kb-document-body.md`（前作業仕様書）と issue #565 の記述は
  「基盤側に本文取り込み経路があるか」「送信側アダプタが Body を送るか」を対象としており、
  **いずれも解決済み**（IADR-0274）と正しく記録している。
- しかし `backend/Services/ReportService/Infrastructure/ExternalServices/ReportKnowledgeMapper.cs`
  （確定報告書 → `KnowledgeDocument` の写像）は IADR-0274 の作業に含まれておらず、
  `Content: null` / `ContentType: null` を**明示的に**渡すコードのまま残っていた。冒頭コメントも
  「本文は現行 platform POST /documents が受けないため送らない」という IADR-0069 時点の
  スコープ境界（IADR-0274 で解消済み）を書いたままだった。
- `ReportKnowledgeMapperTests.機密区分は_internal_本文は送らない()` が `doc.Content.Should().BeNull()`
  を主張しており、**このテストが現状（本文を送らない）を固定していた**。IADR-0274 のマージでも
  このテストは変更されておらず、赤くならなかった（`ReportKnowledgeMapper` が `IADR-0274` の対象外
  だったため）。
- 対照として `InformationCollectionService.Infrastructure.ExternalServices.KnowledgeBaseWriterSink`
  は `Content: item.Content` / `ContentType: "text/markdown"` を既に渡しており、収集情報側は
  本文欠落の対象ではない。**確定報告書だけが取り残されていた。**

結論: 本 issue の残作業は「基盤側経路の有無の確認」でも「共有アダプタの追随」でもなく、
**`ReportKnowledgeMapper` 1 箇所を IADR-0274 の決定に追随させること**である。

## 対象範囲

- 対象:
  - `backend/Services/ReportService/Infrastructure/ExternalServices/ReportKnowledgeMapper.cs`
    （`report.Body` を `Content` へ、`ContentType: "text/markdown"` を渡すように変更。冒頭コメントの
    陳腐化した記述を是正）
  - `backend/Services/ReportService/Tests/Infrastructure/ExternalServices/ReportKnowledgeMapperTests.cs`
    （「本文は送らない」テストを反転し、空 Body の扱いを新たに固定）
  - `backend/Services/ReportService/Features/Reports/ConfirmReport/Endpoint.cs`
    （`ReportKnowledgeMapper.ToDocument` へロガーを渡す配線のみ。KB 保存の呼び出し構造・fail-safe は
    無変更）
  - `.ai-context/adr/IADR-0274_kb-document-body-forwarding.md`（日付付き追記。新規 IADR は起票しない）
- 対象外:
  - `HttpKnowledgeBaseWriter` / `KnowledgeModels.cs`（IADR-0274 で完成済み。本 PR は無変更）
  - 実 KB 接続での RAG ヒット確認（実環境残件。後述「実環境確認」）
  - `UpsertReportDraft`（手動 `PUT /reports/{periodKey}`）に本文入力を追加すること
    （API 契約の拡張は計画外。本 PR は「空なら送らない」を選ぶだけで、空を埋める手段は追加しない）

## 設計

### `ReportKnowledgeMapper.ToDocument` の変更

```csharp
public static KnowledgeDocument ToDocument(TradingReport report, ILogger? logger = null)
{
    ArgumentNullException.ThrowIfNull(report);

    var kind = report.Kind.ToString();
    var attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ... }; // 無変更

    var hasBody = !string.IsNullOrEmpty(report.Body);
    if (!hasBody)
    {
        logger?.LogWarning(
            "確定報告書 {PeriodKey} は本文が空のため KB へ本文を送りません（手動確定など自動生成を経ていない可能性）。",
            report.PeriodKey);
    }

    return new KnowledgeDocument(
        Title: $"確定報告書 {kind} {report.PeriodKey}",
        Content: hasBody ? report.Body : null,
        Confidentiality: KnowledgeConfidentiality.Internal,
        Tags: ["report", kind.ToLowerInvariant()],
        SourceUri: null,
        ContentType: hasBody ? "text/markdown" : null,
        Attributes: attributes);
}
```

呼び出し元（`ConfirmReportEndpoint`）は既存の `loggerFactory.CreateLogger("ReportKnowledgeBase")` を
`ToDocument` へも渡す（現状は catch ブロックのみで生成していたロガーを、成功系の警告にも再利用する）。

### 🔴 空 Body（手動 upsert 経路）の扱いと根拠

**決定: 空文字列（`string.Empty`）は「本文なし」として `Content: null` で送り、`LogWarning` を残す。
空文字列をそのまま `Content: ""` として送らない。**

根拠:

1. **本リポの規律「未供給と 0（ここでは空）を区別する」に整合する。** `TradingReport.Body` は
   `string.Empty` を既定値に持つ非 null プロパティであり、「本文が実際に空である」と「本文が
   一度も供給されていない」を型レベルで区別できない。手動 `PUT /reports/{periodKey}`
   （`UpsertReportDraft`）経路は本文フィールドを受け取らないため、この経路で作られた報告書は
   常に `Body == ""` になる（`TradingReport.cs` のコメント参照）——これは「意図的に空の本文で
   確定した」のではなく「本文を供給する手段が無かった」ケースである。空文字を額面どおり
   「本文あり（0 文字）」として送っても、RAG 検索にとって索引価値が無いばかりか、
   `KnowledgeDocument.Content` に空文字列を渡す呼び出しが増えると将来「意図的な空」なのか
   「未供給」なのかの区別がコード上から失われる。**`null` に倒すことで「未供給」を型で表現し続ける。**
2. **`KnowledgeBodyLimits.Exceeds` は既に空文字列を「本文なし」相当として扱っている**
   （`!string.IsNullOrEmpty(content) && ...`）——`HttpKnowledgeBaseWriter` 側も空文字列と null を
   区別しない実装になっている。呼び出し元（本 PR）が空文字列を `Content: ""` のまま渡しても
   最終的な送信結果（`body` フィールド）は変わらない（`PostAsJsonAsync` は `""` をそのまま送る
   ため、`HttpKnowledgeBaseWriterTests.本文が無ければBodyはnullで送られる_否定形` は
   `Content: null` の場合のみを固定しており空文字列は未検証）。**`ReportKnowledgeMapper` 側で
   明示的に `null` へ倒すことで、`HttpKnowledgeBaseWriter` の暗黙の扱いに依存せず意図を明示する。**
3. **警告ログを残すのは、手動確定という運用上あり得る経路で本文が欠落することを運用者が
   気づけるようにするため。** 例外にはしない（確定そのものを失敗させる理由にはならない。
   FR-08 の KB 保存は既存どおり best-effort）。

### 対象外にした選択肢

- **「空 Body でも `""` をそのまま送る」**: 却下（上記理由1）。
- **`UpsertReportDraft` に本文入力を追加し、手動経路でも本文を供給可能にする**: 却下（計画外の
  API 拡張。issue #565 のスコープは「送る経路の欠落」であり「入力手段の拡張」ではない）。
- **`ArgumentException` 等で空 Body を拒否する**: 却下（確定自体を壊す。KB 保存は best-effort という
  既存方針（IADR-0069/0071 決定3）に反する）。

## テスト方針

`ReportKnowledgeMapperTests.cs` を以下の 3 点で固定する（既存の「本文は送らない」テストを反転）。

- (a) 本文が非空なら `Content` へそのまま渡る。
- (b) 本文が非空なら `ContentType` が `"text/markdown"`。
- (c) 本文が空（`string.Empty`）なら `Content`/`ContentType` はともに `null`、かつ `logger` へ
  警告ログが 1 件残る（否定形）。

ロガーの検証は、中央パッケージ管理にログ用のテストダブルが無いため、既存の同型実装
（`FxRateSourceFactoryTests.CapturingLoggerFactory`／同ファイル内 `CapturingLogger`）に倣い、
`ReportKnowledgeMapperTests` 内に最小の `ILogger` 捕捉実装を置く（警告以上のログのみ記録）。

`HttpKnowledgeBaseWriterTests`（1 MB 超の縮退・Body null/非 null の送信）は無変更で流用できる
（`ReportKnowledgeMapper` が渡す `Content`/`ContentType` は `KnowledgeDocument` の既存フィールドの
値を変えるだけで、`HttpKnowledgeBaseWriter` 側の写像ロジックには影響しない）。

`ConfirmReport` の結合テスト（`ConfirmReportEndpointTests` 等、既存があれば）に「確定 → writer に
本文入りの文書が渡る」を 1 件追加できるか確認し、可能なら追加する（#563 と同型の「呼ばれたこと ≠
出口へ出たこと」の再発防止）。

## 受け入れ基準

- [x] `ReportKnowledgeMapper.ToDocument` が `TradingReport.Body`（非空時）を `Content` へ、
  `ContentType: "text/markdown"` を渡す
- [x] 空 Body は `Content: null`・`ContentType: null` で送り、`LogWarning` を残す
- [x] `ReportKnowledgeMapperTests` の「本文は送らない」テストを反転し (a)(b)(c) を固定する
- [x] `ConfirmReportEndpoint` がロガーを `ToDocument` へ配線する
- [ ] 確定報告書の本文が RAG 検索でヒットすることの結合テスト — **実環境残件のまま**
  （issue #565 受け入れ基準③。前作業仕様書 `20260902_565_kb-document-body.md` の「実環境確認」で
  記録済みの 2 つの環境障害（Istio mTLS ドリフト・KnowledgeBase:Auth:Authority の realm 名誤り）と
  Voyage AI 埋め込み API キー未設定がいずれも未解消のため、本 PR の範囲では確認できない）

## 実 KB での確認手順（オーケストレータへの引き継ぎ）

前作業仕様書 `.ai-context/specs/20260902_565_kb-document-body.md` §実環境確認で記録済みの
2 つの環境障害が是正された前提で、以下の手順により確認する（本 PR 内では実行しない。
Istio mTLS により AST→基盤が全断のため）。

1. **前提の是正**（本 PR の範囲外。インフラ/デプロイ設定）:
   - `microservices-platform` namespace の `PeerAuthentication`（`STRICT` へドリフト）を宣言どおり
     `PERMISSIVE` に戻すか、`ai-stock-trading` namespace を Istio メッシュへ参加させる。
   - `deploy/helm/ai-stock-trading/values-local.yaml` の `KnowledgeBase:Auth:Authority` を
     実際の MSP realm id（`platform`）に合わせる（現状 `microservices-platform` と誤っている）。
   - MSP 側 `llmgateway-service` に Voyage AI の Embedding API キー
     （`Embedding:Voyage:ApiKey`）を投入する（未設定だと埋め込みが完了せず検索が常に 0 件）。
2. **s2s のロール確認**: `ai-stock-trading-kb-writer`（MSP `platform` レルムの client_credentials）が
   `platform-operator` ロールを持つこと（`POST /documents` の書き込みロール要件を満たす。
   IADR-0093 で付与済みのはずだが realm 是正後に再確認する）。
3. **本 PR をマージしたイメージを再デプロイ**し、日報を 1 件確定する
   （`POST /reports/{periodKey}/confirm`）。
4. `kubectl logs deploy/report-service -n ai-stock-trading` に KB 保存の失敗ログが出ていないこと
   （警告ログ「本文が空」も出ていないこと＝自動生成の本文が乗っていることの確認）を見る。
5. `document-service` の応答（または DB）で当該文書の `markdownUri` が設定されていることを確認する。
6. 数十秒〜数分待ってから `POST /search`（`retrieval-service`）に確定報告書本文中の一意な語句
   （例: `PolicySummary` に含めたユニークな文字列）を投げ、`totalHits > 0` かつヒットした
   `DocumentTitle` が「確定報告書 …」であることを確認する。
7. 確認できたら issue #565 の受け入れ基準③にチェックを入れ、issue をクローズ可否を判断する
   （本 PR の PR 本文では `Closes` にしない。実環境確認が残るため `Refs #565` に留める）。

## 計画書との差異

- 差異: なし。FR-08 の受け入れ基準（本文が RAG 検索でヒットする）に向けた前進であり、
  IADR-0274 が既に確定した設計（Body 送信・1 MB 超の扱い）をそのまま利用する。新規の設計判断は
  「空 Body の扱い」1 点のみであり、IADR-0274 への日付付き追記として記録する（新規 IADR は
  起票しない）。

## 未決事項

- なし（実環境確認は前作業仕様書が既に切り分け済みの残件であり、本 PR のスコープ外として
  オーケストレータへ引き継ぐ）。
