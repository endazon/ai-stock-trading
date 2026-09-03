---
title: LlmPricing 本番投入・#636 文書是正・blocked-tasks 実測反映
type: spec
status: draft
related_ids: [FR-10, FR-04, FR-06, FR-16, NFR, ADR-0011, ADR-0014, ADR-0015, ADR-0016, ADR-0029]
author: claude (worker)
created: 2026-09-03
updated: 2026-09-03
plan_refs: [ADR-0016 決定6, ADR-0011, ADR-0014, ADR-0015]
---

# 仕様書: LlmPricing 本番投入・#636 文書是正・blocked-tasks 実測反映

> 本仕様書は実装着手前に作成する。3 件（#243 のコード瑕疵切り出し・#636・blocked-tasks 実測反映）は
> いずれも「文書・設定と実体の食い違いを正す」性質のため 1 PR にまとめる。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04（AI 判断のガードレール）／FR-06・FR-16（報告書生成）／FR-10（リスク統制）
- 関連 ADR: ADR-0011（LLM モデル固定）／ADR-0014・ADR-0015（用途別モデル割当）／ADR-0016 決定6（統制値の表）／ADR-0029（資料再編・planning 依存の全撤去）
- 起点 issue: #243（IADR-0101 フォローアップの一部）／#636（利用者裁定 (b)）／blocked-tasks.md 実測反映（A-13/A-14/A-16）

## 目的・背景

3 件はいずれも「実体（コード・実測）と文書・設定の記述が食い違っている」ことの是正である。

1. **#243 のコード瑕疵**: 本番 `values.yaml` に `LlmPricing` のモデル別単価表が 0 件のまま残っており、
   月次 LLM 費用上限（¥15,000）の 80%/100% 判定が構造的に発火しない。加えて `values-local.yaml` の
   コメント 2 件（#282 の状態・`report-monthly` の割当モデル）が陳腐化している。
2. **#636**: 統制値の計画適合検査（`PlanConformance.Tests`）が #536 で削除されたのに、
   `docs/DEFINITION_OF_DONE.md` と `docs/blocked-tasks.md` が「担保している」という前提の記述を
   残していた。利用者裁定により **(b) 検査を復活させない**を選び、記述を実体に合わせる。
   検査復活の是非自体は別 issue（#675）に切り出す。
3. **`docs/blocked-tasks.md` の実測反映**: A-14（PVC アノテーション是正 PR）が実際にはマージ済み、
   A-13 に `openai-api-key` も空である旨が漏れていた、Keycloak の AST レルム消失という新規事象
   （A-16）が発見・復旧済みだった。

## 対象範囲

- 対象:
  - `deploy/helm/ai-stock-trading/values.yaml`（`trade-decision`・`report` 両サービスへ `LlmPricing` 投入）
  - `deploy/helm/ai-stock-trading/values-local.yaml`（陳腐化コメント 2 箇所＋関連箇所の是正）
  - `docs/DEFINITION_OF_DONE.md`（統制系チェックリスト 2 行の是正）
  - `docs/blocked-tasks.md`（借株料残置行・A-13・A-14・A-16 の是正/新設）
  - 検査復活 (a) の是非を問う別 issue の起票
- 対象外:
  - #243 本体が要求する「稼働環境での実測（egress 計測・月次見積り再評価）」——本 PR は静的な設定・文書の
    食い違いの是正に限る。#243 はクローズしない。
  - #636 の (a)（検査復活）の実装——利用者裁定により別 issue（#675）へ切り出す。

## 設計

### (A) LlmPricing 本番投入

- `values-local.yaml`（経路B・IADR-0114）の単価表を、出典コメントごと `values.yaml` の
  `trade-decision.extraEnv` へ移植した。
