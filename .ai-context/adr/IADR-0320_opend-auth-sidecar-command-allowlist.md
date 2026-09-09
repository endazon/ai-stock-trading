---
title: IADR-0320 OpenD の検証コード投入はサイドカー経由にし、書ける行を閉じた 3 コマンドの allowlist に限る
type: impl-adr
status: Accepted
related_ids:
  - NFR
author: 実装担当
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - NFR
---

# IADR-0320: OpenD の検証コード投入はサイドカー経由にし、書ける行を閉じた 3 コマンドの allowlist に限る

- 状態: Accepted
- 日付: 2026-09-09
- 決定者: 実装担当（輸送手段の選択は利用者裁定・2026-09-09）

## 起点・関連

- 関連する計画書 ID: NFR（無採番。計画の非機能要件表に「OpenD の再認証手段」に当たる番号は無い。
  `.claude/rules/traceability.md` の無採番を許す場合 2 に当たるため環流はしない）
- 関連する計画 ADR: `ADR-0002`（moomoo OpenD 経由の発注・無人運用の成立性）
- 関連する実装 ADR: [IADR-0053](./IADR-0053_moomoo-opend-dockerization.md)（常駐モデル・有人デバイス検証）・
  [IADR-0060](./IADR-0060_opend-production-cutover-gates.md)（本番配備のゲート・非 root 化のオプトイン）・
  [IADR-0167](./IADR-0167_opend-unattended-restart-followup.md)（liveness を付けない理由）
- 関連する実装仕様書: [20260909_722_opend-auth-sidecar](../specs/20260909_722_opend-auth-sidecar.md)
  （段 1 は [20260909_722_opend-stdin-fifo-for-screen-auth](../specs/20260909_722_opend-stdin-fifo-for-screen-auth.md)）
