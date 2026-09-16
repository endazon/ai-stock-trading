---
title: "console 経路で OpenD が終了したら本体（PID 1）も同じ終了コードで終わり、コンテナを再起動させる（#802）"
type: spec
status: done
related_ids: [FR-11, FR-09, UC-06, SC-04, IADR-0053, IADR-0060, IADR-0167, IADR-0325]
author: claude
created: 2026-09-16
updated: 2026-09-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: console 経路で OpenD が終了したら本体も終わる（#802）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-11`（OpenD 常駐）／`FR-09`（有人認証の受け口。console 経路の入力面を壊さないこと）
- ユースケース（UC）: `UC-06` 代替フロー「ゲートウェイの有人認証」
- 画面（SC）: `SC-04`（OpenD 認証操作。console 経路の到達点）
- 関連 IADR: `IADR-0053`（常駐モデル）／`IADR-0060`（entrypoint）／`IADR-0167`（livenessProbe は付けない＝復旧は entrypoint の終了に頼る）／
  `IADR-0325`（`OPEND_STDIN_MODE`・console 経路。本作業で日付つき追記する）
- 起票: #802（console モードで OpenD が終了しても `script`（PID 1）が生き残り Pod が Running のまま残る）
- 前提: #730 / #801 の修正（pty の `stty rows/cols`）は据え置く

## 目的・背景

`console` 経路（chart の既定）は `exec script -q -e -f -a -c "stty …; exec ./OpenD" console.log 0<> FIFO` で `script` を PID 1 にする。
#801 の監査で、OpenD が終了（「moomoo OpenD has exited」）しても `script` が PID 1 に居座り、コンテナが `Up` のまま残ることが
実測された。chart は意図的に livenessProbe を付けない（IADR-0167）ので、Pod は NotReady のまま Running に残り、復旧は手動の Pod 削除になる。

### 根本原因（2026-09-16・使い捨てコンテナ＝稼働イメージ `k3d-local/ai-stock-trading/opend:latest`（jammy・util-linux 2.37.2）で確定）

- **issue の見立て（FIFO を `0<>` で開くと EOF が出ず `-e` が効かない）は外れ。** 背景ループを張らずに
  `script -q -e -f -a -c 'sh -c "sleep 1; exit 3"' log 0<> FIFO` を単独で走らせると、子の終了と同時に `script` は **rc=3 で自ら抜ける**
  （stdin が `/dev/null` でも同じ）。
- **真因は `exec script` の形で、本体が先に張った背景ループ（`cap_console_log` / `watch_captcha`）が `script` の子として引き継がれること。**
  子（OpenD）以外に生きている子が 1 つでも居ると、`script` は子の終了後に CPU を回したまま（`ps` で状態 `R`）抜けない。
  背景の子の有無 × stdin（FIFO `0<>` / `/dev/null`）の 2×2 で実測し、**背景の子が居るときだけ**固まった（下の証跡）。
- util-linux 2.37.2 `lib/pty-session.c` `ul_pty_wait_for_child`（実行中の枝）:
  `for (;;) { pid = waitpid(pty->child, &status, WNOHANG); if (pid != -1) { child_die; ul_pty_set_child(pty, -1); } else break; }`
  —— 1 周目で子（OpenD）を回収して `pty->child = -1` にした後、2 周目は **`waitpid(-1, WNOHANG)`（＝任意の子）** になる。
  他に生きている子が居ると `0` が返り（`-1` ではない）、`break` に至らず**無限に回る**。子が OpenD だけなら `ECHILD` で `-1` が返って抜ける。
- 実際の entrypoint を PID 1（`exec`）・子・tty 配下（Pod の `tty: true` 相当）の 3 形で走らせ、**いずれも固まる**ことを確認（FIFO や PID 1 は条件ではない）。

## 設計

**採用: `script` を `exec` せず、本体（bash＝PID 1）の子として起こし、本体が `wait` して同じ終了コードで終わる。**

