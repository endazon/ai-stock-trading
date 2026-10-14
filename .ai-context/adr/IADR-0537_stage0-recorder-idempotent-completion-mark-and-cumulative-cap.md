---
title: IADR-0537 Stage 0 の記録器を冪等にする。永続ボリュームに完了の印・途中の記録・費用の累計を置き、完了済みなら LLM を呼ばず、途中で落ちたら途中から再開し、累計が承認額を超える見込みなら呼ぶ前に止める
type: impl-adr
status: Accepted
related_ids: [FR-15, NFR, ADR-0033, ADR-0054, ADR-0064, IADR-0318, IADR-0498, IADR-0528, IADR-0397]
author: claude (Claude Code)
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-15 Stage 0 の LLM 費用は実行前の見積り提示と利用者承認を必須とする)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 5)
related_specs:
  - ../specs/20261014_1342_stage0-recorder-idempotent.md
---

# IADR-0537: Stage 0 の記録器の冪等化（完了の印・途中からの再開・費用の累計の上限・永続ボリューム）（#1342）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-14
- 決定者: claude が起案（起票 [#1342](https://github.com/endazon/ai-stock-trading/issues/1342)。レビューは PR で受ける）

## 起点・関連

- 計画: FR-15（Stage 0 の LLM 費用は実行前の見積り提示と利用者承認を必須とする）、ADR-0033 決定 5（見積りの提示・承認・実行中の超過で停止）。
- 事象: PoC の運用（2026-10-14）。記録器は trade-decision の起動ごとに走り、`Enabled=true` で承認値が一致していれば**再起動のたびに最初から記録し直して課金する**。PoC のホストは 3 日で 4 回、停止または一斉再起動した（MSP#1919）。書き出し先は Pod の揮発領域（`/tmp`）だった。
- 関連 IADR:
  - [IADR-0318](IADR-0318_stage0-ai-decision-record-and-replay.md) 決定 5（承認ゲート・run-once・見積り超過で停止）。本 IADR は承認ゲートを変えず、**承認の効く範囲を「1 回の実行」から「承認値の組の累計」へ広げる**。
  - [IADR-0498](IADR-0498_stage0-two-tier-recording-and-live-gate-prerequisite.md)（二段・見積りの式）。式は変えない。
  - [IADR-0528](IADR-0528_stage0-asof-base-input-supply.md)（as-of 入力の基底）。変えない。
  - [IADR-0397](IADR-0397_composition-wiring-guard.md)（配線の抜けの検査）。本物の既定（`NoStage0RecordingStateStore`）を試験から参照する。
- 作業仕様書: [20261014_1342_stage0-recorder-idempotent](../specs/20261014_1342_stage0-recorder-idempotent.md)（窓の表・母集合・変異）。

## コンテキスト

1. 承認ゲート（`ValidateApproval`）は 1 回の実行の見積りと承認額の一致を見る。実行中の停止も、その実行の実績だけを見ていた。累計は誰も見ていない。
2. 記録は実行の最後に 1 回だけ書き出す。途中で落ちると、それまでの費用に対して何も残らない。
3. trade-decision は DB を持たない（判断はステートレス）。永続の状態を置ける既存のストアが無い。
4. 「実行したらすぐ `Enabled=false` に戻す」運用は、記録中・戻す前の再起動を防げない。

## 決定

### 決定 1: 実行の状態を永続領域に置き、無ければ走らせない

- 新しい構成 `Stage0Recording:StateDirectory`（ディレクトリ）に、完了の印・チェックポイント（途中の記録）・費用の累計の台帳・実行のロックを置く。
- 🔴 **未設定なら記録しない**（`NotConfigured`）。揮発領域の状態で記録を走らせる抜け道を残さない。DI の既定は `NoStage0RecordingStateStore`（読めない・書けない・ロックが取れない）。
- 実行のロック（`recorder.lock` を `FileShare.None` で開く。Unix では flock）が取れなければ LLM を呼ばない（`Locked`）。配備の入れ替えで旧 Pod と新 Pod の記録器が重なる場合に備える。
- DB を新設しない（trade-decision に DB とマイグレーションを足すのは本件の範囲を超える）。

### 決定 2: 印の鍵と台帳の鍵を分ける

- **印の鍵**（実行の同一性）: 承認の単位（承認額〔円未満 2 桁〕・承認回数・期間・銘柄集合〔並べ替え〕）＋カットオフ・両層のモデル・多数決回数・1 日の判断回数・4 つのトークン量の SHA-256。
- **台帳の鍵**（承認の単位）: 承認額・承認回数・期間・銘柄集合だけの SHA-256。印の鍵とは別に計算し、別のファイルに置く。
- 書き出し先・状態の置き場・`Enabled` はどちらにも入れない（置き場を変えただけで同じ承認値の記録がもう一度走らない）。
- 帰結: 通し試験の組（AAPL × 4 日・7.67 円）と本番の組（12 銘柄 × 19 日・437.28 円）は互いを止めない。同じ承認のままモデルやトークン量の欄だけを動かすと、印は別になるが台帳は引き継ぐ（同じ承認額の中で、欄を動かしてもう一度課金できない）。

### 決定 3: 窓の両端を突き合わせる（途中で落ちたら途中から再開する）

窓の両端は「記録の開始（開始の印）」と「完了の印」である。

| 状態 | 扱い |
| --- | --- |
| 印が無い | 開始の印を書いてから走る（書けなければ LLM を呼ばない） |
| 終わりの印（`Completed` / `Stopped`）がある | LLM を呼ばずに終える（`AlreadyFinished`）。書き出さない（記録を上書きしない） |
| 開始の印だけがある | チェックポイントの判断時点を飛ばして**途中から再開**する |
| 印かチェックポイントが読めない | LLM を呼ばない（`StateUnavailable`）。人が状態のファイルを確かめて消すまで走らない |

- 判断時点ごとに、①台帳へ見込みを積む（予約）→②LLM を呼ぶ→③台帳を実績へ置き換える→④チェックポイントを書く、の順で進める。
- 終わりの印は、記録を書き出せたときだけ付ける。書き出しに失敗したら付けない（次の起動はチェックポイントから LLM を呼ばずに書き出し直す）。
- 🔴 **止まった実行（上限・状態の失敗）の記録が 0 件なら、書き出しも終わりの印も行わない。** 印だけが消えた組や、印の鍵だけが変わった組（台帳の鍵は同じ）は、1 件目より前で上限に当たる。このとき空の集合で書き出すと、課金済みの既存の記録を 0 件で上書きし、さらに `Stopped` の印で固めてしまう（PR のレビューで指摘）。
- 書き出し先（`FileStage0DecisionRecordSink`）も二重に守る。既存の記録より判断の件数が少ない集合（0 件を含む）での上書きを拒み（`SaveFailed`）、一時ファイルへ書いて flush してから置き換える。読めない既存のファイルは守る対象にしない。置き換えたいときは、人が既存のファイルを退避する。
- 棄却した形（作業仕様書の表で実測）:
  - **後の端だけを見る**（完了の印があれば走らない）: 途中で落ちると最初からやり直し、記録済みの判断時点を二重に課金する。
  - **前の端だけを見る**（開始の印があれば走らない）: 途中で落ちた正当な実行が、人が消すまで止まる。課金は二重にならないが、PoC のホストでは再起動が多く、記録がそろわない。
- 残る二重課金: 「LLM の応答の後・チェックポイントの前」に落ちた 1 判断時点（一次 1 回＋多数決回数）だけ。その費用は①の予約と③で台帳に入っており、累計の上限に効く。

### 決定 4: 費用の累計の上限（承認額）を印と独立に見る

- 上限は承認額（`ApprovedEstimateJpy`）。判断時点ごとに、**呼ぶ前に**「累計＋1 判断時点の見積り（一次 1 ＋ 多数決回数）」を円未満 2 桁に丸めて承認額を超えるなら止める（`StoppedOverBudget`）。呼んだ後に累計が承認額を超えても止める。
  - 丸めは承認ゲートと同じ粒度である。承認額は見積りを円未満 2 桁で写した値であり、写した端数（切り捨て側）で最後の判断時点を落とさない。
- 印が消えても・壊れても、台帳の累計が承認額に届いていれば呼ばない（両端で止める）。台帳が読めなければ走らない。
- 旧来の「その実行の実績が見積りを超えたら停止」は、累計による判定に置き換わる（累計 ≥ その実行の実績なので、旧判定より早く止まることはあっても遅くはならない）。
- 応答が見積りより重いと、旧来より 1 判断時点早く止まることがある（「超える見込みなら止める」の帰結）。

### 決定 5: helm の永続ボリューム（既定無効）

- `stage0Recording.persistence`（`enabled` 既定 false・`size` 1Gi・`storageClassName`・`mountPath` `/var/lib/ai-stock-trading/stage0`）。有効なら PVC `trade-decision-stage0`（`helm.sh/resource-policy: keep`）を作り、trade-decision へ mount し、`Stage0Recording__StateDirectory=<mountPath>/state` を注入し、更新方式を `Recreate` にする。
- 既定（無効）の描画はバイト等価（helm template の差分 0）。`values-local.yaml` は有効にし、書き出し先を `/var/lib/ai-stock-trading/stage0/records.json` へ移す。
- 描画で止める: `Stage0Recording__StateDirectory` を extraEnv に書く／書き出し先が mount の外／`mountPath` が相対。`helm.yml` の「Assert stage0 recorder persistence (#1342)」が検査する。
- backtest へ渡す経路は従来どおり `kubectl cp`（PVC から取り出す。Pod の再起動では消えない）。backtest の Pod へ同じ PVC を mount するのは本 IADR の範囲外。

## 帰結

- PoC は「実行後すぐ `Enabled=false` に戻す」に頼らずに済む（戻すのは引き続き推奨。戻し忘れても二重には課金しない）。
- 同じ組でもう一度記録するには、人が `records.json` を退避してから、PVC の `state/runs/` と `state/ledger/` を消す（chart README）。台帳を消すと、承認額の上限も最初から数え直しになる。これが「人の明示（印の解除）」である。
- 新しい結末 `AlreadyFinished` / `StateUnavailable` / `Locked` は、いずれも LLM を呼んでいない（`StateUnavailable` は途中で状態を書けなくなったときに限り、それまでの呼び出しがある）。

## 残余リスク

- 状態は PVC 1 つにある。PVC を消せば印も台帳も消える（keep で uninstall では消さない）。
- ReadWriteOnce の PVC と `Recreate` は単一ノードの PoC を前提にしている。複数ノードでノードをまたいで再スケジュールされると、ボリュームの付け替えを待つ。
- 台帳は記録器が自分で数えた費用（応答のトークン量 × 単価表）であり、費用統制サービスの計上（`purpose=stage0-recording`）と突き合わせていない。

## 関連

- 計画 ADR-0033 決定 5、FR-15。
- [IADR-0318](IADR-0318_stage0-ai-decision-record-and-replay.md)、[IADR-0498](IADR-0498_stage0-two-tier-recording-and-live-gate-prerequisite.md)、[IADR-0528](IADR-0528_stage0-asof-base-input-supply.md)、[IADR-0397](IADR-0397_composition-wiring-guard.md)。
- 作業仕様書: [20261014_1342_stage0-recorder-idempotent](../specs/20261014_1342_stage0-recorder-idempotent.md)。