- 起点 issue: [#722](https://github.com/endazon/ai-stock-trading/issues/722)

## コンテキストと課題

OpenD はログイン時の検証コード（SMS / 画像 CAPTCHA）を **PID 1 の標準入力**から読む。
段 1（`b504b809`）でその標準入力を FIFO（`/run/opend/stdin`）にしたため、`kubectl exec` からも届くようになり、
**既配備の Headlamp の Terminal がそのまま入力面になった**。

段 1 の作業仕様書と IADR に相当する記述は、**専用の HTTP 面・サイドカーは作らない**と明記していた。
理由は「OpenD のコンソールは検証コード以外の強いコマンドを受け付けるため、標準入力への書き込み面は
実口座に対する強い権限であり、自前のトークン検査で守る対象ではない。exec 経由なら apiserver の RBAC が
そのまま効く」であった。

**2026-09-09、利用者がこれを覆した** —— 検証コードは **ai-stock-trading の SPA 画面**から入れられる
ようにする。Headlamp は入力面にしない。運用者に kubeconfig と Kubernetes の知識を要求しないためである。

そこで「SPA（＝BFF）から OpenD の標準入力へ 1 行届ける」輸送手段を決める必要が生じた。
同時に、段 1 が挙げた危険 —— OpenD のコンソールが受け付ける次のコマンド群 —— は消えていない。

| コマンド | 何ができるか |
| --- | --- |
| `show_delay_report -detail_report_path=<path>` | **root 権限で呼び出し側が選んだパスへファイルを書く** |
| `show_sub_info -sub_info_path=<path>` | 同上 |
| `relogin -login_pwd=<pwd>` | 信頼済み端末・IP からのパスワード試行 |
| `exit` / `close_api_conn` | 口座への窓口を落とす／API 接続を切る |
| `set_log_level` | 記録の粒度を落とす |

上 2 つは**デバイス信頼の実体**（`$HOME/.com.moomoo.OpenD/F3CNN/Device.dat`）や `OpenD.xml`
（ログイン資格情報の MD5 を含む）を上書きできる。**濾過されない 1 行が通れば、それだけで
実口座に対する重大な事故になる。**

## 検討した選択肢

| 案 | 概要 | 評価 |
| --- | --- | --- |
| A. Headlamp を使い続ける（段 1 の据え置き） | 追加実装ゼロ。認可は apiserver の RBAC | **利用者が明示的に否定した。** 運用者に kubeconfig・Kubernetes の知識・cluster-admin 相当の権限が要る |
| B. BFF が Kubernetes API の `pods/exec` を叩く | サイドカー不要。認可は RBAC のまま | **BFF に `pods/exec` を与える**ことになる。exec は**任意のコマンドを実行できる**権限であり、BFF が侵害された場合の被害はクラスタ全体に及ぶ。「コンソールへ 1 行書く」ために与える権限として桁が違う。加えて exec はストリーミング（SPDY / WebSocket）で、BFF に Kubernetes クライアントと kubeconfig の管理を持ち込む |
| C. **OpenD Pod にサイドカーを同居させ、BFF から HTTP で呼ぶ**（採用） | 共有 `emptyDir` 越しに FIFO へ書く最小の HTTP 面 | 与える権限が「**閉じた 3 コマンドを書くこと**」だけに縮む。Kubernetes API を一切使わない。攻撃面はサイドカーのコードに閉じ、そこは全部読める大きさである |
| D. OpenD コンテナ内に HTTP サーバを同居させる | Pod もサイドカーも増えない | OpenD イメージにランタイムを足すことになる（`python3` も `socat` も無く、`.NET` を入れると肥大する）。OpenD の再ビルドが要り、ベンダ由来のイメージへ手を入れる面が増える |

## 決定

### 決定 1: 輸送手段はサイドカーとし、安全の主たる統制は **allowlist** に置く（案 C）

`backend/Services/OpendAuthGateway/`（ASP.NET Core minimal API・net10.0・依存は Web SDK のみ）を
OpenD Pod へ同居させ、共有 `emptyDir`（`/run/opend`）越しに標準入力 FIFO へ書く。

🔴 **クライアントはコマンド文字列を渡さない。** 渡すのは閉じた列挙（`phone` / `pic` / `resend`）と
コードだけで、実際に書かれる行は `OpendConsoleCommand.TryCompose` が組み立てる。
**書かれ得る行は次の 3 つで、それ以外はこの型では表現できない。**

1. `input_phone_verify_code -code=<4〜8 桁の ASCII 数字>`
2. `input_pic_verify_code -code=<4 文字の ASCII 英数>`
3. `req_phone_verify_code`（引数なし）

**認証ではなく allowlist が主たる統制である。** 案 B が持っていた「apiserver の RBAC」という
既存の信頼の基点を手放す代わりに、**渡せる権限そのものを 3 行に縮める**。
権限を絞ったうえで認証を BFF に委ねるほうが、権限を絞らずに認証を足すより安全側である。

### 決定 2: 検証はアンカー付き全一致、書き込みは要求ごとに `O_WRONLY | O_NONBLOCK`

- 🔴 **`^…$` を使わない。** .NET の `$` は**末尾の `\n` の直前にも一致する**ため、`^[0-9]{4,8}$` は
  `"123456\n"` を通す ——**改行注入がそのまま素通りする**。`\A…\z` を使う。
- 🔴 **`\d` を使わない。** .NET の `\d` は Unicode の数字（全角・アラビア数字等）にも一致する。
  `[0-9]` / `[A-Za-z0-9]` と書いて ASCII に閉じる。
- 長さは照合前に切る。要求本文にも上限（既定 512 バイト）を置き、**エンドポイント側で独立に**数える
  （Kestrel の上限だけに頼ると、`TestServer` を使う試験では検査できない）。
- 🔴 **fd をキャッシュしない。** OpenD が再起動すると `entrypoint.sh` は FIFO を消して作り直すため、
  保持した fd は**誰も読まない孤児の inode** を指す。書き込みは成功したように見えて OpenD には
  永久に届かない。要求ごとに開いて閉じる。
- 🔴 **`O_NONBLOCK` を付ける。** 素の `O_WRONLY` は読み手が現れるまで `open` が塞がり、
  OpenD が落ちている間の要求が呼び出し元（BFF）のスレッドを道連れにする。`ENXIO` は 503 へ写す。
- 1 行を **1 回の `write`** で書く（`PIPE_BUF` 以下＝不可分。他の書き手と行が混ざらない）。

### 決定 3: コンソールは `script(1)` で複製する（`tee` ではない）

サイドカーは `kubectl logs` を読めない。共有 `emptyDir` 上のファイルとして渡す。

🔴 **`tee` を挟むと OpenD の stdout がパイプになり、C の stdio が行バッファから全バッファへ切り替わる。**
`Command Tips` が 4KB のバッファに埋もれ、`kubectl logs` にも `attach` にも出なくなる。
`script` は疑似端末を与えるので、子から見た fd 1 は**依然として tty** であり行バッファのままである。
実 OpenD コンテナで `script -q -f -c CMD FILE 0<> FIFO` の形を実測し、
(a) FIFO の入力が子へ届く (b) 標準出力も従来どおり出る (c) 複製ファイルにも入る
(d) 子から `[ -t 0 ]` が真、の 4 点を確認した（2026-09-09）。

🔴 **`-a`（O_APPEND）を付ける。** これが**上限（1MiB 既定で切り詰め）を成立させている**。
O_APPEND でないと `script` は自分が数えているオフセットへ書き続け、切り詰めた直後のファイルが
**NUL で埋まった穴あきファイル**になる（見かけのサイズが減らず、末尾を読むとゴミが混ざる）。

### 決定 4: 流量制限は**サービス全体**で数え、検証の**後**に置く

守っている資源が全体で 1 つしかない —— moomoo の SMS 送信枠（`resend` が消費する）と、
OpenD のコンソール（投入した行は次のプロンプトが消費するので、束ねて投げると順序が壊れる）。
呼び出し元ごとに数えると、呼び出し元を増やすだけで全体の上限が上がってしまう。

検証を流量制限より**前**に置く。壊れた要求は 1 バイトも書かない＝資源を消費しないので、
枠を消費させると「壊れた要求を投げ続けるだけで正当な投入を締め出せる」ことになる。
既定は 5 件 / 60 秒で、`opend.authGateway.rateLimit*` で構成できる。

### 決定 5: コードは記録しない・返さない。コンソール末尾では伏せ字にする

応答にも載せず（棄却理由は符号だけ）、ログにも出さない（棄却は理由の列挙値のみ）。

🔴 **`GET /state` が返すコンソール末尾でも伏せる。** `script` は tty の記録なので、
運用者が打った `input_phone_verify_code -code=123456` が**そのまま複製に残る**。
何もしなければ状態照会が**コードを echo する**ことになる。`-code=` と `-login_pwd=` の値を
`***` へ置換する（キー名は残す ——`Command Tips` 行のコマンド名がプロンプト判定に要る）。

### 決定 6: サイドカーは PVC を見ない。Ingress・Route・TLS を持たない

- 🔴 **`opend-persist`（PVC）を絶対にマウントしない。** 画像 CAPTCHA の実体は PVC 上にあるが、
  そこには**デバイス信頼の実体**（`Device.dat`）と `OpenD.xml` が同居する。画像 1 枚のために
  口座の信頼状態を晒すことになる。**複写は PVC を持つ側（OpenD 本体）の役目**とし、
  サイドカーは共有 `emptyDir` 上の写しだけを読む。
- 画像の取得口は**固定パス・引数なし**。呼び出し側が読む対象を選べるようにすると、PVC の読み出しへ一歩で繋がる。
- **Ingress も Route も TLS も持たない。** Pod 網にだけ bind し（`ASPNETCORE_URLS=http://+:8080`）、
  Service には ClusterIP のポートを 1 本足すだけである。呼び出し元はクラスタ内の BFF だけで、
  利用者認証はその手前で済んでいる。TLS はメッシュ参入時に Envoy が持つ（`IADR-0314`。ただし OpenD Pod は
  既定でサイドカー注入対象外なので、現状は平文である）。
- 配備は **`opend.authGateway.enabled=false` が既定**（fail-safe）。SPA / BFF が入るまで面を開けない。
- サイドカーの `securityContext` は OpenD 本体と**同じ値**を与える。共有ファイルは OpenD 本体が
  `umask 077` で作るため、uid がずれると非 root 化した瞬間に静かに読めなくなる。

## 理由

段 1 の「専用の HTTP 面を作らない」は、**その面が任意のコマンドを通す前提**で書かれていた。
本 IADR が変えたのはそこである —— 面が通すのを 3 行に固定すれば、面そのものが持つ権限が
「検証コードを入れること」以上に広がらない。案 B（`pods/exec`）はこの縮小ができない。

## 影響・残余リスク

- **名前空間内の他 Pod からサイドカーへ到達できる。** chart に NetworkPolicy の枠が無く、
  ここで新設すると射程が広がるため入れていない。到達できたとしてできることは
  「3 コマンドを流量制限つきで投げる」だけだが、`resend` の連打で SMS 枠を消費させることはできる。
  NetworkPolicy（BFF からのみ許可）は後続。
- **FIFO へ書ける主体が 2 つになる**（`pods/exec` を持つ者と、サイドカー）。
  `kubectl attach` / `exec` の従来経路は意図的に残す（サイドカーが落ちても再認証できるようにするため）。
- **`script` が無いイメージでは複製を諦めて従来どおり起動する**（OpenD を上げないほうが害が大きい）。
  そのとき画面からの投入は使えない。警告を stderr へ出す。
- **SPA の画面と BFF のルートは未着手**（planning#594 の裁定待ち）。本 PR の成果物は
  「配線されていない受け口」であり、既定 false で入る。
- **実クラスタでの疎通は未実施。** 実測済みなのは `script` の 4 性質（OpenD コンテナ内）までである。
