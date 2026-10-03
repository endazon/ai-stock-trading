---
title: IADR-0489 east-west gRPC の呼び出し側の宣言は extraEnv（配列）ではなく services.<name>.grpcClients（マップ）で描き、宛先は呼び先の grpcPort から導出する。稼働クラスタでの実測は env を持たない一時 overlay を --reset-then-reuse-values で重ねて行う
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0284, IADR-0328, IADR-0331, IADR-0427, IADR-0445, IADR-0446, IADR-0449, IADR-0450, IADR-0439, IADR-0283, IADR-0058]
author: claude (Claude Code)
created: 2026-10-04
updated: 2026-10-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# IADR-0489: gRPC の呼び出し側の宣言はマップのキーで描き、計測は env を持たない一時 overlay で行う（#753）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-04
- 決定者: Claude Code（#753 の段 6 の着手条件〔稼働クラスタで宣言を入れ、段 1〜5 の全経路の h2c 往復を実測する〕を、PoC セッションの依頼で道具にした）

## 起点・関連

- 起票: [#753](https://github.com/endazon/ai-stock-trading/issues/753)（最後のコメント 2026-09-28: 段 6 の着手条件 2 つ）
- 関連する計画書 ID: NFR（`MSP/ADR-0029` 境界基準・`MSP/ADR-0075` 一括移行の義務）
- 関連する実装 ADR: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5 と 2026-09-27 追記（段 6 の範囲）・
  [IADR-0328](IADR-0328_east-west-grpc-foundation-stage0.md) 決定 3（h2c 専用ポートと helm の `grpcPort`）・IADR-0331 / 0427 / 0445 / 0446 / 0449 / 0450（段 1〜5 の消費側の宣言キー）・
  [IADR-0439](IADR-0439_helm-release-drift-read-only-check.md)（リリースとチャートの差の読み取り専用の検査）・IADR-0283（`--reuse-values` を使わない配備スクリプト）・IADR-0058（ON 派生の描画を CI で回す）
- 作業仕様書: [`.ai-context/specs/20261004_753_grpc-h2c-measurement-runbook.md`](../specs/20261004_753_grpc-h2c-measurement-runbook.md)
- 基点コミット: `origin/develop` `3daeb2a0`

## コンテキストと課題

- 段 1〜5 の消費側 13 宣言（`<提供側>:Grpc`）は、どれも「既定 REST、宣言したときだけ gRPC」である。values.yaml のコメントは有効化の方法として
  「この env 配列（`extraEnv`）へ `{ name: X__Grpc, value: http://…:8081 }` を足す」と案内していた。
- 🔴 **`extraEnv` は配列であり、helm は 2 つ目の values / `--set` の配列を丸ごと置き換える。** 稼働の配備は values.yaml に values-local.yaml を重ね、
  values-local は 7 サービスの `extraEnv` を既に丸ごと持っている。計測のために 3 つ目の values で `extraEnv` を書くと、values-local の配列を写し忘れた分が
  黙って消える。実測（helm v3.16.4）: report に `Audit__Grpc` の 1 行だけを書いた overlay で、report の env は 53 本 → 17 本（`ServiceAuth__*` を含む 37 本が消えた）。
  同じ形の事故（`Reconciliation__*` が消えた）が過去にある。
- 呼び先の `grpcPort` と呼び出し側の宛先は「同じ変更で揃える」と注意書きされているが、揃っていないことを止める仕組みが無かった（片方だけだと常に安全側既定へ倒れる）。

## 決定

### 決定 1 — 呼び出し側の宣言は `services.<name>.grpcClients.<提供側>: true`（マップ）で描く

- テンプレート（`templates/deployment.yaml`）が `extraEnv` の後に `<提供側>__Grpc` を描く。マップのキーは values を重ねても深くマージされるので、
  2 つ目・3 つ目の values で宣言を足しても**既存の env は消えない**。`false` / 未設定は何も描かない（`--set …=false` で 1 経路だけ外せる）。
- 未宣言なら何も描かない —— 既定描画・values-local 描画は追加前と**バイト等価**（`cmp` で実測）。

### 決定 2 — 宛先は呼び先の `grpcPort` から導出し、食い違いは描画で止める

- 提供側の名前 → 呼び先のサービスの表（`Configuration`→configuration・`RiskManagement`→risk-management・`Audit`→audit・`Reports`→report・
  `MarketMonitor`→market-monitor・`CostControl`→cost-control）をテンプレートに置き、`http://<呼び先>-service:<grpcPort>` を描く。
- 描画で止める: 呼び先に `grpcPort` が無い／表に無い提供側（綴り誤り）／真偽値でない値／`extraEnv` に同名の env がある（二重定義）。
- 基盤の LlmGateway（chart の外の呼び先）は表に入れない。`LlmGateway__Grpc` は従来どおり `extraEnv` で書く。

### 決定 3 — 稼働クラスタでの実測は、配列を持たない一時 overlay を `--reset-then-reuse-values` で重ねる

- overlay `values-grpc-measurement.yaml` は提供側 6 の `grpcPort: 8081` と呼び出し側 13 の `grpcClients` だけを持つ。既定の配備（values.yaml・values-local.yaml・
  `k8s-local-deploy.sh`・ArgoCD）からは参照しない。
- 適用は `helm upgrade ast … --reset-then-reuse-values -f values-grpc-measurement.yaml`（リリースの利用者の値〔`--set` で引き継いだ `broker.tier` 等を含む〕に重ねる）。
  適用前の差は IADR-0439 の `helm-release-drift.js --values <overlay>` で出す（同じ合成で描く）。切り戻しは `helm rollback`。
- 手順・合否・中止条件は Runbook（`docs/operations/grpc-h2c-measurement-runbook.md`）が単一情報源である。

### 決定 4 — CI は「overlay で env が 1 本も消えない」を描画で検める

`helm.yml` に 1 ステップ: 既定・values-local の 2 通りで overlay の有無の env 名の集合を Deployment ごとに比べ、消えた env が 0・足した env が gRPC の宣言だけ
（呼び出し側 13・提供側 6）・overlay が配列を持たない・異常系が描画で止まる、を検める。

## 検討した選択肢

| 案 | 採否 | 理由 |
| --- | --- | --- |
| A. overlay に values-local の `extraEnv` を全量写し、`*__Grpc` を足す | 棄却 | values-local の更新に追随し損ねた瞬間に env が消える（写しの腐り）。消えても描画は通るので気付けない |
| B. `--set services.X.extraEnv[N]…` で添字を足す | 棄却 | `--set` の配列も既存の配列を置き換える（添字の指定は追記ではない） |
| C. 消費側ごとに `<提供側>__Grpc` 専用のスカラー値を values に並べる | 棄却 | 宛先の URL を手で書くことになり、呼び先の `grpcPort` との食い違いを止められない |
| **D. マップのキー `grpcClients` ＋宛先の導出（採用）** | 採用 | 重ねても消えない・宛先の食い違いが構造上起きない・既定描画はバイト等価 |
| E. `k8s-local-deploy.sh` に追加の `-f` を受ける env を足す | 見送り | 画像の作り直しと restart を含む配備手順を、1 回限りの計測のために変えない。段 6 で既定にするときは values.yaml へ直接入れる |

## 統制と現在の実現手段

| 統制 | 実現手段 | 状態 |
| --- | --- | --- |
| 宣言の追加で既存の env が消えない | マップのキー（決定 1）＋ helm.yml の集合比較（決定 4） | 配備済み（CI） |
| 呼び先のポートと宛先の食い違いを作らない | テンプレートの導出と `fail`（決定 2） | 配備済み（描画時） |
| 適用で OpenD が作り直されない | `helm-release-drift.js` の 1 行目（終了コード 3 なら適用しない） | 手順（Runbook）。機械の閂ではない |
| 計測が終わったら REST の既定へ戻す | `helm rollback`（Runbook） | 手順。戻し忘れを検知する仕組みは無い（次の `k8s-local-deploy.sh` の実行で overlay は外れる） |

## 結果

- 良い影響: 計測の適用が 1 コマンドで、消える env が構造上 0。段 6 で gRPC を既定にするときも同じキーを values.yaml に書けばよい。
- 悪い影響・トレードオフ: 提供側の表がテンプレートと各輸送の構成キーの 2 箇所にある（新しい提供側を足すときは両方を直す。片方だけなら描画で「未知の提供側」として止まる）。
- フォローアップ:
  1. PoC セッションが Runbook で実測し、結果を #753 に記録する。
  2. 段 6 の着手時に、実測の結果をもとに values.yaml へ `grpcPort` / `grpcClients` を入れる（既定を gRPC にする）かを決める。
