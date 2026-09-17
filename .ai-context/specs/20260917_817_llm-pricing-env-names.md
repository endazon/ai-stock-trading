---
title: LLM 単価の env 名にモデル ID のハイフンが入り、イメージの sh -c 起動で落ちて全呼び出しが 0 円計上になる —— env 名をシェル識別子に揃え、単価表が空なら起動時に警告する
type: spec
status: accepted
related_ids: [FR-04, NFR, IADR-0055, IADR-0114, IADR-0122]
author: endazon (with Claude Code)
created: 2026-09-17
updated: 2026-09-17
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04／NFR 費用: 月次上限 15,000 円)
---

# 仕様書: LLM 単価の env 名をシェル識別子に揃える（#817）

## 起点

- #817（bug）。関連: #303 / IADR-0122（モデル別単価）、#282、#79 / IADR-0055、AST#342（PoC）。
- 計画: FR-04 ／ NFR（費用: 月次 LLM 費用上限 15,000 円）。

## 根本原因（#817 の実測。稼働クラスタ・2026-09-16〜17）

### 症状

- 2026-09-16 の米国開場中の LLM 呼び出し 150 回が**すべて 0 円**で計上された（audit_svc `LlmCostIncurred` 150 件・
  cost_control_svc `cost_entries` 150 件とも Amount=0.000）。月次費用上限（15,000 円）が構造的に発火しない。
- trade-decision の前世代コンテナのログ: `LLM 費用計上イベントを発行 ... model=claude-sonnet-5 in=877 out=420 amount=0.000`
  ＝**トークン数は正常、単価が 0**。
- 基盤 LLM ゲートウェイを同じサービス資格情報で直接呼ぶと `inputTokens` / `outputTokens` は正しく返る（haiku で 14/4）。

### 原因

| 観測 | trade-decision | report |
| --- | --- | --- |
| Pod 定義の env でハイフンを含む名前（`LlmPricing__PerModel__claude-sonnet-5__InputPer1kTokens` 等） | 10 | 10 |
| PID 1（`dotnet <dll>`）の `/proc/1/environ` に届いた同名 | **0** | **0** |

1. `backend/Dockerfile` の `ENTRYPOINT ["sh", "-c", "exec dotnet \"${SERVICE_DLL}\""]` で、シェル（dash）は
   識別子として不正な名前（`-` を含む）の環境変数を `exec` 先へ渡さない。
2. `values-local.yaml` の `LlmPricing__PerModel__<model-id>__*` はモデル ID をそのまま env 名へ入れていた（IADR-0122 決定4）。
3. `LlmPriceTable.From` は単価表が空のとき従来キー（`LlmPricing:InputPer1kTokens` 等・未設定）へ倒れ、`LlmPrice.Zero` になる。
   IADR-0122 決定3 の fail-safe は「未知モデル」を最大単価へ倒すが、「**表そのものが空**」は従来キーの後方互換として
   0 を許しており、しかも**無音**だった。#303 の導入以来、稼働では一度も単価が効いていない可能性が高い。

## 方針（最小）

**Dockerfile の ENTRYPOINT は変えない**（全サービス共通・#811 の codegen 経路とも共有。変更の波及が大きい）。
env 名の側をシェル識別子へ寄せ、読み手（`LlmPriceTable`）がモデル ID の `-` と `_` を同一視する。

1. **`LlmPriceTable` の照合を正規化する**: 構成キーと実効モデル名の双方で `_` を `-` へ置き換え、大小無視で照合する。
   旧形式（`claude-sonnet-5`）のキーは従来どおり読める（テスト・`:` 区切りの構成の後方互換）。
2. **fail-loud**: `LlmPriceTable.IsEffectivelyZero`（表が空 かつ 既定ペアも 0）を新設し、trade-decision / report の
   `Program.cs` が、LLM ゲートウェイ（`LlmGateway:BaseUrl` の絶対 URI、または `LlmGateway:Grpc` のアドレス）が
   構成されているのに単価が実質 0 なら**起動時に WARNING を 1 行出す**。例外は投げない（IADR-0055 の 0 は無害な
   fail-safe のまま。目的は可視化）。
3. **`values-local.yaml`**: trade-decision / report の `LlmPricing__PerModel__<model-id>__*` 20 行をアンダースコア形へ
   改名する（値は不変）。隣接コメントへ理由（dash が非識別子の env 名を落とす・#817）を書く。
4. **再発防止**: `.github/workflows/helm.yml` に、描画されたコンテナ `env:` の全 `name:` が `^[A-Za-z_][A-Za-z0-9_]*$` に
   合うことの検査を足す（既定描画と `-f values-local.yaml --set opend.enabled=true` の描画）。既存の単価アサーション
   （3 本）は新しい env 名へ追随させる。
5. **記録**: IADR-0122 へ日付つき追記（`updated:` 前進）と索引行の追随。

## 母集合（着手前に引いた。誤りの側の文字列で全走査）

