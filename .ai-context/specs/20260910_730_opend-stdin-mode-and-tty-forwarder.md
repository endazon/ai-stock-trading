---
title: OpenD の標準入力の与え方を選べるようにし、console 経路で tty 転送を張らないようにする
type: spec
status: done
related_ids: [FR-09, FR-11, UC-06, SC-04, IADR-0322, IADR-0325]
author: endazon (with Claude Code)
created: 2026-09-10
updated: 2026-09-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
---

# 仕様書: OpenD 標準入力モードの選択と tty 転送の除去（#730）

> 🔴 **本仕様書は着手前ではなく、稼働環境の障害対応の最中に書き起こした。** 利用者が OpenD にログインできず
> 取引経路全体が止まっていたため、切り分けと復旧を先行させた。着手前作成の原則（CLAUDE.md 手順 3）からの逸脱で
> あり、記録として明示しておく。

## 起点

- issue #730。利用者報告「入力できない／SMS 再送もできない」。稼働クラスタで**検証コードがどの経路からも
  OpenD に届かない**（画面 SC-04・サイドカー・`kubectl exec` での FIFO 直書きのすべてが無反応）。

## 実測（2026-09-10）

| 観測 | 結果 |
| --- | --- |
| サイドカー | `投入を受理した（kind=phone）` を記録し HTTP 200。FIFO への書き込みは成功している |
| OpenD の標準入力 | `/dev/pts/1`（`script` の pty スレーブ）。`script`(PID 1) は fd0=FIFO・fd3=ptmx・fd4=pts/1 を持ち配線は繋がっている |
| pty の端末設定 | `-icanon -echo`。**エコーが無いのは正常**であり、無反応の判定材料にならない（当初これを誤って「届いていない証拠」と解釈した） |
| OpenD のスレッド | 54 本が futex 44 / nanosleep 5 / epoll 4 / accept 1。端末を読んでいるスレッドが見当たらない |
| 投げ捨て Pod での `script` 単体検査 | 同じ起動形で FIFO へ書いた行は子へ**届く**（exec 形・背景形の両方） |

**`script` の転送機構自体は動くのに、実 OpenD には届かない。原因は未特定である。**

## 併せて見つけた欠陥（本作業で修正）

`prepare_stdin_fifo` が張る tty 転送の `cat` は、**標準入力が開いたまま塞がる live 常態**では生き続け、その状態だと
`script` が FIFO 入力を子へ渡さなくなる。投げ捨て Pod で再現した。

- 従来の試験（T-722-05/06）は console 経路を **stdin=/dev/null** で起動しており、`cat` が即 EOF で消えるため
  この条件を再現していなかった。入力の試験（T-722-01/03）は **fifo 直読み経路のみ**を測っていた。
  **console 経路の入力配送を測る試験が 1 つも無かった**ことが、live だけで壊れて試験が素通りした理由である。

## 設計

### 1. `console` 経路では tty 転送を張らない

`prepare_stdin_fifo` を `make_stdin_fifo`（mkfifo だけ）と `start_tty_forwarder`（tty→FIFO の `cat`）へ分割し、
`start_opend_with_console` は前者だけを呼ぶ。`0<>`（O_RDWR）が EOF を抑えるため `cat` が無くても OpenD は終了しない。
`start_opend_with_fifo`（`script` を挟まない）は従来どおり両方を呼ぶ＝`kubectl attach` が効く。

### 2. 標準入力の与え方を `OPEND_STDIN_MODE` で選べるようにする

| 値 | 経路 | 用途 |
| --- | --- | --- |
| `console`（既定） | FIFO → `script`(pty) → OpenD | 画面 SC-04 から入れる（#722 段 2） |
| `fifo` | FIFO → OpenD 直読み | console 複製を作らない（画面は使えない） |
| `tty` | コンテナ本来の tty → OpenD | **実績構成**。`kubectl attach` で打つ |

**既定は `console` のまま**＝本番描画は不変。`tty` を残す理由は、**それが実口座でのログイン成功を確認できている
唯一の構成**だからである（README の実績）。原因が特定できるまでの逃げ道として明示的に持つ。

### 採らなかった案

- **`tty` を既定にする**: 画面（SC-04）を諦めることになる。原因未特定の段階で既定を退行させない。
- **`cat` を全経路から消す**: `fifo` 直読み経路では `cat` が `kubectl attach` を成立させており、壊す理由が無い。

## 走査した母集合（規則 2・9）

`prepare_stdin_fifo` / `start_opend_with` / `OPEND_STDIN` で追跡下の全ファイルを走査（`node_modules` / `.git` /
`.claude` / `bin` / `obj` を除外）。

| 箇所 | 扱い |
| --- | --- |
| `deploy/opend/entrypoint.sh` | **変更**（本件の実装点） |
| `deploy/opend/entrypoint.test.sh` | **変更**（T-722-10 を追加） |
| `deploy/opend/README.md` | **追記**（起動モードと実績構成の手順） |
| `deploy/opend/k8s/opend.yaml` のコメント（attach 手順） | **据え置き**: `tty` モードでの手順として正しいまま |
| `.ai-context/adr/IADR-0322`（サイドカーの allowlist） | **対象外**: 書ける行の統制であり、配送経路の話ではない |
| `.ai-context/specs/20260909_722_*`・`20260909_SC-04_*` | **除外**: 凍結記録 |

## 受け入れ基準

- [x] `bash -n` が通る
- [x] `entrypoint.test.sh` が Linux コンテナで 42 passed / 0 failed
- [x] T-722-10 が**変異（旧実装＝console でも tty 転送を張る）では落ちる**ことを確認（41 passed / 1 failed）
- [x] 既定（`OPEND_STDIN_MODE` 未設定）で従来どおり console 経路を選ぶ
- [x] 稼働クラスタを `tty` へ切り替え、**実口座でログイン成功**（2026-09-10）
- [ ] `console` 経路の配送が直る → **#730 で継続**（本作業の射程外）

## 未決事項・残余リスク

- **画面 SC-04 は使えないままである。** 原因未特定のため #730 に残す。
- 稼働環境は `kubectl set env` で `OPEND_STDIN_MODE=tty` を入れており、**Helm の管理外**である。次回の
  `helm upgrade` で剥がれる（＝既定の `console` に戻り、また入力できなくなる）。#730 の解決までは
  再デプロイのたびに再設定が要る。
- 稼働 Pod には ConfigMap 経由で修正版 entrypoint を載せている（イメージは未再ビルド。ビルド環境の
  credential helper が壊れており、`OPEND_TARBALL` も手元に無いため）。恒久化は次回のフルビルドで行う。