- 🔴 **調査で判明した追加の瑕疵**: `LlmPriceTable` は各サービスの `Program.cs` が自前の
  `IConfiguration` から組み立てる（`backend/Services/ReportService/Program.cs` L72-80、
  `backend/Services/TradeDecisionService/Program.cs` 同型）。つまり **単価表はサービスごとに
  独立して構成される**。#282（report-service 自身の計上経路 `PublishingLlmUsageReporter` を実装）が
  クローズ済みであっても、`report` サービスの helm セクションに `LlmPricing__PerModel__*` が
  無ければ report-service の `LlmPriceTable` は空のまま fail-safe ペア（未設定 0）へ落ち、
  **report-monthly/weekly/daily の LLM 費用は依然 ¥0 計上のまま**だった。取引判断は
  `claude-opus-4-8` 固定（ADR-0011）のため Opus 5 化の思考トークン増が直接効くのは
  `report-narrative`（#243 背景に明記）であり、ここへ単価が入っていないことは #243 の趣旨に
  真っ向から反する。**したがって `report` サービスの helm セクション（`values.yaml` /
  `values-local.yaml` 両方）にも同じ単価表を追加した。**
- `report-monthly` の割当モデルが `values-local.yaml` の 2 箇所のコメント
  （L217-218 相当・L163 相当のタイムアウト説明）で `claude-fable-5` のまま陳腐化していたのを
  `claude-opus-5`（MSP#429・計画 ADR-0015）へ是正した。根拠は
  `backend/Shared/AiStockTrading.Shared.Contracts.Tests/LlmAssignmentsTests.cs` の
  スナップショット（`"report-monthly | claude-opus-5 | [claude-sonnet-5] | fallback=True"`）。
- `#282 で計上経路自体が無い` というコメントも #282 が CLOSED（コミット `f1b26d4b`・IADR-0121）
  済みであるため是正した。

### `MaxTokens` の構成化 — **採らない**

`HttpReportNarrativeDrafter.cs` / `HttpLlmCompletionClient.cs` の `MaxTokens: 4096` はコード直書きの
まま据え置く。理由:

1. **#243 自身が「稼働環境での実測を経てから再調整する」と明記している**（スコープ節「実測結果を
   踏まえた MaxTokens 4096 の再調整」）。実測値が無い時点で構成点だけ先に作ると、**どの値へ
   チューニングすべきかの根拠が無いまま「調整可能にした」という見かけの対応**になり、
   計画外の過剰な抽象化（CLAUDE.md 禁止事項）に当たる。
2. **基盤側の値と揃える必要がある**（#243 スコープ「基盤側 #380 と値を揃えて調整する」）。
   構成点を先に作ると、AST 側だけ設定でき MSP 側は揃わないという半端な状態になり得る。
3. 前回の変更（#241・IADR-0101・1024→4096）も**コード変更＋デプロイ**で行っており、
   運用上ここがボトルネックになった実績が無い。ホットな構成変更を要する頻度の高いパラメータでは
   ないため、今この PR で構成化する緊急性が無い。
4. 本 PR の性質（文書・設定と実体の食い違いの是正）から外れる新規の運用面（Options＋Loader＋
   values.yaml/values-local.yaml 追加キー＋テスト）を持ち込むと、レビューの焦点が散る。

→ **#243 に実測・再調整タスクとして残置する**（本 PR ではクローズしない）。

### (B) #636 文書是正

- `docs/DEFINITION_OF_DONE.md` の統制系チェックリスト 2 行を、「検査が存在しない」という
  事実に合わせて是正した（1 行のみを指摘していた issue の記述より広い——実際は 2 行とも
  同じ前提の誤りを含んでいた）。
- `docs/blocked-tasks.md` の借株料 20% 統制の残置行の「計画適合検査で担保している」を、
  実測で健在を確認した**テスト 3 件**（`借株料上限は境界で切り替わる`／
  `借株料の閾値判定は発火しない既知の統制として残置される`／
  `実測の借株料をそのまま年率として写像すると全件拒否になる`。いずれも
  `backend/Services/RiskManagementService/Tests/Domain/ShortSellingControlsTests.cs`）のみで
  担保している旨に是正した。
