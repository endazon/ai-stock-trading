---
title: IADR-0499 LLM ゲートウェイを構成したのに単価が実質 0 なら、配備（環境名 Production）の trade-decision / report は起動しない。本番の単価は配備時の values で与える
type: impl-adr
status: Accepted
related_ids: [NFR-13, FR-04, ADR-0037, ADR-0017, IADR-0055, IADR-0114, IADR-0122, IADR-0296, IADR-0496, IADR-0111]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-13 月次 LLM 費用上限 15,000 円・FR-04)
  - planning:projects/ai-stock-trading/07_adr/ADR-0037_sonnet5-price-correction-and-cutoff-mapping.md (決定 1)
related_specs:
  - ../specs/20261007_1197_llm-pricing-unset-guard.md
---

# IADR-0499: LLM 単価が未設定のまま LLM を有効にした配備は起動しない（#1197）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-07
- 決定者: Claude Code（実装）。起点 [#1197](https://github.com/endazon/ai-stock-trading/issues/1197)（第 4 回全体監査 A-3）

## 起点・関連

- 関連する計画書 ID: NFR-13（月次 LLM 費用上限 ¥15,000）・FR-04・計画 ADR-0037 決定 1・ADR-0017
- 作業仕様書: [`20261007_1197_llm-pricing-unset-guard`](../specs/20261007_1197_llm-pricing-unset-guard.md)
- 前提の実装判断: [IADR-0122](./IADR-0122_per-model-llm-pricing.md) 決定 3・4（未知モデルは最大単価・単価表は経路B 限定）／
  [IADR-0114](./IADR-0114_route-b-parity-observed-drawdown-and-official-sources.md) 決定 6／
  [IADR-0296](./IADR-0296_llm-pricing-stays-local-profile-and-plan-conformance-check-deferral.md)（本番の単価の与え方は「未決の設計課題」として残していた）／
  [IADR-0496](./IADR-0496_aspnetcore-environment-production-and-problem-details.md)（配備の環境名は既定 Production・Development は描画で止める）

## コンテキスト

- 本番 `values.yaml` の `LlmPricing` は 0 件（設計。IADR-0122 決定 4。既定描画に `LlmPricing__` が現れると `helm.yml` が失敗する）。
- `LlmPriceTable` は表が空なら従来キーへ倒れ、未設定は 0。単価 0 では `PublishingLlmUsageReporter` が ¥0 を計上し、
  月次費用上限の 80% / 100% 判定が構造的に発火しない（IADR-0114 決定 6 がかつて直した状態）。
- #817 で「ゲートウェイ構成あり かつ 単価が実質 0」を起動時の **WARNING** にしたが、例外は投げなかった。稼働は止まらず、
  ログを見なければ統制が無効なまま走る。
- 本番の単価の投入手段は IADR にも Runbook にも無く、`values-local.yaml` のコメントは「本番 values.yaml へ移植した」と
  実物と逆のことを書いていた。
- 本番（#24・Hetzner）は未配備。本番既定は `LlmGateway__BaseUrl` も空（LLM を呼ばない）で、現時点の実害は無い。

### 階層（tier）を知る口の実測

| 口 | 誰が読むか | 費用を計上するサービスに届くか |
| --- | --- | --- |
| `broker.tier`（chart） | template が `Broker__Provider` / `Broker__Environment` へ展開 | **order-execution にだけ**注入される。trade-decision / report には届かない |
| `ASPNETCORE_ENVIRONMENT` | 全 Worker（IADR-0496。既定 `Production`、`Development` は描画で止まる） | 届く（`IHostEnvironment`） |
| LLM ゲートウェイの宛先 | trade-decision / report（`LlmGateway:BaseUrl` の絶対 URI か `LlmGateway:Grpc`） | 届く。空なら LLM を呼ばない（プレースホルダ） |

LLM の費用を計上するのは trade-decision と report だけ（`LlmPriceTable` を組み立てるのはこの 2 つの `Program.cs`）。
費用統制（cost-control）は `LlmCostIncurred` の金額を積むだけで、単価を知らない。

NFR-13 の上限の対象は**取引判断サイクルの LLM 費用だけ**で、報告書生成の費用は対象外（抑制はせず、月報に実績を載せる）。
したがって trade-decision の 0 円計上は上限を無効にし、report の 0 円計上は月報の実績を ¥0 と誤って載せる。
どちらも「統制・報告が黙って誤る」点で同じなので、両サービスに同じ判定を置く。

## 検討した選択肢

| # | 案 | 長所 | 短所 | 評価 |
| --- | --- | --- | --- | --- |
| A | **起動を拒否する**（ゲートウェイ構成あり × 単価が実質 0 × 環境名 Production） | 誤配備が Pod の起動失敗（CrashLoopBackOff）として即座に表へ出る。1 サービス内で完結し、契約を増やさない。IADR-0496 の起動時の検査と同じ形 | 単価を与え忘れると取引判断・報告書が止まる（LLM を使う機能が丸ごと止まる） | **採用** |
| B | 費用統制を「単価未設定」として停止状態（Halted 相当）に倒す | 他の機能（監視・リスク管理）は動き続ける | 単価の有無は cost-control から見えない（金額しか届かない）。新しいイベント／契約と、停止の解除条件が要る。停止中も LLM を呼ぶ経路が残ると実費だけが積まれる | 却下（影響範囲が大きく、統制の穴を別の形で残し得る） |
| C | 判定を `broker.tier`（paper 以外）で行う | issue の文面に近い | tier は trade-decision / report に届かない（新たな env の注入が要る）。**LLM の費用はブローカ階層に依らず実費**であり、paper の PoC でも同じ額がかかる＝tier は統制の有無の判定に関係しない | 却下 |
| D | Helm の描画で `fail`（LLM の宛先あり かつ `LlmPricing__` 行なし） | クラスタへ届く前に止まる | 単価は従来キー・`secretKeyRef`・別の値ファイルからも来うるので、描画からは実効値が見えない（偽陽性）。判定がアプリと 2 箇所に写される。docker-compose・`kubectl` 直適用は通る | 却下（アプリ側の判定を単一の正とする） |
| E | 本番 `values.yaml` に単価表を置く | 与え忘れが起きない | IADR-0122 決定 4・IADR-0114 決定 6・IADR-0296 が却下済み（変動する外部価格を本番既定に固定すると陳腐化が検出されない） | 却下（決定 4 は変えない） |

## 決定

### 決定 1: 配備（環境名 Production）では、LLM ゲートウェイを構成したのに単価が実質 0 なら起動しない

- 判定は共有の純関数 `LlmPricingStartupGuard.Evaluate(table, llmGatewayConfigured, isProduction)`
  （`AiStockTrading.Shared.Infrastructure.Composable.Llm`）。
  - ゲートウェイ未構成、または単価がある（`LlmPriceTable.IsEffectivelyZero` が偽）→ `Ok`（何もしない）。
  - ゲートウェイ構成あり かつ 実質 0 → Production なら `Refuse`、それ以外は `Warn`（#817 の警告のまま）。
- trade-decision / report の `Program.cs` は `Build()` の後で判定し、`Refuse` なら `InvalidOperationException`
  （文言は `LLM 単価が未設定` で始まり、投入手段と本 IADR を名指す）を投げる。
- 「実質 0」の定義は #817 のまま（モデル別の表が空 かつ 従来キーも入出力とも 0／未設定）。解析できない・非正の行だけの表も空と同じ。
  従来キーだけ（片側だけでも）与えた構成は起動する（後方互換）。
- **環境名で分ける理由**: 配備の環境名は chart が既定 Production で描き、Development は描画で止まる（IADR-0496）。
  試験のホスト（`Testing`）と docker-compose（`Development`）は LLM の宛先を与えても単価を持たないことが多く、ここを止めると
  開発と既存の試験が壊れる。Production と名乗る配備だけを止める。
- **tier では分けない**（案 C の却下理由）。PoC は paper の階層で実際の LLM を呼んでおり、単価が 0 なら同じように上限が効かない。

### 決定 2: 計上時の解決は変えない

`LlmPriceTable.Resolve` は従来どおり例外を投げない（IADR-0055: 計測は best-effort＝LLM 応答を壊さない）。
止めるのは起動だけで、稼働中の挙動・IADR-0122 決定 3（未知モデルは最大単価）・経路B の単価表は変えない。

### 決定 3: 本番の単価の投入手段（現在の実現手段）は「配備時の values の上書き」とする

- ArgoCD の `valueFiles` に足す `values-<env>.yaml`（`deploy/argocd/application.yaml` のコメントが示す口）か helm の `-f` で、
  trade-decision と report の**両方**の `extraEnv` へ `LlmPricing__PerModel__<model>__*` を足す（env 名ではモデル ID の `-` を `_`）。
- `extraEnv` は配列で、重ねた values では丸ごと置き換わる（IADR-0489 と同じ事情）。`values.yaml` の同じ配列を全部写してから足す
  （経路B の `values-local.yaml` がこの形）。
- 単価は機密ではないので Secret は使わない。チャートの `values.yaml` には置かない（IADR-0122 決定 4 を維持）。
- 手順（描画と起動での確かめ方を含む）は `docs/operations/operations.md`「本番の LLM 単価の投入」、要約は chart README。
- 与え忘れの検知は決定 1（起動拒否）が担う。

## 理由

- 統制を定めた（NFR-13）のに本番既定では効かない状態は、黙って稼働するより止まって表に出るほうが安全側である
  （費用統制の危険側は過小計上。IADR-0122 決定 3 と同じ向き）。
- 判定に要る情報（単価・LLM の宛先・環境名）はすべて計上するサービス自身が持つ。契約もサービス間の状態も増やさない。
- 起動時の検査で配備の誤りを止める形は IADR-0496（共通の終端の表明）と揃う。

## 結果

- 良い影響: LLM を有効にした配備で単価を与え忘れると、Pod の起動失敗として即座に分かる。0 円計上で上限が黙って無効になる経路が塞がる。
- 悪い影響・トレードオフ:
  - 与え忘れると取引判断・報告書が止まる（LLM を使わない構成へ戻すか、単価を与えて配備し直す）。
  - 配備時の values は `extraEnv` の配列を写す必要があり、`values.yaml` の更新に追随し忘れると env が欠ける（既存の経路B と同じ弱点）。
- 現 PoC（経路B `values-local.yaml`）への影響: trade-decision / report とも単価表を持ち、環境名は Production（IADR-0496）なので**起動は変わらない**。
  本番既定（`values.yaml`）は LLM の宛先が空なので起動は変わらない。docker-compose（Development）は警告のまま。
- 残余リスク:
  - 環境名を Production 以外（`Staging` など。chart の `aspnetcoreEnvironment` で与えられる）にした配備は警告に留まる。
  - 単価の値が古い・桁違いでも起動は通る（鮮度は「LLM 単価の定期見直し」の運用に委ねる）。
  - 従来キーだけの構成はモデル別にならない（起動は通す。後方互換）。

## 関連

- Supersedes: なし（IADR-0122 の #817 追記の「例外は投げない」を、配備（Production）に限って改める。同 IADR に日付つき追記を置いた）
- Superseded by: なし
- 試験: T-10-2366〜T-10-2372（`docs/tests/FR-10_risk-controls-tests.md`）
