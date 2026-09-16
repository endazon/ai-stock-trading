---
title: "console 経路の pty に画面サイズを与え、画面 SC-04 から入れた検証コードが OpenD に届くようにする（#730）"
type: spec
status: done
related_ids: [SC-04, FR-09, FR-11, UC-06, IADR-0322, IADR-0325]
author: claude
created: 2026-09-16
updated: 2026-09-16
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: console 経路の pty に画面サイズを与える（#730）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-09`（有人認証の受け口）／`FR-11`（OpenD 常駐）
- ユースケース（UC）: `UC-06` 代替フロー「ゲートウェイの有人認証」
- 画面（SC）: `SC-04`（OpenD 認証操作）
- 関連 IADR: `IADR-0322`（サイドカーの allowlist）／`IADR-0325`（`OPEND_STDIN_MODE`。本作業で残余リスクへ追記する）
- 起票: #730（画面・サイドカー・FIFO 直書きのいずれからも検証コードが OpenD に届かない）
- 利用者指示（2026-09-16）: 「tty モードでのログインは最終手段なので、どうにか画面からログインできないか検討」

## 目的・背景

`console` 経路（chart の既定）は FIFO → `script`（pty）→ OpenD の形で標準入力を与える。稼働クラスタで画面
SC-04 から 6 桁を送ると、サイドカーは `POST /opend-auth/verify` → 200 を返し FIFO へ書くが、OpenD はプロンプト
`>>>` を再描画するだけでログインしない（2026-09-16 18:47:52.705 に POST、18:47:52.805 に再描画）。

### 根本原因（2026-09-16・使い捨てコンテナで確定）

- `script` は**自分の標準入力が端末のときだけ**ウィンドウサイズを pty へ写す。console 経路では標準入力が FIFO なので
  pty は **0 行 0 桁**（`stty -a -F /dev/pts/1` → `rows 0; columns 0`）のまま OpenD が起動する。
- OpenD の行エディタは幅 0 のとき入力文字をすべて捨て、Enter で**空行**を送る。console.log の増分
  `\r>>>\r>>>\r\n\r>>>`（14 バイト）はその再描画であり、9/14 の記録が「配送は動く」と読んだものも同じ空行だった
  （`help` の出力＝コマンド一覧は 500 バイト超で、出ていない）。
- `stty -F /dev/pts/1 rows 40 cols 120` を打った直後から同じ `help\n` でコマンド一覧（587 バイト）が出る。
  1 バイトずつ書いても（仮説「複数バイトの一括到着」）幅 0 では捨てられ、幅を与えれば通る＝サイドカーの書き方は無関係。
- `tty` モードが動くのは `kubectl attach` が端末の実サイズ（TIOCSWINSZ）を送るからで、読み口（fd 0 か制御端末か）の
  違いではない。#730 の仮説 A（制御端末）・B（CR/LF）はいずれも外れ。

## 変更の範囲

| 箇所 | 扱い |
| --- | --- |
| `deploy/opend/entrypoint.sh` `start_opend_with_console` | **変更**: `script -c` の子で OpenD を `exec` する前に `stty rows ${OPEND_CONSOLE_ROWS:-24} cols ${OPEND_CONSOLE_COLS:-200}` を打つ（失敗しても起動を止めない `|| :`）。標準入力＝pty のスレッドなので、その場の `stty` が OpenD に見える |
| `deploy/opend/entrypoint.test.sh` | **追加** T-730-01: 偽の OpenD に `stty size` を出させ、複製に `24 200` が入ることを固定する（console 群＝Linux ゲート内） |
| `deploy/opend/README.md` 「標準入力の与え方」 | **改訂**: console 経路が既定で使える旨と根本原因、`tty` は最終手段として残す |
| `.ai-context/adr/IADR-0325` 残余リスク | **日付つき追記**（凍結本文は書き換えない） |
| `deploy/opend/k8s/opend.yaml`・chart `templates/opend.yaml` | **据え置き**: env 名の追加は無い（`OPEND_CONSOLE_ROWS/COLS` は任意の上書きで既定は entrypoint が持つ） |
| サイドカー `OpendAuthGateway` | **据え置き**: 書き方は原因ではない。受け入れ基準 2（届いたことの確認）は #730 に残す |

走査: `start_opend_with_console` / `OPEND_STDIN_MODE` / `#730` で追跡下の全ファイルを引いた。凍結記録
（`.ai-context/specs/20260909_722_*`・`20260910_730_*`）は書き換えない。

## 受け入れ基準

- [x] AC1: `bash -n deploy/opend/entrypoint.sh` が通る
- [x] AC2: `entrypoint.test.sh` が Linux コンテナで **44 passed / 0 failed**（既存 42 ＋ T-730-01 の 2 件）
- [x] AC3: T-730-01 は**修正前の実装で赤**（42 passed / 2 failed。`stty size` が `0 0`）
- [x] AC4: 実 OpenD（`k3d-local/ai-stock-trading/opend:latest`・ダミー資格情報・`--dns 192.0.2.1` で「Logging in」の段）で、
  修正版 entrypoint なら手動の `stty` なしに起動直後の pty が `24 200` になり、FIFO への `help\n` でコマンド一覧（667 バイト）が出る
- [ ] AC5: 稼働クラスタで画面 SC-04 から入れた 6 桁でログインが成立する（実口座・SMS。稼働 Pod には `kubectl exec … stty -F /dev/pts/1 rows 40 cols 120` を当てて検証中。結果は #730 に記録する）

## 検証の証跡（2026-09-16）

```
# 修正前（T-730-01 を足した状態）
  NG    T-730-01 console 経路の pty に画面サイズが入る（0 行 0 桁だと OpenD が入力を捨てる）
  NG    T-730-01 既定は 24 行 200 桁（コマンド 1 行が折り返さない幅）
42 passed, 2 failed, 0 skipped
# 修正後
  ok    T-730-01 console 経路の pty に画面サイズが入る（0 行 0 桁だと OpenD が入力を捨てる）
  ok    T-730-01 既定は 24 行 200 桁（コマンド 1 行が折り返さない幅）
44 passed, 0 failed, 0 skipped
# 実 OpenD（使い捨てコンテナ・修正版 entrypoint）
winsize at start: 24 200
help delta=667
Command List:
	input_phone_verify_code
```

## 未決事項・残余リスク

- OpenD が画面サイズを起動時にしか読まない版が出た場合は、実行中の `stty` が効かなくなるが、本修正は起動前に打つので影響しない。
- `OPEND_CONSOLE_COLS` を小さくすると長いコマンド（`relogin -login_pwd=…`）が折り返し、行エディタの再描画が乱れ得る。既定 200 のまま使う。
- 受け入れ基準 2（#730）「届いたことを出力で確認できる手段」は本作業の射程外。幅を与えた後は入力がエコーされ出力も複製に載るので、
  サイドカーが投入前後の console.log の増分を返す形で実装できる。
