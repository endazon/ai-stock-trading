---
title: LLM 単価が未設定のまま LLM を有効にした配備（環境名 Production）は起動しないようにし、本番の単価の投入手段を決め、values-local の「本番へ移植済み」の記述を実物に合わせる（#1197・第 4 回全体監査 A-3）
type: spec
status: accepted
related_ids: [NFR-13, FR-04, ADR-0037, ADR-0017, IADR-0499, IADR-0122, IADR-0114, IADR-0296, IADR-0055, IADR-0496, IADR-0111]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-13 月次 LLM 費用上限・対象は取引判断サイクルの LLM 費用のみ。報告書生成は対象外で月報に実績を載せる)
---

# LLM 単価が未設定のまま LLM を有効にした配備は起動しない（#1197）

## 背景（issue の観測）

- `deploy/helm/ai-stock-trading/values.yaml` に `LlmPricing` は 0 件（設計: IADR-0122 決定 4・IADR-0114 決定 6・IADR-0296。
  既定描画に `LlmPricing__` が出ると `helm.yml` が失敗する）。
- `LlmPriceTable` は表が空なら従来キーへ倒れ、未設定は 0。0 円計上では月次費用上限（¥15,000）の 80% / 100% 判定が発火しない。
- #817 で「ゲートウェイ構成あり × 実質 0」を起動時の WARNING にしたが、稼働は止めない。
- 本番での投入手段が IADR にも Runbook にも無い。`values-local.yaml`（当時 393-394 行。現 399-400 行）は
  「#243 実測で本番 values.yaml が表 0 件のまま残っていた瑕疵を発見・是正済み（本表を移植した）」と実物と逆を書いている。

## 起点 ID

- **NFR-13**（月次 LLM 費用上限）。FR-04・計画 ADR-0037 決定 1・ADR-0017（issue の起点 ID のとおり）。
- 計画の NFR-13 は上限の対象を**取引判断サイクルの LLM 費用だけ**とし、報告書生成は対象外（月報に実績を載せる）。
  report の 0 円計上は上限ではなく月報の実績を誤らせる。両サービスに同じ判定を置く理由として IADR-0499 に書いた。

## 実測（origin/develop d0a4b232）

| 問い | 実測 |
| --- | --- |
| LLM 費用を計上するサービス | `LlmPriceTable` を組み立てるのは `TradeDecisionService/Program.cs` と `ReportService/Program.cs` だけ（`BuildLlmPriceTable`）。cost-control は `LlmCostIncurred` の金額を積むだけで単価を知らない |
| tier を知る口 | chart の `broker.tier` は `Broker__Provider` / `Broker__Environment` として **order-execution にだけ**注入（`templates/deployment.yaml`）。trade-decision / report には届かない |
| 環境名 | IADR-0496（#1205）で全 Worker の `ASPNETCORE_ENVIRONMENT` は既定 `Production`、`Development` は描画で止まる。values-local も Production を継ぐ |
| 本番既定の LLM | `values.yaml` の trade-decision / report とも `LlmGateway__BaseUrl: ""`、`LlmGateway__Grpc` はコメントのみ＝LLM を呼ばない |
| 経路B（現 PoC） | `values-local.yaml` の trade-decision・report とも `LlmPricing__PerModel__*`（`_` 形）を持ち、LLM の宛先を持つ |
| 試験のホスト | `WebApplicationFactory` の既定は Development、多くの試験は `UseEnvironment("Testing")`。LLM の宛先を単価なしで与える試験が複数ある（`LlmGatewayAuthWiringTests` 等） |
| docker-compose | `ASPNETCORE_ENVIRONMENT: Development`、`LlmGateway__BaseUrl: ${LLM_GATEWAY_BASEURL:-}`、単価なし |
| 本番の環境別 values の口 | `deploy/argocd/application.yaml` に「環境別の上書きは values-<env>.yaml を valueFiles に追加する」（コメント）。現在は `valueFiles` なし |

## 決定（詳細は IADR-0499）

- 受け入れ基準 1 の二択は **起動拒否**を採る。判定は「ゲートウェイ構成あり × 単価が実質 0 × 環境名 Production」。
  - tier では判定しない（届かない・LLM の費用は tier に依らない）。
  - Production 以外は従来の警告。計上時の解決は変えない。
  - 費用統制を Halted 相当に倒す案・Helm の `fail` 案・本番 `values.yaml` に表を置く案は IADR-0499 で却下理由を残す。
- 受け入れ基準 2: 投入手段（現在の実現手段）＝配備時の values の上書き（ArgoCD `valueFiles`／helm `-f`）。
  `docs/operations/operations.md` に「本番の LLM 単価の投入」節、IADR-0122 に日付つき追記、chart README に要約。
- 受け入れ基準 3: `values-local.yaml` のコメントを「本番は表を持たない・投入は手順 2」へ直す。
- 受け入れ基準 4（否定形）: 経路B の単価表の行・値と IADR-0122 決定 3 は触らない。

## 母集合の引き直し（規則 9・10）

### 規則 9: 誤りの側の文字列で全文書を走査

走査語: `移植`・`本番 values.yaml`・`本番 \`values.yaml\``（単価・`LlmPricing` を含む行）、`構造的に発火`・`¥0 計上`・`0 円計上`。
`.ai-context/specs/` は凍結記録として除外。