- 残骸 `backend/Tests/AiStockTrading.PlanConformance.Tests/{bin,obj}` は、本ワークツリーでは
  **存在しないことを確認した**（`find . -iname "*PlanConformance*"` が 0 件）。#636 が言及する
  「ls backend/Tests/ に今も出て『まだ在る』と誤認させる」状態は、ビルド成果物のある別クローンで
  観測されたものであり、本ワークツリーには追跡対象・未追跡ファイルとも存在しない。追加の
  掃除作業は不要と判断した。
- (a)（検査復活）の是非は別 issue [#675](https://github.com/endazon/ai-stock-trading/issues/675) を
  起票し、本 PR の範囲から切り離した。

### (C) `docs/blocked-tasks.md` 実測反映

- A-13: `openai-api-key` も空（base64 長 0）である旨を追記。
- A-14: 是正 PR [#647](https://github.com/endazon/ai-stock-trading/pull/647) がマージ済みで、
  `deploy/helm/ai-stock-trading/templates/opend.yaml` の PVC `opend-persist` に
  `helm.sh/resource-policy: keep` が付与されていることを実測で確認（再測定手順①合格）。
  挙動としての切替確認（②）は未実施のまま残置。
- A-16（新設）: Keycloak の AST レルム消失（`keycloak-realms` ConfigMap に realm が無く
  `realms/ai-stock-trading` が 404 → 全 s2s が「認証なしで送信」→ 401 → fail-safe 縮退）を
  2026-09-03 に発見し、ConfigMap への realm 再投入と Keycloak 再起動で同日中に復旧を確認した。
  再発条件（MSP `k8s-local-up.sh` を再実行せずに Keycloak DB のみ作り直す）を記録した。

## 受け入れ基準

- [x] 本番 `values.yaml` に `LlmPricing` のモデル別単価表が投入されている（trade-decision・report 両方）
- [x] `values-local.yaml` の陳腐化コメント（#282 の状態・report-monthly の割当モデル）が是正されている
- [x] `MaxTokens` 構成化の採否判断とその理由が本仕様書に記録されている
- [x] `docs/DEFINITION_OF_DONE.md` の「CI が通っていれば自動的に満たされている」という誤った担保の記述が是正されている
- [x] `docs/blocked-tasks.md` の借株料残置行の担保の記述が実体（テスト 3 件のみ）に一致している
- [x] (a) 検査復活の是非を問う issue が起票されている
- [x] `docs/blocked-tasks.md` の A-13・A-14・A-16 が実測に基づき是正・新設されている
- [ ] `dotnet build backend/backend.slnx` が警告 0 で通る
- [ ] `check-trace-blocks.js`・`check-cross-repo-refs.js`・`check-doc-links.js`・`check-adr-index-sync.js`・`check-reading-budget.js` が通る
- [ ] `helm template` が values.yaml / values-local.yaml の両方で描画できる

## テスト方針

本 PR はコードを変更しない（helm 値・コメント・docs の是正のみ）ため、新規テストは追加しない。
既存の `LlmAssignmentsTests`（用途別モデル割当のスナップショット）と
`ShortSellingControlsTests` の借株料 3 テストが、コメント修正の根拠として引用した実体を
すでに固定している。`helm template` の描画確認で構文エラーが無いことのみ確認する。

## 計画書との差異

- 差異: なし。本 PR は計画書の記述を変えるものではなく、実装・設定・文書間の内部矛盾を是正するもの。

## 未決事項

- #243 が要求する「稼働環境での実測」（egress 計測・月次見積り再評価・MaxTokens 再調整）は
  本 PR の範囲外として残置する（#243 はクローズしない）。
- #675（検査復活の是非）は別セッションでの判断待ち。
