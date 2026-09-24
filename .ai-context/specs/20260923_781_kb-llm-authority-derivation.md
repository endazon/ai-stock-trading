---
title: values-local の KnowledgeBase / LlmGateway の Auth__Authority リテラル 4 件を global.authAuthority からの導出へ移す（#776 の残余。#781）
type: spec
status: accepted
related_ids: [NFR-06, FR-08, ADR-0038, IADR-0093, IADR-0283, IADR-0323, IADR-0324]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-24
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0038_linked-deploy-auth-realm-is-the-platform-realm.md
---

# 仕様書: KB / LLM の Auth__Authority を `global.authAuthority` から導出する（#781）

## 起点

- **[#781](https://github.com/endazon/ai-stock-trading/issues/781)**（chore）。[#776](https://github.com/endazon/ai-stock-trading/issues/776)（PR #780）が
  [IADR-0324](../adr/IADR-0324_msp-linked-deploy-single-auth-realm.md) の 2026-09-11 追記へ**明示的に残した負債**である
  ——「`values-local.yaml` のリテラル 4 件はテンプレート導出ではないため `global.authAuthority` に追随しない。
  **本検査はそのずれを赤で捕まえるが、ずれを作らない構造にはしていない**（別 issue）」。
- 起点 ID: **NFR-06**（配備・運用）／計画 **ADR-0038 決定 2**（連結配備の認証レルムは基盤レルム・全経路が単一レルムを指す）。
- 同型の先行: `TOKEN_ENDPOINT`（[#456](https://github.com/endazon/ai-stock-trading/issues/456)・CronJob）／
  `ServiceAuth__TokenEndpoint`（[#736](https://github.com/endazon/ai-stock-trading/issues/736)・s2s 発信者）。
  **壊れ方は 3 回とも同じ**で、「導出元の設定が注入されない経路が 1 つ残り、そこだけ別レルムへ倒れる」である。

## 🔴 起票内容の再現（実測）

```
$ helm template ast <chart> -f <chart>/values-local.yaml \
    --set global.authAuthority=http://kc:8080/realms/zzsentinel | grep -o 'realms/[a-z0-9-]*' | sort | uniq -c
      4 realms/platform      ← 追随しないリテラル 4 件
     13 realms/zzsentinel
```

4 件の内訳（`values-local.yaml`）:

| サービス | env |
| --- | --- |
| information-collection | `KnowledgeBase__Auth__Authority` |
| report | `LlmGateway__Auth__Authority` / `KnowledgeBase__Auth__Authority` |
| trade-decision | `LlmGateway__Auth__Authority` |

## 対象範囲

- 対象: `deploy/helm/ai-stock-trading/templates/deployment.yaml`（導出の追加）、
  `deploy/helm/ai-stock-trading/values-local.yaml`（4 リテラルの撤去）、
  `.github/workflows/helm.yml`（陰性対照の 1 面追加）、`.ai-context/adr/IADR-0324…md`（日付つき追記）＋索引行。
- 対象外: **`values.yaml`（本番描画）は 1 バイトも変えない**。アプリのコード。`realm-export.json`。
  基盤（MSP）側のレルム宣言。**クラスタへの適用は行わない**（稼働中の PoC クラスタがあるため。下の「運用者の取り込み手順」）。

## 直し方

### 導出の条件（🔴 ここが設計判断）

起票は「MSP 連結（`global.authAuthority` が MSP レルムを指す）のときだけ値を出す」と書くが、**レルム名で条件を書くと
陰性対照が成立しない** —— `--set global.authAuthority=…/realms/x` としたときに条件が外れて 4 件が「消える」ため、
「4 つも追随する」ことを確かめられない（検査は空振りで緑になる）。

そこで条件を**連携が実際に配線されているか**に採る。KB / LLM ゲートウェイはいずれも基盤（MSP）のサービスであり、
**その BaseUrl が空＝未構成のときは Authority も意味を持たない**（本番既定はまさにその状態である）。

| 出す env | 条件（同一サービスの `extraEnv` を見る） |
| --- | --- |
| `KnowledgeBase__Auth__Authority` | `KnowledgeBase__Documents__BaseUrl` **または** `KnowledgeBase__Search__BaseUrl` が非空 |
| `LlmGateway__Auth__Authority` | `LlmGateway__BaseUrl` が非空 |

値は `$g.authAuthority`（`Auth__Authority` / `ServiceAuth__TokenEndpoint` / CronJob の `TOKEN_ENDPOINT` /
Discord OwnerAuth と**同一ソース**）。実装は既存の `$envOverrides`（#245 / IADR-0102）へ載せる
——env を**追加せず** `extraEnv` の**値を上書き**するので、同名 env の二重定義も並び順の変化も起きない。

この条件は現況と 1 件も食い違わない（実測）:

| サービス | Documents | Search | LlmGateway | 出る env |
| --- | --- | --- | --- | --- |
| information-collection（local） | 設定 | 空 | — | KB ✓ |
| report（local） | 設定 | — | 設定 | KB ✓ / LLM ✓ |
| trade-decision（local） | — | **空** | 設定 | LLM ✓（KB は**出さない**＝現況どおり空） |
| 全サービス（本番既定） | 空 | 空 | 空 | **1 件も出ない**＝描画はバイト等価 |

### values-local からリテラルを消す

4 件は `value: ""` へ落とす（**キー自体は残す**）。`helm.yml` の
`Assert values-local drops no env from prod default` は本番描画の env 名がすべて values-local 描画にも
在ることを要求するため、キーを消すと赤くなる。**値だけを消してテンプレートに埋めさせる**のが正しい形である。

### 陰性対照を CI に足す

`helm.yml` の `Assert every rendered realm matches global.authAuthority (#776)` は 4 面
（既定 / values-local / CronJob 込み / 既定＋`--set`）に掛かっているが、**values-local と `--set` を
組み合わせた面が無い** —— まさに #781 が生きていた隙間である。その 1 面を足す。

🔴 **それだけでは導出そのものを守れない**（PR #927 の監査で実測）。この検査は「違うレルムを指す経路が無いこと」を
見るだけなので、値が空の経路は `/realms/` を含まず母集合から**黙って落ちる**（件数下限 10 も割らない）。導出の 2 行を
消しても、導出をレルム名で条件付けても全面が緑だった。そこで BaseUrl を配線する 3 面（`values-local` / 全フラグ ON /
`values-local`＋`--set`）には、**env 名が `KnowledgeBase__Auth__Authority` / `LlmGateway__Auth__Authority` で値が非空の
行がちょうど 4 件**であることを追加で要求する（下限ではなく正確な件数。多い側＝未配線の trade-decision の KB Authority
まで埋まる変異も落とす）。

### 記録

[IADR-0324](../adr/IADR-0324_msp-linked-deploy-single-auth-realm.md) へ日付つき追記（`［2026-09-23 追記 / #781］`）。
**新しい IADR は起こさない** —— 決めているのは同 IADR 決定 2（1 値からの導出）の適用範囲を残り 4 経路へ広げることであり、
#456 / #736 の是正も同じく本 IADR の追記として記録されている（先例に倣う）。索引行にも追記を足す
（既存の追記を 1 つも落とさないこと）。

## 受け入れ基準

- [x] AC1: `helm template ast <chart>`（本番既定）が変更前と**バイト等価**（`diff` 無差分）。
      `--set global.authAuthority=…/realms/zzsentinel` を足した描画も**バイト等価**。
- [x] AC2: `helm template ast <chart> -f values-local.yaml` が変更前と**バイト等価**
      （現に配備されている経路Bの構成が 1 バイトも変わらない）。
- [x] AC3: CronJob / OpenD / moomoo を全部 ON にした values-local 描画も変更前と**バイト等価**。
- [x] AC4: `-f values-local.yaml --set global.authAuthority=…/realms/zzsentinel` の描画は
      **17 件すべてが `realms/zzsentinel`**（変更前は `platform` 4 件 ＋ `zzsentinel` 13 件）。
- [x] AC5: `helm lint`（既定・values-local とも 0 failed）。
- [x] AC6: `check-adr-index-addendum-loss.js`（追記 90 件すべて健在）/ `check-doc-links.js` /
      `check-trace-blocks.js` / `gen-knowledge-graph.js --check` / `check-cross-repo-refs.js` /
      `check-plan-id-qualification.js` / `check-action-versions.js` / `check-workflow-job-refs.js` /
      `check-ai-workflow-config.js` / `validate-runtime-scaffold.js` がすべて緑。
- [x] AC7: `bash scripts/k8s-local-deploy.test.sh` が **128 passed / 0 failed**。
- [x] AC8: `helm.yml` の realm 検査 5 面をローカルで同じ論理で実走し全面緑。**新しい 5 面目を
      変更前の描画へ当てると 4 経路を名指しして落ちる**（＝番人であることの実測）。
- [x] AC9: 本番描画の env 名 261 件が values-local 描画からも 1 件も失われていない
      （`Assert values-local drops no env from prod default` 相当）。
- [x] AC10: realm 検査の配線 3 面が「非空の KB / LLM `Auth__Authority` ＝ちょうど 4 件」を要求し、PR head で緑・
      (b) 導出 2 行を削除した chart で赤（values-local 面が 0 件）・(c) 導出を `contains "/realms/platform"` で
      条件付けた chart で赤（`values-local`＋`--set` 面が 0 件。条件をレルム名だけにした形は values-local 面が 5 件で赤）。
      変更前の検査はこの 3 変異すべてで緑だった。

## 運用者の取り込み手順（🔴 適用はしない）

**本 PR はリポジトリ上の変更だけで、クラスタへは何も当てない。** 稼働中の経路B クラスタが本変更を取り込むには
運用者が通常どおり `scripts/k8s-local-deploy.sh`（手順 [4/5] が `helm upgrade -f values-local.yaml`）を実行する。
AC2 / AC3 が示すとおり**描画はバイト等価**なので、`helm upgrade` 自体は Deployment の spec を変えない（素の
`helm upgrade` だけなら Pod の再作成も再起動も起きない）。🔴 **ただし同スクリプトは手順 [5/5] で OpenD（`opend`）を
除く全 Deployment へ無条件に `kubectl rollout restart` を打つ**（#673。イメージ更新を Pod へ届けるため）ので、
スクリプト経由の取り込みでは描画の等価性と無関係に opend 以外の Pod は再起動する。取り込まなくても現状の挙動は変わらない。

## 未決事項

- 「BaseUrl を設定したのに Authority を意図的に空にしたい」構成は、本変更後は作れない（導出が埋める）。
  現況にそのような構成は無く、KB / LLM はいずれも s2s 認証必須（匿名は 401）なので実害は想定しない。
  必要になったら明示の opt-out を足す。
- 基盤レルムと AST 専用レルムの写しのずれの突合は基盤側の受け皿（ADR-0038 フォローアップ 2）。本 PR の対象外。