| ヒット | 判定 |
| --- | --- |
| `values-local.yaml`「本番 values.yaml が表 0 件のまま残っていた瑕疵を発見・是正済み（本表を移植した）」 | **誤り**。書き直した（基準 3） |
| `deploy/helm/ai-stock-trading/README.md`「本番 `values.yaml` には置かない」 | 正しい。直後に本番の投入手段の段落を足した |
| `IADR-0114` 決定 6・却下案「本番 values.yaml にも置く」 | 正しい（決定は維持）。追記不要 |
| `IADR-0296`「本番へは投入しない」「良い影響: 月次上限が両サービスで実効化される」 | 前者は正しい。後者は経路B に限った記述と読めないので、日付つき追記で「本番は IADR-0499 の手段で単価を与えたときに限る」と補った |
| `docs/operations/operations.md`「本番の計上は従来どおり ¥0」 | 本番既定は LLM を呼ばないので実害はないが、LLM 有効化時の扱いが無い。書き直し、新節を足した |
| `LlmPriceTableTests.cs`「本番 values.yaml の現状」（0 円） | 正しい（解決は 0 のまま）。起動拒否への参照を足した |
| `values-local.yaml`・chart README の「¥0 計上」「構造的に発火しない」（単価なしの帰結の説明） | 正しい（単価なしの帰結）。変更不要 |
| `CHANGELOG.md` の #817 行 | 生成物・当時の記述。変更しない |

### 規則 10: この変更で新たに誤りになる記述

走査語: `単価が未設定`・`単価が実質 0`・`例外は投げない`（LLM 単価の文脈）・`WARNING`。

| ヒット | 対応 |
| --- | --- |
| `TradeDecisionService/Program.cs`・`ReportService/Program.cs` の #817 コメント「例外は投げない」 | 置き換えた（Production は起動しない） |
| `LlmPricingWiringTests`・`LlmPricingStartupWarningTests` の「例外は投げない」 | Production 以外に限る旨と新しい試験 ID を追記 |
| `IADR-0122` の #817 追記 3「例外は投げない」 | 凍結記録の本文は直さず、日付つき追記で「配備に限って改める」 |
| `docs/operations/operations.md`「起動時に … WARNING を出す」 | Production は起動しない・それ以外は WARNING へ |
| chart README「起動時に WARNING（`LLM 単価が未設定 …`）を出す」 | 同上 |
| `LlmPriceTable.IsEffectivelyZero` の doc コメント「起動時の警告の判定にだけ使う」 | 起動拒否にも使う旨へ |

導出値: 試験 ID は develop の最大 T-10-2365、open PR（#1191 は最大 T-10-2337 の追加、#1210 は T-10 の追加なし）と重ならない
**T-10-2366〜T-10-2372** を使う。IADR 番号は 0495（#1191）・0497（#1210）・0498（予約）を避けて **0499**。

## 受け入れ基準

1. Given 環境名 Production・LLM ゲートウェイ（REST の絶対 URI か gRPC）構成あり・単価が実質 0 When trade-decision / report を起動 Then
   `LLM 単価が未設定` で始まる `InvalidOperationException` で起動しない（T-10-2369・T-10-2371）。
2. Given 同上で単価がある、またはゲートウェイ未構成 Then 起動し、文言を出さない（T-10-2370・T-10-2372）。
3. Given Production 以外 Then 従来どおり警告して起動する（既存の警告の試験が緑のまま）。
4. 判定の純関数の全組み合わせ（T-10-2366〜T-10-2368）。
5. 投入手段が IADR-0122 の追記・IADR-0499 決定 3・`docs/operations/operations.md` に「現在の実現手段」として書かれる。
6. `values-local.yaml` のコメントが実物に一致する。経路B の単価表の行と値、IADR-0122 決定 3 は不変（`git diff` で確認）。
7. 既定描画（`values.yaml`）と values-local の描画が通り、既定描画に `LlmPricing__` が現れない（`helm.yml` の該当段を手元で実走）。
8. 変異 8 本がすべて赤。

## 変更するファイル

- `backend/Shared/AiStockTrading.Shared.Infrastructure/Composable/Llm/LlmPricingStartupGuard.cs`（新規）・`LlmPriceTable.cs`（doc コメント）
- `backend/Services/TradeDecisionService/Program.cs`・`backend/Services/ReportService/Program.cs`
- 試験: `LlmPricingStartupGuardTests.cs`（新規）・`LlmPriceTableTests.cs`（コメント）・`LlmPricingWiringTests.cs`・`LlmPricingStartupWarningTests.cs`
- `deploy/helm/ai-stock-trading/values.yaml`（コメントのみ）・`values-local.yaml`（コメントのみ）・`README.md`
- `docs/operations/operations.md`・`docs/tests/FR-10_risk-controls-tests.md`
- `.ai-context/adr/IADR-0499_*.md`（新規）・`IADR-0122`・`IADR-0296`（日付つき追記）・`.ai-context/adr/README.md`

## 現 PoC への影響

- 経路B（`values-local.yaml`）: trade-decision / report とも単価表を持つため、Production でも起動は変わらない。
- 本番既定（`values.yaml`）: LLM の宛先が空なので起動は変わらない。
- docker-compose（Development）: 警告のまま。
- 🔴 経路B で単価の行を消す・`-` 形へ戻すと、従来は警告だけだったものが**起動しなくなる**（意図した変化）。
