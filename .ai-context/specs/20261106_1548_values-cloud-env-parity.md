---
title: values-cloud の各サービスの env が既定（values.yaml）の描画から抜けていないことを CI で確かめる（理由付きの除外表）
type: spec
status: accepted
related_ids: [NFR, ADR-0006, IADR-0608]
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
---

# 仕様書: values-cloud の env の抜けの検査（#1548）

## 起点

- #1548（#1525 の後続。オーケストレーターの依頼）。
- `values-cloud.yaml` は `values.yaml` に直接重ねる（IADR-0608 決定 1）。Helm はリスト（`extraEnv`）を置き換えるので、
  `values.yaml` のサービスに後から足した env は、`values-cloud.yaml` の同じサービスへ写さない限りクラウドの描画から黙って落ちる。
- 経路 B（`values-local.yaml`）には同じ形の検査（helm.yml「Assert values-local drops no env from prod default」）が既にある。クラウドには無い。

## 決定

1. helm.yml に段「Assert values-cloud drops no env from prod default」を足す（values-cloud の段の直後）。
   - 既定の描画と、`values-cloud.yaml`＋検査用の digest（`ci/values-cloud-digests.fixture.yaml`）の描画から、Deployment ごとの env の名前を取り出す（既存の values-local の段と同じ awk）。
   - 既定の描画にあり、クラウドの描画に無い名前のうち、除外表に無いものがあれば落とす。
2. 除外表は段の中に置く（1 行 = `<Deployment 名> <env の名前> <理由>`）。理由の空の行は落とす。
3. 除外表を腐らせない:
   - 表の行がクラウドの描画に現れたら落とす（外す理由が無くなった）。
   - 表の行が既定の描画に無ければ落とす（名前が変わった・消えた）。
4. 抽出の破綻（0 件で緑）を、既定の描画の件数の下限（100）で止める（values-local の段と同じ）。
5. IADR は起こさない（オーケストレーターの指示）。IADR-0608 に追記する（決定 1 の重ね方の防御）。

## 母集合（規則 9）

- 対象: `templates/deployment.yaml` が描く全 Deployment の env（`extraEnv` と chart が描く env の両方。描画で比べるので、テンプレート側の env の抜けも捕まる）。
- 今の develop（ffbfc2f8）での実測: 既定 293 件・クラウド 352 件。既定にありクラウドに無いのは 16 件で、すべて次の 2 つの意図した外しである。
  - ニュースの RAG を持ち込まない（オーナーの決定 2026-11-05・IADR-0608 決定 4）: 情報収集の `KnowledgeBase__{Documents,Search}__BaseUrl`・`KnowledgeBase__Auth__{Authority,ClientId,ClientSecret}`（5）、
    報告書の `KnowledgeBase__Documents__BaseUrl`・`KnowledgeBase__Auth__{Authority,ClientId,ClientSecret}`（4）、
    取引判断の `KnowledgeBase__Search__BaseUrl`・`KnowledgeBase__SearchAuth__{Authority,ClientId,ClientSecret}`（4）。
  - Stage 0 の記録を無効にする（IADR-0608）: 報告書の `Stage0Recording__ApprovedEstimateJpy`（1）、取引判断の `Stage0Recording__{LlmTrainingCutoff,ApprovedEstimateJpy}`（2）。
- 除外: OpenD（`templates/opend.yaml`）と CronJob（`templates/cronjob.yaml`）。どちらも `extraEnv` を持たない。

## 受け入れ基準

- [x] 除外表が空なら、今の develop の values-cloud で段が落ち、上の 16 件を列挙する（赤）。
- [x] 除外表を入れると通る（緑）。
- [x] values-cloud から除外表に無い env を 1 つ消すと落ちる。
- [x] 除外表の名前を values-cloud に足すと落ちる（表が古い）。表の名前が既定の描画に無いと落ちる。理由が空の行は落ちる。
- [x] `/verify` が通る。
