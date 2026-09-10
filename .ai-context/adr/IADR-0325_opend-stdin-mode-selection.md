---
title: IADR-0325 OpenD の標準入力の与え方を `OPEND_STDIN_MODE` で選べるようにし、console 経路では tty 転送を張らない
type: impl-adr
status: Accepted
related_ids: [FR-09, FR-11, UC-06, SC-04, IADR-0053, IADR-0060, IADR-0321, IADR-0322]
author: endazon (with Claude Code)
created: 2026-09-10
updated: 2026-09-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
---

# IADR-0325: OpenD の標準入力の与え方を選べるようにし、console 経路では tty 転送を張らない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- issue: [#730](https://github.com/endazon/ai-stock-trading/issues/730)（稼働クラスタで検証コードが OpenD に届かない）
- 仕様書: [`.ai-context/specs/20260910_730_opend-stdin-mode-and-tty-forwarder.md`](../specs/20260910_730_opend-stdin-mode-and-tty-forwarder.md)
- 関連 IADR: [IADR-0053](IADR-0053_moomoo-opend-dockerization.md)（OpenD の常駐モデル）／
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（entrypoint と RSA）／
  [IADR-0321](IADR-0321_opend-auth-bff-sole-authorization-point.md)（BFF が唯一の認可点）／
  [IADR-0322](IADR-0322_opend-auth-sidecar-command-allowlist.md)（サイドカーの allowlist）

## 背景・課題

#722 で OpenD の標準入力を FIFO 化し、段 2 で `script` の擬似端末（pty）へ移して console 複製を取れるようにした
（画面 SC-04 から検証コードを入れるため）。**しかし稼働クラスタでは検証コードがどの経路からも OpenD に届かない。**

2026-09-10 の実測:

| 経路 | 結果 |
| --- | --- |
| 画面 SC-04 → BFF → サイドカー → FIFO | サイドカーは `投入を受理した（kind=phone）` を記録し 200 を返すが OpenD は無反応 |
| `kubectl exec … > /run/opend/stdin` | 同じく無反応 |
| サイドカーの `resend`（`req_phone_verify_code`） | 200 だが OpenD は新しい SMS を要求しない |
| 投げ捨て Pod での `script` 単体検査 | 同じ起動形なら FIFO へ書いた行は子へ**届く** |

**`script` の転送機構そのものは動くのに、実 OpenD には届かない。原因は未特定である。**

さらに切り分けの過程で、**`prepare_stdin_fifo` が張る tty 転送の `cat` は、標準入力が開いたまま塞がる live 常態では
`script` の入力転送を殺す**ことを投げ捨て Pod で再現した。これは #722 の設計とは独立した別の欠陥である。

## 決定

### 1. `console` 経路では tty 転送（`cat`）を張らない

`prepare_stdin_fifo` を `make_stdin_fifo`（mkfifo だけ）と `start_tty_forwarder`（tty→FIFO の `cat`）へ分割し、
`start_opend_with_console` は前者だけを呼ぶ。`0<>`（O_RDWR）が EOF を抑えるので `cat` が無くても OpenD は終了しない。
`script` を挟まない `start_opend_with_fifo` は従来どおり両方を呼ぶ＝`kubectl attach` が効く。

### 2. 標準入力の与え方を `OPEND_STDIN_MODE` で選ぶ。**既定は `console` のまま**

| 値 | 経路 | 位置づけ |
| --- | --- | --- |
| `console`（既定） | FIFO → `script`(pty) → OpenD | 画面 SC-04 から入れる（#722 段 2）。🔴 稼働では未達（#730） |
| `fifo` | FIFO → OpenD 直読み | console 複製を作らない。画面は使えない |
| `tty` | コンテナ本来の tty → OpenD | **実績構成**。`kubectl attach` で打つ |

**`tty` を残す理由は、それが実口座でのログイン成功を確認できている唯一の構成だからである**（README の実績。
2026-09-10 にも本構成で実口座ログインに成功した）。原因が特定できるまでの逃げ道として明示的に持つ。

**既定を `console` のままにする理由**は、原因未特定の段階で画面（SC-04）という到達点を既定から降ろさないためである。
稼働環境だけを `tty` へ倒す。

### 3. 回帰は**構造**で測る（振る舞いでは測らない）

`console` 経路が tty 転送を張らないことを、FIFO を開いている `cat` の有無として `/proc` から測る（T-722-10）。
「入力が子へ届くか」を振る舞いで測る形は `script`/pty の起動タイミングで緑赤に揺れ、**変異（旧実装）を安定に
捕まえられなかった**（実測）。不変条件そのものを測る。

## 検討した選択肢

- **A: 本 ADR（モード選択 ＋ console から tty 転送を外す）** — 採用。
- **B: `tty` を既定にする** — 却下。画面 SC-04 を既定から降ろすことになる。原因未特定の段階で退行させない。
- **C: `cat` を全経路から消す** — 却下。`fifo` 直読み経路では `cat` が `kubectl attach` を成立させており、壊す理由が無い。
- **D: console 経路を撤去する** — 却下。#722 段 2 の到達点を捨てることになる。原因は未特定であって不可能ではない。

## 影響・結果

- 稼働環境は `OPEND_STDIN_MODE=tty` で運用する。**`kubectl set env` は Helm の管理外**であり、次回の
  `helm upgrade` で剥がれて既定（`console`）へ戻る。#730 の解決までは再デプロイのたびに再設定が要る。
- 本番描画（`helm template` 既定）は不変。

## 残余リスク

- **画面 SC-04 は使えないままである。** 原因未特定のため #730 に残す。
  有力な仮説は「OpenD が fd 0 ではなく制御端末（`/dev/tty`）を読んでおり、`script` 配下では制御端末が
  移っていない」で、`console` モードの `/proc/<pid>/stat` の `tty_nr` と fd 0 の突き合わせで判定できる
  （#730 のコメントに検査手順を残した）。