- `script` の子は OpenD だけになる（背景ループは本体の子のまま）ので、OpenD の終了と同時に `script` は `-e` の終了コードで自ら抜ける（10 ms の leaving timeout）。
- 本体は `wait "$script_pid"` の戻り値で `exit` する。`-e` により OpenD の終了コード（シグナル死は `0x80+signo`）がそのままコンテナの終了コードになる。
- Pod 削除の SIGTERM は本体が `trap` で `script` へ転送し、`script` が子（OpenD）へ渡す（従来と同じ経路）。trap で `wait` が中断された場合は
  `script` が生きている限り `wait` し直す。
- **据え置き**: FIFO の `0<>`（O_RDWR。書き手が来ては去っても EOF を出さない＝決定 1）、`-a`（console 上限の成立条件）、`-f`、`stty rows/cols`（#730）、
  `$*` を 1 つの文字列で `exec` する形（OpenD は `script` の直接の子のまま＝SIGTERM がそのまま届く）。
- 背景ジョブの標準入力は `/dev/null` へ差し替えられるが、`0<> "$fifo"` は**明示のリダイレクト**なので差し替えの後に適用される（`<&0` の罠には当たらない）。

### 検討して採らなかった案

- **(a) 背景の監視で `pgrep -x OpenD` が消えたら `kill 1`**: OpenD の終了コードを本体が知れない（親ではない）。`script` の spin 中に TERM を送ると
  `-e` は 143 を返し、OpenD の終了コードが失われる。comm 名で OpenD を同定する必要もあり、試験（偽 OpenD）と本番で形が変わる。
- **(c) `sh -c` の内側で `$*` を `exec` せず終了コードをファイルへ書いて `$PPID` を殺す**: `script` と OpenD の間に `sh` が挟まり、SIGTERM が
  `sh` で止まって OpenD が孤児になる（graceful に落ちない）。
- **背景ループを `script` の子にしない別解（setsid / 二重 fork）**: コンテナ内では孤児が PID 1（＝`script`）へ再親化されるので効かない。

## 変更の範囲

| 箇所 | 扱い |
| --- | --- |
| `deploy/opend/entrypoint.sh` `start_opend_with_console` | **変更**: `exec script …` → `script … &` ＋ `trap`（TERM/INT/HUP を転送）＋ `wait` ＋ `return rc`。`-e` の誤ったコメント（「Pod が Running のまま残らないようにする」）を真因で書き直す |
| `deploy/opend/entrypoint.sh` `case … console)` | **変更**: 関数の戻り値で `exit` する |
| `deploy/opend/entrypoint.test.sh` | **追加** T-802-01（背景の子を張った本体と同じ形で、偽 OpenD が 1 秒後に 3 で終わる → 本体が 8 秒以内に RC=3 で抜ける）／T-802-02（FIFO へ書いた行が届き、その後の終了コード 5 が伝わる）。`stop_fake_opend` は TERM → 猶予 → -9 へ（console 経路の CHILD は本体になったため -9 だけでは `script` と偽 OpenD が残る） |
| `deploy/opend/README.md` | **追記**: 「標準入力の与え方」に OpenD 終了時の挙動（コンテナが同じ終了コードで終わり `restartPolicy: Always` で再起動）と真因、「OpenD 本体が担う複写」の `script` 行に注記 |
| `.ai-context/adr/IADR-0325` 残余リスク | **日付つき追記**（凍結本文は書き換えない）＋ 索引行（`check-adr-index-sync`） |
| `deploy/opend/k8s/opend.yaml`・chart `templates/opend.yaml`・`values.yaml` | **据え置き**: livenessProbe を付けない方針（IADR-0167）は変えない。console 経路が終了コードで抜けるようになり、`restartPolicy: Always` の再起動が効くようになるだけ |
| `deploy/helm/ai-stock-trading/README.md` | **据え置き**: 49 行目は #730（pty の画面サイズ）だけを述べており、終了時の挙動には触れていない |
| サイドカー `OpendAuthGateway` の C# コメント（`script -q -f -a`） | **据え置き**: 複製の出力形は変わらない |

