---
title: S3（#821）監査の残論点 3・5 —— helm の調整値で数値の 0 を空と取り違えない・発火したが約定しない StopLimit の検知不能を受容した制約として IADR-0347 へ記録する
type: spec
status: accepted
related_ids: [FR-10, FR-12, ADR-0040, IADR-0347, IADR-0405, IADR-0060, IADR-0058]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
---

# 仕様書: S3 監査の残論点 3・5 の決着（#842）

## 起点

- [#842](https://github.com/endazon/ai-stock-trading/issues/842)（#838 ＝ S3 の監査で挙がった非ブロッキング 5 件）。
  論点 1・2・4 は PR #968（`20260925_842_s3-audit-followups`・IADR-0405）で是正済み。**本作業は残る 3・5 を扱う。**
- 本作業の割り当て: ブランチ `fix/FR-10-842-s3-audit-followups`（#968 と同名。#968 のリモートブランチはマージ後に
  削除済みで、ローカルに残っていた旧ブランチは `archive/842-pr968-head` へ改名して退避した）・IADR-0417（使わなかった）・
  テスト ID `T-10-950`〜`T-10-969`（使わなかった。下の「検査」）。
- 衝突回避: #973（`OrderFillPoller`・`ProtectiveStopGuard`）・#978（監査の免除打ち消し）・open PR #977
  （`ProtectiveStopNetting`・`BrokerPositionSnapshotService`）のファイルには**触れない**。

## 🔴 前提の変化: #809 の裁定（2026-09-24）

#842 のトリアージ（2026-09-23）は 3・5 を「S3 の受理可否の実測待ち（#809 の裁定依存）」とした。
その #809 に**オーナーの裁定が出て閉じられている**:

> **S3 は測らず、S1（ソフトウェア逆指値）で進める。** … S3 の実測は将来の課題として残す
> （再開するときは本 issue を参照して新しく起票する）。

#809 のトリアージが示した 2 択のうち **(A)「S3 の実機実測を行わない。#842 の論点 3・5 を『S3 を採らない前提の
受容した制約』として IADR-0347 へ記録する」** に当たる。したがって 3・5 は「実測を待つ」ではなく、
**受容した制約として記録する（3）／実測に依らず直せる部分を直す（5）** で決着できる。

## 🔴 実測（`origin/develop` = `a0d600b7`）

`git rev-parse --is-shallow-repository` → `false`。

| # | 論点 | 現物（develop） | 判定 |
| ---: | --- | --- | --- |
| 1 | 拒否された保護レグに実在しない GUID | `AlternativeProtectiveOrderPlacement.BrokerOrderId`（PR #968）。拒否では null | **解消済み**（#968） |
| 2 | 台帳の自由記述欄へ接続先・任意長の例外メッセージ | `AuditService/Domain/AuditFreeText`（PR #968）。500 文字・接続先の伏せ字 | **解消済み**（#968） |
| 3 | 発火したが約定しない StopLimit をガードが検知できない | `GuardProtectiveStops/ProtectiveStopGuard.cs` の Pending 分岐が残量ありなら `StillActive`。滞留時間の判定は無い | **残**（下で決着） |
| 4 | 作業仕様書が不在のテスト名を 3 件引く | `20260918_821_…` 末尾の `［2026-09-25 追記 / #842］`（PR #968） | **解消済み**（#968） |
| 5 | helm の `stopLimitOffsetRatio` に数値 0 → 無言で既定 1% | `templates/deployment.yaml` の `{{- with $.Values.moomoo.stopLimitOffsetRatio }}`。helm v4.2.1 で再現（下表） | **残**（下で是正） |

項目 5 の再現（`helm template ast deploy/helm/ai-stock-trading --set broker.tier=moomoo-sim <追加>`。是正前）:

| 追加の引数 | 描画された env |
| --- | --- |
| `--set moomoo.stopLimitOffsetRatio=0` | **無し**（アダプタ既定 1% へ黙って倒れる） |
| `--set-string moomoo.stopLimitOffsetRatio=0` | `Broker__Moomoo__StopLimitOffsetRatio: "0"` |
| `--set moomoo.stopLimitOffsetRatio=0.02` | `"0.02"` |
| `--set moomoo.opend.replyTimeoutSeconds=0` | **無し**（アダプタ既定 15 秒へ黙って倒れる） |
| `--set moomoo.opend.replyTimeoutSeconds=30` | `"30"` |

## 分類（片付ける／見送る）

| # | 扱い | 理由 |
| ---: | --- | --- |
| 1・2・4 | 片付け済み（#968） | 上表 |
| 3 | **片付ける（受容した制約として記録）** | #809 の裁定で S3 は実測しない（＝SIMULATE の常用手法にしない）。検知の設計（発火の判定・滞留の閾値・検知後の処置）は S3 が受理される前提でしか意味を持たず、今作ると使われない分岐が残る（IADR-0347 の既存の残余リスクと同じ理由）。**S3 を再開する前提条件として IADR-0347 へ明記する**。検知の実装は見送る——再開時の新 issue の射程 |
| 5 | **片付ける（是正）** | helm が運用者の値を書き換えるのは S3 の受理可否と無関係な欠陥である。空（`""`）と未設定（null）だけを「未指定」とし、0 を含むそれ以外は値として注入する |

## 🔴 母集合（規則 9〜11。走査した語と結果・除外理由）

- **項目 5（誤りの側の文字列＝数値を取り得る値への `with` / `default`）**:
  `grep -rn "with \|default " deploy/helm/ --include=*.yaml --include=*.tpl` を 1 件ずつ見た。
  - **採る（3 件）**: `deployment.yaml` の moomoo 調整値 `moomoo.opend.replyTimeoutSeconds`・`moomoo.alternativeStopOrderType`・
    `moomoo.stopLimitOffsetRatio`。いずれも「空＝アダプタ既定・それ以外はアプリが検証し不正なら起動時に停止」の契約で、
    `with` が 0（や `false`）を空と同じに扱うため**アプリの検証まで届かない**。
    - `replyTimeoutSeconds=0`: アプリは 1〜600 の範囲外として起動時に停止する（`MoomooBrokerOptions.ParseReplyTimeout`）
      はずが、helm が 15 秒へ黙って倒していた。**chart 側 README・values.yaml の「範囲外は起動時に停止」が偽だった**。
    - `alternativeStopOrderType=0`（`--set` で数値になる）: アプリは未知の値として停止するはずが、既定の stoplimit へ倒れていた。
    - `stopLimitOffsetRatio=0`: アプリは 0〜0.1 を受理する（0 は範囲内。`EnsureBeyondTrigger` が指値を発火価格から
      最低 1 刻み離す＝#844）。helm は 1% へ黙って倒していた。
  - 除外: `opend.yaml` の `with $o.podSecurityContext / nodeSelector / affinity / tolerations / securityContext`
    （マップ・リスト。0 を取らない）／`deployment.yaml` の `with $g.configVersion`（版の文字列。空＝null の契約で 0 は意味を持たない）／
    `with $bot.guildId / channelId / allowedUserIds / userMapping`（Discord の ID・文字列。0 は有効な ID ではない）／
    `$svc.tag | default $g.image.tag`・`index $envOverrides $e.name | default $e.value`（文字列の上書き。0 を取らない）／
    `(.Values.x | default dict)`（マップ）。
- **規則 10（本変更で新たに誤りになる自分の記述）**: `values.yaml` / chart README の「空＝アダプタ既定」は本変更後も真。
  「範囲外は起動時に停止」は本変更で**真になる側**（是正前は 0 に限って偽）。`docs/functional/FR-10_risk-controls.md` の
  「0〜10%」「発火価格と同値に置かない」は、0 が届くようになっても `EnsureBeyondTrigger` が 1 刻み離すため偽にならない。
  `docs/operations/live-trading-cutover-runbook.md` の `ReplyTimeoutSeconds`（1〜600・範囲外は起動時停止）はアプリの記述で不変。
  IADR-0347 の索引行と本文の残余リスクは、本作業で**追記**する（本文は書き換えない）。
- **項目 3（誤りの側の文字列＝「S3 の受理を実測してから」の保留）**: `git grep -n "実測してから\|受理を実測\|S3 が受理"` →
  IADR-0347（残余リスク 3 か所）・IADR-0405（射程外の注記）・仕様書 2 件（凍結）。**live な権威文書は IADR-0347 だけ**であり、
  そこへ日付つき追記で #809 の裁定と再開の前提条件を足す。IADR-0405 は「本 IADR の射程外」と書くだけで偽にならない（除外）。
- **規則 11（窓）**: 本作業は時間差を扱わない（項目 3 の滞留判定は実装しない）。該当なし。

## 射程

1. **項目 5**: `deploy/helm/ai-stock-trading/templates/deployment.yaml` の moomoo 調整値 3 件を、
   `kindIs "invalid"`（未設定・null）または `toString` が空のときだけ省き、それ以外（数値の 0・`false` を含む）は
   `quote` して注入する形へ改める。既定描画（値はすべて `""`）は是正前とバイト等価に保つ。
   `values.yaml` のコメントと chart README に「数値の 0 も値として注入する（空と区別する）」を足す。
2. **項目 5 の検査**: `.github/workflows/helm.yml` に描画アサートを 1 ステップ足す（下の「検査」）。
3. **項目 3**: IADR-0347 に `［2026-09-25 追記 / #842］` を足し、索引行にも同じ追記を足す。

### 射程外

- 項目 3 の検知の実装（発火の判定・滞留の閾値・検知後の処置）。S3 を再開するときの新 issue で扱う（IADR-0347 追記に前提条件として書く）。
- `stopLimitOffsetRatio=0` をアプリの起動時検証で拒むか（下限を開区間にするか）。現行の契約は 0 を受理し、#844 で
  1 刻みの離隔を保証している。変えるのは S3 の保護の形の設計判断であり、S3 を採らない裁定の下で動かす理由が無い。

## 決めたこと

新しい IADR は作らない。項目 3・5 はいずれも IADR-0347（S3 の設定と残余リスクの正本）への日付つき追記で足りる。

## 検査（受け入れ基準 → 検査）

helm の描画はテスト仕様書の対象外で、先例（grpcPort・broker.tier・discord.bot）も**テスト ID を振らず** `helm.yml` の
アサートで固定している。本作業もそれに倣い、割り当てのテスト ID（`T-10-950`〜`T-10-969`）は使わない。

| 受け入れ基準 | 検査（`helm.yml` の `Assert moomoo tuning values keep numeric 0 (#842)`） |
| --- | --- |
| 数値の 0 が値として env へ届く（3 件） | `--set <key>=0` で各 env が `value: "0"` |
| 空・未設定は注入しない（アダプタ既定・既定描画のバイト等価） | 既定の moomoo-sim 描画・`--set <key>=null` で 3 件とも env が現れない |
| 0 以外の値は従来どおり | `--set moomoo.stopLimitOffsetRatio=0.02` → `"0.02"`、`--set-string …=0` → `"0"` |

## 検証

- helm v4.2.1（CI と同一版）: `helm lint --strict`・既定描画・`values-local` 描画が是正前とバイト等価であること、
  上の表のアサートをローカルで実行し、是正前の template で**赤になる**ことを確かめる。
- 文書検査器一式（共通ルールの列挙）。backend は触らない。
