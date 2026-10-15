---
title: Recreate へ移る最初の helm upgrade が既存の rollingUpdate で拒否される —— 起動器が upgrade の前に strategy を置き換える（#1351）
type: spec
status: done
related_ids: [FR-15, ADR-0033, IADR-0537]
author: claude (Claude Code)
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 5)
---

# 仕様書: Recreate への移行で残る rollingUpdate を起動器が外す（#1351）

> 本仕様書は実装着手前に作成した（着手 2026-10-15）。基点は AST `origin/develop` `61b29b8a`。
> ファイル名の `rolling-update-null` は当初の直し方（描画に `rollingUpdate: null`）の名残である。下の実測で
> その直し方は効かないと分かり、起動器での移行へ切り替えた（オーケストレーター裁定 2026-10-15）。

## 起点

- #1351: #1344（IADR-0537 決定5）で `stage0Recording.persistence.enabled=true` のとき trade-decision を `strategy.type: Recreate` にした。
  PoC で AST `61b29b8a` の helm upgrade が rev 46 で失敗し、rev 45 のまま止まった。

  ```
  Deployment.apps "trade-decision-service" is invalid: spec.strategy.rollingUpdate: Forbidden: may not be specified when strategy `type` is 'Recreate'
  ```

- 🔴 **issue 本文の原因（helm の 3-way パッチが描画に無いフィールドを消さない）は誤り。** 原因は **helm 4 の server-side apply（SSA）** である
  （下の実測）。RollingUpdate で作った Deployment の `rollingUpdate` は API サーバが既定で補った値で、field manager が付かない。
  SSA は自分の所有でないフィールドを消さないため、`type: Recreate` と並んで拒否される。helm 3 の client-side 3-way パッチでは起きない。

## 実測（envtest の kube-apiserver v1.31.0 ＋ etcd。docker の daemon が無いため k3d / kind の代わり）

`values-local.yaml`（＋ `opend.enabled=true`）で `5e2b5bef^`（#1344 の前＝RollingUpdate）を install し、upgrade した。

| helm / 方式 | develop `61b29b8a`（無修正） | 描画に `rollingUpdate: null` を足した版 | 起動器の移行の段 → upgrade |
| --- | --- | --- | --- |
| v3.16.4（client-side 3-way パッチ） | **通る**（`{"type":"Recreate"}` へ。retainKeys で消える） | — | — |
| v4.2.1 既定（SSA。CI と同版） | **Forbidden を再現** | **Forbidden のまま**（効かない） | **通る** |
| v4.2.1 `--server-side=false` | 通る | 通る | — |
| v4.2.1・オーナーが手で patch 済み（PoC の当座） | 通る | 通る | 通る（移行の段は「対象なし」） |
| v4.2.1・新規環境 | — | 通る | 通る（「対象なし」） |
| 同じ流れの再実行 | — | — | 「対象なし」で通る（冪等） |

- 稼働中の managedFields は `helm / Apply` の 1 件だけで、`f:strategy` を持たない（補った値は誰の所有でもない）。
- `kubectl apply --server-side --field-manager=helm --dry-run=server` に `rollingUpdate: null` を与えても Forbidden のまま。
- 再現の手順は scratchpad の使い捨てスクリプト（`launcher-repro.sh`）で、起動器を `AST_DEPLOY_LIB=1` で読み込み、
  `ast_migrate_recreate_strategy` → `helm upgrade --install`（同じ値の配列）を実クラスタ API に対して走らせた。
  起動器全体（イメージの build・k3d import）は docker が無いため走らせていない。

## 母集合（規則 9: `Recreate` / `strategy` / `rollingUpdate` / `helm upgrade` で走査）

