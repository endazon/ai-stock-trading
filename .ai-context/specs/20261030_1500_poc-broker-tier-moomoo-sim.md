---
title: PoC の構成（values-local.yaml）に broker.tier: moomoo-sim を書き、CI の「実弾 OFF」の検査を「取引環境が sim で、moomoo-live が描画で止まる」へ改める（#1500）
type: spec
status: done
related_ids: [FR-05, FR-12, FR-20, ADR-0072, ADR-0076, IADR-0100, IADR-0111, IADR-0283, IADR-0598]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0076_poc-path-broker-tier-is-moomoo-sim.md (決定 1〜4・フォローアップ 1)
---

# 仕様書: PoC の経路の発注先を moomoo-sim と構成に書く（#1500）

> 本仕様書は実装着手前に作成した（着手 2026-10-30）。基点は AST `origin/develop` `45a46525`。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR-0076（Accepted。利用者裁定 2026-10-24）決定 1〜4・フォローアップ 1。ADR-0072 決定 1 の補完。
- 起票: #1500
- 新規 IADR: IADR-0598（IADR-0100 決定 6・7 を改める）

## 対象範囲

- 対象:
  - `deploy/helm/ai-stock-trading/values-local.yaml`: `broker.tier: moomoo-sim` を足す。冒頭（旧 18-19 行目）・order-execution の節（旧 141 行目）・実 DD の供給の注記（旧 106-107 行目。#270 で解消済みの古い記述）を改める。
  - `.github/workflows/helm.yml`: values-local の描画の「paper」の検査 2 か所（旧 741-742・1198-1199 行目）を、moomoo＋sim だけ＋moomoo-live が描画で止まる＋paper の上書きで OpenD の要素が現れない、へ改める。`name: opend` の禁止は保つ。
  - `deploy/helm/ai-stock-trading/README.md`（259-260・274 行目）と `scripts/k8s-local-deploy.sh`（297 行目のコメント）の追随。
  - IADR-0598、IADR の索引。
- 対象外:
  - `values.yaml`（本番の既定は paper のまま。ADR-0076 決定 3）、templates、`deploy/argocd/*`。
  - `opend.enabled` を values-local に書くこと（IADR-0598 決定 2）。
  - 配備の手順の引き継ぎの仕組み（ADR-0076 は改めない）。
  - 稼働クラスタでの確認（ADR-0076 フォローアップ 2。配備の後に PoC 側で行う）。

## 母集合（規則 9・10。着手前に引いた）

- 「values-local / 経路B は paper」と述べる箇所: `git grep -n paper -- . ':!.ai-context' ':!CHANGELOG.md' ':!backend' ':!frontend'` を `values-local|経路 ?B|ローカル|local|PoC` で絞った（行フィルタを使ったので、次の 2 軸目で補った）→ 本 PR で直す前の時点で次の箇所。
  - `values-local.yaml:18-19`・`:141`（直す）。
  - `deploy/helm/ai-stock-trading/README.md:259`・`:274`（直す）。
  - `.env.example:109`・`docs/how-to/local-run.md:19`: プロセス直起動（.env）の既定で、helm の経路ではない（除外）。
  - `docs/operations/live-trading-cutover-runbook.md:199`: 本番の安全既定の記述（除外）。
  - `docs/operations/operations.md:40`・`docs/tests/FR-10_risk-controls-tests.md:6139`: 発注先に依らない記述（除外）。
  - `scripts/k8s-local-deploy.sh:42`・`:94`: 前者は値の列挙、後者は #626 以前の挙動の説明で、いまも正しい（除外）。`:297`「chart 既定＝paper に委ねる」は、プロファイルの値が先に効くので直す。
  - `scripts/k8s-local-deploy.test.sh:404`: 引き継ぎの仕組みの説明（除外）。
- 2 軸目（`git grep -n -E 'BROKER_TIER|broker\.tier'` を docs・deploy・scripts・*.md で）: 上と同じ箇所のほかは、チャートの既定の説明（README の表・runbook の階層の表）と手順の例で、values-local の発注先を述べていない（除外）。
- 3 軸目（`.github` の `paper`）: `helm.yml` の既定（本番）の描画の検査（386・451-453・507-536 行目）は ADR-0076 決定 3 のまま正しい（変えない）。values-local の描画の検査は上の 2 か所だけ。
- values-local の描画を読む他の消費者（`scripts/helm-release-drift.js`、`scripts/scripts*.test.js`、backend の試験 7 ファイル）: 発注先を読んでいない（除外）。
- 新たに誤りになる自分の記述（規則 10）: values-local の実 DD の注記「経路B の既定ブローカは paper」（直す）。IADR-0100 の本文は凍結記録のため直さず、IADR-0598 が改める（索引の行で示す）。

## 受け入れ基準

1. values-local の描画の order-execution が `Broker__Provider=moomoo`・`Broker__Environment=sim` になる。
2. values-local の描画の `Broker__Environment` は sim だけ。values-local に `broker.tier=moomoo-live` を重ねると描画で止まる。
3. values-local に `broker.tier=paper` を重ねると paper に戻り、OpenD の要素が現れない。
4. values-local 単体の描画に OpenD の要素（`name: opend`）が現れない（`opend.enabled` を書かない）。
5. 既定（本番）の描画は変わらない（paper・`Broker__Environment` なし・OpenD の要素なし）。
6. `helm.yml` の全ステップが手元で通る。`k8s-local-deploy.test.sh` が全件通る。

## 検証

- `helm.yml` の chart ジョブの全 `run:` を、CI と同じ helm v4.2.1 と `bash -e` で手元で実行する（`CHART=deploy/helm/ai-stock-trading`）。
- red → green: values-local の `broker.tier` を空へ戻す変異で、改めた 2 つの検査が赤になる。戻すと全ステップ緑。
- `bash scripts/k8s-local-deploy.test.sh`、`node scripts/scripts.test.js`、文書系の検査。

## PoC 配備への影響

IADR-0598 §PoC 配備への影響 のとおり（発注先は moomoo-sim になる。前回のリリースの値の引き継ぎとの関係。OpenD が前提で、無ければ新規建ては拒否される。配備前の描画の確かめ方）。
