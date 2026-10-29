---
title: IADR-0598 PoC の構成（values-local.yaml）に broker.tier: moomoo-sim を書き、CI の「実弾 OFF」は「paper」ではなく「取引環境が sim で、moomoo-live が描画で止まる」で検査する（IADR-0100 決定 6・7 を改める。opend.enabled は書かない）
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-12, FR-20, ADR-0072, ADR-0076, ADR-0067, IADR-0100, IADR-0111, IADR-0283, IADR-0539, IADR-0016, IADR-0056]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0076_poc-path-broker-tier-is-moomoo-sim.md (決定 1〜4・フォローアップ 1)
  - planning:projects/ai-stock-trading/07_adr/ADR-0072_poc-path-enables-decision-close-cancel-before-ast1214.md (決定 1・5)
related_specs:
  - ../specs/20261030_1500_poc-broker-tier-moomoo-sim.md
---

# IADR-0598: PoC の経路の発注先を構成に書き、「実弾 OFF」の検査の形を改める（#1500）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-30
- 決定者: 実装（Claude Code）。計画 ADR-0076（利用者裁定 2026-10-24・Accepted）のフォローアップ 1 の実装。

## 起点・関連

- 起票: [#1500](https://github.com/endazon/ai-stock-trading/issues/1500)
- 計画: ADR-0076（PoC の経路の発注先は moomoo-sim とし、構成に書く。ADR-0072 の補完）。ADR-0072（PoC の経路で判断由来の決済の取消を有効にする。取消は moomoo の構成でだけ働く）。
- 改める: IADR-0100 決定 6（「order-execution を values-local に含めない＝`Broker__Provider=paper`＝paper 固定」）と決定 7（CI の (b)「values-local の描画が Broker=paper・opend 不在」）。
- 前提（覆さない）: IADR-0111（発注先の階層。moomoo-live は描画で止まる）、IADR-0283（配備の手順の引き継ぎ）、IADR-0016・IADR-0056（SIMULATE 限定・本番の安全既定は paper）、IADR-0539（決済の取消）。

## コンテキスト

- `values-local.yaml` は発注先を書かず、チャートの既定（`broker.tier: ""` → 非推奨エイリアス `moomoo.enabled: false` → paper）に委ねていた（ADR-0076 実測 1）。
- 配備の手順（`k8s-local-deploy.sh`）は、`BROKER_TIER` が無ければ前回のリリースの値を `--set` で引き継ぐ。`--set` は `-f values-local.yaml` より優先する（ADR-0076 実測 3・IADR-0283）。稼働中の PoC は moomoo-sim で動いていた形跡がある（同 実測 4）。構成・CI・コメントは paper と述べ、稼働は moomoo-sim になり得た。
- CI（`helm.yml`）は values-local の描画の `Broker__Provider` が paper であることを「実弾 OFF 不変」として 2 か所で検査していた（同 実測 5）。
- OpenD の要素（Deployment・Service・NetworkPolicy 等）は `opend.enabled` のときだけ描かれ、`broker.tier` だけでは描かれない（同 実測 5）。

## 決定

### 決定 1: `values-local.yaml` に `broker.tier: moomoo-sim` を書く（IADR-0100 決定 6 の改定）

- PoC の経路の発注先は構成に書く。値は moomoo-sim（OpenD 経由の SIMULATE）。描画は order-execution に `Broker__Provider=moomoo`・`Broker__Environment=sim` を載せる。
- IADR-0100 決定 6 のうち「order-execution を values-local に含めない＝paper 固定」は改める（order-execution の extraEnv は #1339 で既に values-local に入っていた。ADR-0076 実測 2）。「実弾 OFF は不変」は保つ。moomoo-sim は SIMULATE で、moomoo-live は描画で止まる（IADR-0111）。
- 本番の構成（`values.yaml`）は変えない。既定は paper のまま（ADR-0076 決定 3）。ArgoCD は `values-local.yaml` を重ねない。
- `values-local.yaml` の冒頭・order-execution の節・実 DD の供給の注記（「moomoo SIMULATE は約定が台帳へ伝播しない」は #270 / IADR-0113 で解消済みの古い記述だった）を追随させる。

### 決定 2: `opend.enabled` は values-local に書かない（OpenD の要素の検査は現状のまま保つ）

- OpenD の常駐の有無は環境固有である。`opend.enabled` は従来どおり配備の手順の `OPEND_ENABLED` → 引き継ぎで扱う（values-local の opend 節の注記どおり）。
  - 書くと、OpenD の無いクラスタで配備するたびに OpenD の Deployment と PVC が作られる。逆に `false` を書くと、引き継ぎより弱い値として残るだけでなく、`OPEND_ENABLED` を渡し忘れた環境で OpenD の Deployment と PVC が消える（デバイスの信頼の再認証が要る。IADR-0283）。どちらの向きでも、環境固有の値を構成に焼くことになる。
  - ADR-0076 は OpenD の要素を PoC の構成の描画に許すかどうかを実装に委ねた（決定 2）。本 IADR は「許さない（values-local 単体の描画には現れない）」を選ぶ。
- したがって CI の「values-local の描画に `name: opend` が現れない」検査は、現状のまま保つ。moomoo-sim の描画は OpenD のホスト名（`opend`）を env の値として持つが、`name: opend` の要素は描かない（描画で確かめた）。
- ADR-0076 決定 2 の「paper の描画では OpenD の要素が現れない」は、既定の描画の検査（既存）と、values-local に `broker.tier=paper` を重ねた描画の検査（本 IADR で追加）で保つ。

### 決定 3: 「実弾 OFF」は「取引環境が sim で、moomoo-live が描画で止まる」で検査する（IADR-0100 決定 7 の改定）

`helm.yml` の 2 か所（values-local の有効化の検査と、Discord の ID 指定の検査）を次の形へ改める。

| 旧 | 新 |
| --- | --- |
| values-local の描画の `Broker__Provider` が paper | values-local の描画の `Broker__Provider` が moomoo（ADR-0076 決定 1） |
| — | `Broker__Environment` があり、値が sim だけ（実弾に届かない） |
| — | values-local に `broker.tier=moomoo-live` を重ねると描画で止まる |
| — | values-local に `broker.tier=paper` を重ねると paper に戻り、OpenD の要素が現れない（OpenD の無いクラスタの上書き） |
| values-local の描画に `name: opend` が無い | 変えない（決定 2） |

既定の描画（本番）の検査（`Broker__Provider` が paper・`Broker__Environment` を注入しない・OpenD の要素が無い）は変えない。

## PoC 配備への影響

- 配備の後は発注先が moomoo-sim になる。ただし `k8s-local-deploy.sh` は前回のリリースの値を `--set` で引き継ぎ、`--set` は values-local より優先する。前回のリリースに `broker.tier=paper` が `--set` で入っていれば paper のまま動く。前回が moomoo-sim か、値が無ければ moomoo-sim になる。確実にするには、配備のときに `BROKER_TIER=moomoo-sim` を明示する。
- 一度 values-local で配備すると、リリースの利用者の値に `broker.tier: moomoo-sim` が残り、以後は引き継がれる（`helm get values` は `-f` と `--set` の両方を返す）。
- OpenD が常駐し、ログイン済みであることが前提である。そうでない間は新規建てが拒否される（fail-closed。ADR-0076 実測 7）。OpenD の無いクラスタでは `BROKER_TIER=paper` を明示する。
- 配備前の確かめ方: `helm template ast deploy/helm/ai-stock-trading -f deploy/helm/ai-stock-trading/values-local.yaml` の order-execution の env が `Broker__Provider=moomoo`・`Broker__Environment=sim` であること。
- 配備後の確かめ（ADR-0076 フォローアップ 2。PoC 側）: 稼働中の order-execution の env が `Broker__Provider=moomoo`・`Broker__Environment=sim` であることを確かめて記録する。

## 結果

- 良い点: 構成・CI・コメントが発注先を moomoo-sim と読める。ADR-0072 の取消が構成どおりに働く条件が揃う。日足の取得も PoC の経路で取れる（ADR-0076 実測 8）。
- 悪い点: PoC の経路は OpenD に依る。OpenD の無いクラスタでは `BROKER_TIER=paper` の明示が要る。
- 残余リスク: 引き継ぎ（`--set`）は構成より優先するため、構成を読んだだけでは稼働の発注先は確定しない。稼働の確かめは ADR-0076 フォローアップ 2 に残る（本 IADR は引き継ぎの仕組みを改めない）。