| 箇所 | 結果 |
| --- | --- |
| `deploy/helm/ai-stock-trading/templates/deployment.yaml:125` | trade-decision。`stage0Recording.persistence.enabled` のときだけ Recreate。**移行の対象** |
| `deploy/helm/ai-stock-trading/templates/opend.yaml:68` | OpenD。導入時から常に Recreate。既存は RollingUpdate だった版が無いので通常は対象外だが、起動器は描画から判定するので同じ扱いになる |
| `deploy/opend/k8s/opend.yaml:28` | dev の生 manifest（`kubectl apply`）。最初から Recreate。対象外 |
| `values.yaml` / `values-local.yaml` / `README.md` / `pvc-stage0-recording.yaml` | 注記と `Backtest__Stage0__Strategy`（無関係） |
| `.github/workflows/helm.yml:1290, 1346–1362` | `type: Recreate` の描画の有無を検査。`rollingUpdate` は見ていない。変更不要 |
| `rollingUpdate`（全追跡ファイル） | 0 件 |
| `helm upgrade` を呼ぶ起動器 | `scripts/k8s-local-deploy.sh` だけ（`k8s-local-up.sh` は本リポに無い）。`deploy/opend/README.md:272` は手打ちの例で、OpenD は最初から Recreate |

## 設計

| 対象 | 変更 |
| --- | --- |
| `scripts/k8s-local-deploy.sh` | `ast_recreate_deployments`（描画から Recreate の Deployment 名を挙げる）と `ast_migrate_recreate_strategy` を足す。upgrade と同じ値（`AST_HELM_VALUE_ARGS`）で `helm template` し、稼働中が `type != Recreate` かつ `rollingUpdate` を持つものだけ `kubectl patch --type=json` で `/spec/strategy` を `{"type":"Recreate"}` に置き換え、1 行出す。描画・取得・置き換えの失敗は止める（fail-closed）。helm upgrade の直前で呼ぶ |
| `scripts/k8s-local-deploy.test.sh` | kubectl / helm のスタブに `get deployment <name>`・`patch deployment`・`template` を足し、T-1351-01〜07 |
| `helm.yml` | 「Assert launcher detects Recreate deployments from the real render (#1351)」: `ast_recreate_deployments` を実際の描画へ当て、既定＝なし／values-local＝trade-decision／values-local＋opend＝opend と trade-decision を固定する（AI レビュー 🟡。描画の形が変わったとき、移行の段が黙って「対象なし」に倒れるのを防ぐ）。起動の paths に `scripts/k8s-local-deploy.sh` を足す |
| chart `README.md` | 「Recreate へ移る最初の upgrade」: 症状・原因（SSA）・起動器の自動の直し・Pod の作り直し・手での `kubectl patch` |
| `docs/tests/FR-15_backtest-tests.md` | T-15-139 と変異の行 |

- 描画に `rollingUpdate: null` は入れない（SSA では効かず、直したように読めて誤解を招く）。
- 新しい IADR は作らない。決定（Recreate にする）は IADR-0537 決定5 のままで、配備の移行手順を足すだけである。

## 受け入れ基準

1. 要る場合だけ patch する（描画が Recreate・稼働中が Recreate でなく rollingUpdate を持つ）。操作したら 1 行出す。Pod の作り直しを明記する。
2. 不要（すでに Recreate・未作成・描画が Recreate でない）なら何もしない。
3. 描画・取得・置き換えの失敗で止まる。
4. 起動器は upgrade と同じ値で移行の段を呼び、その後に upgrade する。
5. helm v4.2.1 の既定（SSA）で、rev（#1344 の前）→ 修正後の起動器の流れの upgrade が通る。

## 実測（変異）

`bash scripts/k8s-local-deploy.test.sh`（196 passed）に対し:

| 変異 | 結果 |
| --- | --- |
| 判定を外し常に置き換える | 4 件が赤（T-1351-01 ほか） |
| 置き換えの失敗を無視する | 4 件が赤 |
| 取得の失敗を無視する | 2 件が赤（T-1351-04） |
| 描画の失敗を無視する | 2 件が赤（T-1351-06） |
| 起動器本体から呼び出しを外す | 1 件が赤（T-1351-07） |
| 描画の判定を盲目にする | 7 件が赤 |
| `/spec/strategy/type` だけを置き換える（rollingUpdate が残る） | 1 件が赤（T-1351-01） |

`helm.yml` の新しい step（ローカルで helm v4.2.1）に対し、awk を壊す変異 5 種（strategy の字下げ・type の字下げ・kind を拾わない・
name を拾わない・文書の区切りを見ない）はいずれも「values-local: 期待 [trade-decision-service] に対し []」で赤。
