---
title: 基盤 LLM ゲートウェイへの trade-decision-screening 登録と Decision:EnableScreening 既定反転（#571）
type: spec
status: draft
related_ids: [FR-04, UC-01, NFR, ADR-0014, ADR-0017, IADR-0212, IADR-0215, IADR-0216, IADR-0218]
author: 実装エージェント（worker）
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# 仕様書: 基盤 LLM ゲートウェイへの trade-decision-screening 登録と Decision:EnableScreening 既定反転（#571）

> 本仕様書は着手前に作成する。#571 は #335（PR #555・IADR-0212/0215/0216/0217/0218/0219）の**受け皿**であり、
> 基盤（microservices-platform）側の設定不足 2 点と、AST 側の既定反転・確認手順を扱う。

## 起点となる計画書・issue（トレーサビリティ）

- 機能要求（FR）: FR-04（AI 判断のガードレール）
- ユースケース（UC）: UC-01（取引サイクル）
- 関連計画 ADR: ADR-0014 §決定1（用途別割当表）・ADR-0017 決定1・決定2（フォールバック方針・取引判断はフォールバック禁止）
- 関連 IADR: IADR-0212（purpose 呼び出しごと引数化・二段判断層別化）・IADR-0215（割当表を検証の単一情報源とする）・
  IADR-0216（フォールバック禁止の強制）・IADR-0218（費用の用途別スコープ）
- 起点 issue: AST#571（AST#335 の受け皿。基盤側 2 点の不足を解消する）

## 背景・ギャップ

IADR-0212 が導入した層別 purpose（`trade-decision` / `trade-decision-screening`）は、基盤 `Llm:Routing:PurposeModels`
に `trade-decision-screening` が**未登録**であるため機能しない。登録されるまで `Decision:EnableScreening=true` は
「一次スクリーニングが割当外と判定され全サイクル見送り」という安全側だが機能しない帰結に倒れる（IADR-0212 §帰結）。

同様に `PurposeFallbackModels`（基盤側フォールバック鎖）には `report-daily` / `report-weekly` / `report-monthly` の
鎖が未登録である（AST 側の期待値は `LlmAssignmentsTests.割当表は計画の確定値と一致する` が固定済み）。

## 対象範囲

- 対象（本リポジトリ = AST）:
  - `TradeDecisionService/Infrastructure/ExternalServices/DecisionOptionsLoader.cs`
    （**構成 `Decision:EnableScreening` が未設定のときの既定値**を false → true へ反転）
  - `TradeDecisionService/Tests/DecisionOptionsLoaderTests.cs`（既定値の回帰テスト追随）
  - `deploy/helm/ai-stock-trading/values.yaml` / `values-local.yaml`（明示的な `Decision__EnableScreening` 行を
    追加し、反転を運用設定としても可視化する）
  - `.ai-context/adr/IADR-0212_per-call-llm-purpose.md`（日付付き改訂節。基盤登録により「潜伏」が解消したことを追記）
  - `.ai-context/adr/IADR-0277_...`（本作業の決定を記録する新規 IADR）
- 対象外（本リポジトリでは変更しない。microservices-platform 側の担当）:
  - 基盤 `Llm:Routing:PurposeModels` / `PurposeFallbackModels` の登録そのもの（別リポジトリ・別 PR）
  - `DecisionOrchestrationOptions.Default`（レコードの既定値そのもの）は**変更しない**。理由は次節。

## 判断が要った点

### 判断1: `DecisionOrchestrationOptions.Default` は反転せず、`DecisionOptionsLoader` の構成読み取り既定だけを反転する

`DecisionOrchestrationOptions.Default` は 2 つの文脈で使われている。

1. **本番の構成既定**（`DecisionOptionsLoader.FromConfiguration` が構成未設定時のフォールバック先）
2. **単体テストの便宜的な「何も指定しない」フィクスチャ**（`DecisionOrchestratorTests` / `TradeDecisionAppService`
   のテスト構築子 `options ?? DecisionOrchestrationOptions.Default`）

issue #571 が要求しているのは (1) だけであり、`Decision:EnableScreening` という**構成キーの既定値**を反転することである。
`DecisionOrchestrationOptions.Default` レコード自体を反転すると (2) の**十数個の既存単体テスト**（`既定は単発判断と
等価_1回だけ二次プロンプトを呼ぶ` 等、スクリーニングを意図的に含まない「単発判断」の基準ケースとして `Default` を
使っている）が軒並み壊れ、**本来の変更対象ではないテストの意味を変えてしまう**（オーケストレーション層の単体テストは
「二段判断の機構が正しいか」を見るものであり、「本番の既定値が何か」を見るものではない）。