### 母集合の引き方（`.claude/rules/traceability.md` 規則 1〜6・9・10）

誤りの側の文字列で追跡下の全ファイルを引いた（拡張子で絞らない。`node_modules` / `.git` / `CHANGELOG.md`（生成物）/ `.ai-context/specs/` / `.ai-context/superpowers/`（凍結）を除外）:

| 軸 | 検索語 | 当たり | 扱い |
| --- | --- | --- | --- |
| 1 | `Running のまま` | `entrypoint.sh:145` | 是正（誤ったコメント） |
| 2 | `script -q` | `entrypoint.sh:165`・`README.md:282`・`IADR-0322:111`・`OpendAuthOptions.cs:24`・`ConsoleTail.cs:7` | entrypoint と README は是正／IADR-0322 は #722 段 2 当時の実測の引用（凍結記録）で据え置き／C# は複製の形の説明で正しいまま |
| 3 | `start_opend_with_console` | `entrypoint.sh`・`entrypoint.test.sh`・`IADR-0325:55,100` | entrypoint と試験は是正／IADR-0325 は追記で対応 |
| 4 | `pid 1` / `PID 1` | `entrypoint.sh:51`・`IADR-0322:34`・`Program.cs:9`・`backend/Dockerfile:40` | 「OpenD は PID 1 の標準入力から読む」は console 経路では元々 `script` の pty を指す説明で、本変更でも標準入力は同じ pty のまま（据え置き） |
| 5 | `子の終了コード` | `entrypoint.sh:145` | 軸 1 と同じ行 |
| 6 | `livenessProbe` / `OpenD が落ち` / `has exited` / `CrashLoop` | `README.md:345`・`k8s/opend.yaml:60`・chart `templates/opend.yaml:136`・`IADR-0167`・`OpendAuthGateway` の C#（ENXIO） | livenessProbe を付けない結論と根拠は変わらない（据え置き）。ENXIO の説明は FIFO の読み手不在で、変更なし |

除外の理由: `.ai-context/specs/20260909_722_*`・`20260910_730_*`・`20260916_730_*` は当時の記述（凍結）。`CHANGELOG.md` は生成物。

## 受け入れ基準

- [x] AC1: `bash -n deploy/opend/entrypoint.sh` が通る
- [x] AC2: `entrypoint.test.sh` が Linux コンテナ（稼働イメージ）で **49 passed / 0 failed**（既存 44 ＋ T-802-01 の 2 件 ＋ T-802-02 の 3 件）
- [x] AC3: T-802-01/02 は**修正前の実装で赤**（本体が抜けない）
- [x] AC4: 実際の entrypoint を PID 1・子・tty 配下の 3 形で走らせ、偽 OpenD（2 秒後に `exit 3`）の終了から**数秒以内に rc=3 で終わる**（修正前は 3 形とも 15 秒の監視で固まる）
- [x] AC5: FIFO へ書いた行が引き続き OpenD に届く（T-802-02・T-722-05 系）。`stty` の画面サイズは据え置き（T-730-01）
- [x] AC6: 本体に SIGTERM を送ると `script` 経由で OpenD が落ち、本体が終了する（stop_fake_opend の TERM 経路で各 console 試験が後始末できている）
- [ ] AC7: 稼働クラスタで OpenD の終了後にコンテナが再起動されること（本作業では稼働クラスタへ触れない。次回の再デプロイ後に `kubectl get pods` の RESTARTS で確認する）

## 検証の証跡（2026-09-16）