| 走査語 | コマンド | 結果 |
| --- | --- | --- |
| `PerModel` | `git grep -n "PerModel" -- ':!CHANGELOG.md'` | 下表のとおり |
| ハイフン入り `LlmPricing` env | `git grep -nE "LlmPricing__[A-Za-z_]*-"` | `values-local.yaml` 20 行・`helm.yml` 3 行・`.ai-context/specs/20260828_243_*` 1 行 |
| ハイフン入り env 名（yaml 一般） | `git grep -nE "(^\|[ {-])name: [A-Za-z_][A-Za-z0-9_]*__[A-Za-z0-9_]*-" -- '*.yaml' '*.yml'` | `values-local.yaml` の 20 行のみ |
| 描画後の env 名（既定） | `helm template ast deploy/helm/ai-stock-trading` → `env:` 直下の `name:` を抽出 | 243 件・非識別子 **0** |
| 描画後の env 名（values-local＋opend） | `helm template … -f values-local.yaml --set opend.enabled=true` → 同上 | 非識別子 **20**（すべて `LlmPricing__PerModel__claude-*`。develop 時点） |
| compose | `grep -nE "__[A-Za-z0-9_]*-" docker-compose.yml` | 0 件 |

対象と扱い:

| ファイル | 扱い |
| --- | --- |
| `deploy/helm/ai-stock-trading/values-local.yaml` | **改名**（20 行）＋コメント追記 |
| `.github/workflows/helm.yml` | 既存アサーション 3 本を新名へ追随＋env 名検査を追加 |
| `deploy/helm/ai-stock-trading/README.md`（2 箇所） | 書式 `<model-id>` の説明へ「`-` は `_` で書く」を追記 |
| `docs/operations/operations.md`（1 箇所） | 同上（表示テキストに計画 ID・IADR・issue 番号を書かない） |
| `backend/**/*.cs` の `LlmPricing:PerModel:claude-*` | **据え置き**（`:` 区切りの構成キーはシェルを通らない。後方互換の証拠として残す） |

除外と理由:

- `.ai-context/specs/`（`20260731_303_*` / `20260828_243_*` / `20260902_204_*` / `20260903_636_243_*`）と
  `.ai-context/adr/IADR-0219_*` / `IADR-0296_*`: 確定済みの point-in-time 記録（書き換えない）。記録は IADR-0122 の追記に寄せる。
- `CHANGELOG.md`: 生成物。
- `.env.example`: ガードレール（`guard-bash.js`）で読めない。`LlmPricing` の構成点ではない（compose は単価を注入していない）。

## 受け入れ基準 → テスト／検証

| # | 受け入れ基準 | テスト／検証 |
| --- | --- | --- |
| 1 | アンダースコア形のキーがハイフン入りのモデル名に一致する | `LlmPriceTableTests.アンダースコアのキーはハイフンのモデル名に一致する`（Theory・5 モデル） |
| 2 | ハイフン形のキー（旧形式）も従来どおり一致する | 既存 `LlmPriceTableTests.実効モデルの単価を引く` |
| 3 | 表が非空なら未知モデルは最大単価（正規化後も不変） | `LlmPriceTableTests.アンダースコアの表でも未知モデルは最大単価へ倒す` |
| 4 | 表が空なら従来キーへ倒れる（不変） | 既存 `表が空なら既定ペアへ倒れる` |
| 5 | 表が空 かつ 従来キーも無い＝実質 0 を判定できる | `LlmPriceTableTests.IsEffectivelyZero_*`（空・全行不正・表あり・従来キーあり・片側だけ） |
| 6 | 構成（env 名 `__` 区切り＝`:`）のアンダースコア形が trade-decision の計上額へ届く | `LlmPricingWiringTests.アンダースコア形のモデル別単価が計上額に反映される` |
| 7 | ゲートウェイ構成あり＋単価 0 で起動時に警告・単価ありなら出ない・ゲートウェイ無しなら出ない | `LlmPricingWiringTests`（TradeDecision）/ `LlmPricingStartupWarningTests`（Report）の起動ログ捕捉 |
| 8 | 描画された全 env 名がシェル識別子 | `helm.yml`「Assert container env names are shell identifiers」。**負の対照**: develop の values-local 描画で 20 件検出し失敗すること |
| 9 | 既存の単価アサーション（sonnet-5 入出力・fable-5 出力）が新名で通る | `helm.yml`「Assert values-local activates route-B features」 |
| 10 | 反映後、稼働で非 0 計上になる | **デプロイ後の確認（本 PR では未検証）**: trade-decision のログ `LLM 費用計上イベントを発行 … amount=` が **> 0**、cost_control_svc の `cost_entries` に非 0 の Amount が積まれる。起動ログに本件の WARNING が**出ない**こと。`/proc/1/environ` に `LlmPricing__PerModel__claude_sonnet_5__InputPer1kTokens` があること |

## 反映

trade-decision / report のイメージ再ビルド ＋ `scripts/k8s-local-deploy.sh`。OpenD は再起動しない。

## 残余リスク

- 他の設定点がモデル ID 等のハイフンを env 名へ入れても、helm.yml の検査が止める（chart 描画に限る）。
  compose・手動 `kubectl set env` は対象外。
- 警告は起動時 1 回だけ。ログを見なければ気付かない（例外で落とさないのは IADR-0055 の判断を維持するため）。