**決定**: `DecisionOptionsLoader.FromConfiguration` のベースラインを
`DecisionOrchestrationOptions.Default with { EnableScreening = true }` に変更する。構成で明示的に
`Decision:EnableScreening=false` を与えれば従来どおり無効化できる（fail-safe な上書き経路は維持）。
`DecisionOrchestrationOptions.Default` 自体・`TradeDecisionAppService` のテスト構築子・既存の
`DecisionOrchestratorTests` は**無改修**（本来の関心事が異なるため）。

### 判断2: values.yaml / values-local.yaml へ明示行を追加する（挙動は変えない・可視化のみ）

構成未設定時の既定が true になるため、helm values に何も書かなくても本番挙動は反転する。しかし本リポジトリの
慣行（`MarketData__EnableMarkToMarket` 等、重要な挙動フラグは既定値と一致していても明示的に env へ書き、
コメントで根拠を残す）に倣い、`Decision__EnableScreening: "true"` を明示行として追加する。**費用が発生する
LLM 呼び出しが 1 段増える変更であり、値を helm 側で追わずコードの既定値だけに委ねると、次に読む人が
「有効化されている」ことに気づけない**（IADR-0017 決定4 の可観測性の思想と同じ理由）。

### 判断3: 基盤側 `trade-decision-screening` 未登録の残存リスクへの対処

**AST 側の変更だけでは screening は機能しない**（基盤登録が前提）。本 PR は基盤側 PR（microservices-platform、
別 worker が担当）とセットでなければ「安全側だが機能しない」状態（IADR-0212 と同じ帰結）が本番へ出る。
本 PR の説明・IADR 追記に、**基盤 PR のマージ・反映が前提条件である旨を明記**する。

## 実装内容

1. `DecisionOptionsLoader.FromConfiguration`: ベースラインを `DecisionOrchestrationOptions.Default with
   { EnableScreening = true }` に変更。`Decision:EnableScreening` が有効な bool 文字列で与えられればそちらを優先
   （既存ロジックのまま。上書き方向が変わるだけ）。
2. `DecisionOptionsLoaderTests.cs`:
   - `未設定なら既定_現行挙動と等価` → `未設定なら既定でスクリーニングが有効になる`（EnableScreening.Should().BeTrue()）
   - `不正なEnableScreeningは既定false` → `不正なEnableScreeningは既定true`（新既定へ倒れる）
   - 追加: `Decision:EnableScreening=false` を明示すれば無効化できることを固定する回帰テスト（否定形の対称）
3. `deploy/helm/ai-stock-trading/values.yaml` と `values-local.yaml` の `trade-decision.extraEnv` に
   `Decision__EnableScreening: "true"` を追加し、根拠（IADR-0212/0272・基盤登録前提・#571）をコメントに残す。
4. `.ai-context/adr/IADR-0212_per-call-llm-purpose.md` に日付付き改訂節を追記（本文は書き換えない）。
5. `.ai-context/adr/IADR-0277_...`（新規）: 本判断（判断1〜3）を記録する。
6. `.ai-context/adr/README.md` の索引へ IADR-0278 の行を追加。

## 受け入れ基準

- [ ] `DecisionOptionsLoader.FromConfiguration()`（構成未設定）が `EnableScreening=true` を返す
- [ ] `Decision:EnableScreening=false` を明示すれば無効化できる（否定形）
- [ ] `DecisionOrchestrationOptions.Default` / `DecisionOrchestratorTests` / `TradeDecisionAppService` の
      構築子既定は無改修（既存テストが無改修のまま緑であること自体が回帰確認になる）
- [ ] `LlmPurposeWiringTests`（明示的に `Decision:EnableScreening=true` を設定する composition root テスト）は
      無改修のまま緑
- [ ] `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が通る
- [ ] `values.yaml` / `values-local.yaml` に明示行が入り、コメントが基盤登録前提を明記する
- [ ] IADR-0212 に日付付き改訂節、IADR-0278 が新規決定を記録し、索引に反映される

## 確認手順（本リポジトリ完結。基盤側の実クラスタ確認は別 worker・MSP 側仕様書を参照）

本 PR 単体では実クラスタへの反映を行わない（issue #571 タスク5: 実 LLM での二段判断確認は develop 再デプロイ後、
呼び出し元＝定時サイクルの再デプロイを担当する別セッションが行う）。本 PR の完了条件はコード・構成・IADR・
テストの整合のみとする。

## 計画へのフィードバック

なし（本作業は実装内の構成既定反転であり、計画書の記述と矛盾しない。ADR-0014/ADR-0017 の割当表・フォールバック
方針は変更しない）。