```
# 真因の切り分け（稼働イメージの使い捨てコンテナ・util-linux 2.37.2）
EXTRA=0 STDIN=fifo    : script exited by itself rc=3 (~1 s)
EXTRA=0 STDIN=devnull : script exited by itself rc=3 (~1 s)
EXTRA=1 STDIN=fifo    : script STILL RUNNING 6 s after child exit (state=R) => HANG
EXTRA=1 STDIN=devnull : script STILL RUNNING 6 s after child exit (state=R) => HANG

# 修正前の entrypoint（偽 OpenD は 2 秒後に exit 3）: child / pid1 / tty の 3 形とも
WATCHDOG: still running after 15 s => HANG REPRODUCED
      1       0 Rs   script          script -q -e -f -a -c stty rows 24 cols 200 2>/dev/null || :; exec ./OpenD …
     21       1 S    bash            /bin/bash /tmp/e.sh        ← cap_console_log（script の子）
     22       1 S    bash            /bin/bash /tmp/e.sh        ← watch_captcha（script の子）
```

# 修正前の entrypoint に T-802 を足した状態（同コンテナ）
  NG    T-802-01 OpenD 終了後に本体が抜ける（script が PID 1 に居座らない）
  NG    T-802-01 本体は OpenD の終了コードで終わる
  ok    T-802-02 FIFO へ書いた行が OpenD に届く（0<> の性質を保つ）
  NG    T-802-02 入力後に OpenD が終われば本体も抜ける
  NG    T-802-02 本体は OpenD の終了コードで終わる
45 passed, 4 failed, 0 skipped

# 修正後（同コンテナ・2 回連続）
  ok    T-722-05 複製ファイルにコンソール出力が入る
  ok    T-722-10 console 経路は tty 転送 cat を張らない（#727 回帰・script の入力転送を殺さない）
  ok    T-730-01 既定は 24 行 200 桁（コマンド 1 行が折り返さない幅）
  ok    T-802-01 OpenD 終了後に本体が抜ける（script が PID 1 に居座らない）
  ok    T-802-01 本体は OpenD の終了コードで終わる
  ok    T-802-02 FIFO へ書いた行が OpenD に届く（0<> の性質を保つ）
  ok    T-802-02 入力後に OpenD が終われば本体も抜ける
  ok    T-802-02 本体は OpenD の終了コードで終わる
49 passed, 0 failed, 0 skipped

# 修正後の entrypoint（偽 OpenD は 2 秒後に exit 3）
== variant=child entrypoint exited rc=3 after 2 s
== variant=pid1  … ==> moomoo OpenD exited with code 3; leaving the container so it gets restarted / host: nerdctl rc=3 elapsed=3 s
== variant=tty   … 同じ行が出て entrypoint は抜ける（試験用の外側 `script` だけが、孤児になった背景ループが握る pty スレーブを待って
                   15 秒残った。Pod には外側の `script` は無く、PID 1 の終了で pid namespace ごと消える）
== variant=tty2  … 上の形で entrypoint が抜けた直後に孤児の背景ループを殺すと外側 `script` は 0 秒で抜ける
                   （entrypoint exited rc=3 after 2 s）＝残っていたのは試験用の外側 `script` であって entrypoint ではない
```

Windows ホスト（Git Bash）では FIFO / console 群は従来どおり skip する（`19 passed, 0 failed, 4 skipped`）。

## 未決事項・残余リスク

- OpenD が異常終了を繰り返す状態では、`restartPolicy: Always` の再起動が CrashLoopBackOff になる。これは #802 が求めた挙動（自動で作り直す）であり、
  livenessProbe を付けない判断（IADR-0167）とも矛盾しない（ハング検知は依然として監視＋有人）。
- `script` の版が上がって `ul_pty_wait_for_child` が直っても、本変更は害にならない（`script` の子が OpenD だけである形は変わらない）。
- 本体が `wait` 中に SIGTERM を受け、`script` が同時に終了した極小の窓では、本体の終了コードが 143 になる（OpenD の終了コードではない）。
  非ゼロであることは変わらず、Pod 削除の文脈でしか起きない。
