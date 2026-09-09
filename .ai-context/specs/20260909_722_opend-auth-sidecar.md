---
title: OpenD の検証コード投入をサイドカー（OpendAuthGateway）越しに行えるようにする（#722 段 2）
issue: "#722"
plan_refs:
  - NFR
adr_refs:
  - ADR-0002
  - IADR-0053
  - IADR-0060
  - IADR-0320
status: in-progress
created: 2026-09-09
---

# 作業仕様書: OpenD 認証サイドカー（#722 段 2）

## 起点

- issue #722（NFR・無採番。計画の非機能要件表に「OpenD の再認証手段」に当たる番号は無い。
  `.claude/rules/traceability.md` の無採番を許す場合 2 に当たるため環流はしない）。
  ADR-0002「無人運用の成立性」／IADR-0053「常駐モデル」の運用面である。
- 段 1（`b504b809`・作業仕様書 `20260909_722_opend-stdin-fifo-for-screen-auth.md`）で、OpenD の標準入力を
  FIFO（`/run/opend/stdin`）にした。`kubectl exec` から検証コードが届くようになり、
  **Headlamp の Terminal がそのまま入力面になった。**
- 段 2 の起点は**利用者の決定（2026-09-09）**である ——「検証コードは **ai-stock-trading の SPA 画面**から
  入れられるようにする。Headlamp は入力面にしない」。輸送手段として **OpenD Pod へのサイドカー**を採る。

### 🔴 段 1 の作業仕様書は書き換えない

`.ai-context/README.md` は「作業仕様書は書いた時点の判断を凍結し、判断が変わった場合は
**新しい作業仕様書・PR で対応する**」と定める。段 1 の仕様書は「専用の HTTP 面・サイドカーは作らない」と
書いており、本 PR はその判断を**覆す**。したがって段 1 の本文には追記も訂正もせず、**本書を新設**した。
覆した理由は IADR-0320 に記録する。

## 対象範囲

### 対象

1. `deploy/opend/entrypoint.sh`: コンソールの複製（`script -q -e -f -a`）と画像 CAPTCHA の複写ループ。
2. `backend/Services/OpendAuthGateway/`: サイドカー本体（ASP.NET Core minimal API・net10.0）と xUnit 試験。
3. `deploy/opend/k8s/opend.yaml` / `deploy/helm/ai-stock-trading/templates/opend.yaml` ＋ `values.yaml`:
   共有 `emptyDir`（`/run/opend`）とサイドカーの配備。
4. `deploy/opend/entrypoint.test.sh`: 複製・複写・上限・パス導出の試験。
5. `scripts/k8s-local-images.sh`・`.github/workflows/helm.yml`: イメージのビルドと描画の検査。
6. 記録: 本書・IADR-0320・`.ai-context/adr/README.md`・`deploy/opend/README.md`。

### 対象外（この PR では作らない）

- 🔴 **SPA の画面と BFF のルート。** 裁定待ち（planning#594）である。**サイドカーだけを作る。**
  したがって本 PR の成果物は「配線されていない受け口」であり、`opend.authGateway.enabled` は
  **既定 false** で入る（fail-safe）。
- NetworkPolicy による「BFF からだけ到達可能」の強制。現状 chart に NetworkPolicy の枠が無く、
  ここで新設すると射程が広がる。名前空間内は相互到達可能であるという前提は IADR-0320 の残余リスクに書く。
- `deploy/opend/k8s/bootstrap-pod.yaml`（使い捨ての検証用 Pod）。常駐しないので投入面を持つ意味が無い。

## 母集合の引き直し（着手前・[[IADR-0141]] 規則 1〜6 / 本リポ規則 9・10）

**issue 本文の「反映先」は使わず、自分で引いた。** 誤りの側（＝いま Headlamp / attach を唯一の入力面として
書いている記述、および検証コード関連の識別子）から引いた。走査は 2026-09-09 の作業ブランチ上で実施。

| 軸 | 検索語（`git grep -lni`。パス除外のみ・拡張子で絞らない） | 件数 | 扱い |
| --- | --- | --- | --- |
| 1 | `/run/opend` | 0（develop 時点。段 1 マージ後は entrypoint 系のみ） | — |
| 2 | `input_phone_verify_code\|input_pic_verify_code\|req_phone_verify_code` | 10 | 下表 |
| 3 | `PicVerifyCode` | 4 | 下表 |
| 4 | `com.moomoo.OpenD` | 16 | 下表 |
| 5 | `[Hh]eadlamp` | 4 | 下表 |
| 6 | `opend`（広すぎる軸。ブローカー側の記述を大量に含む） | 約 230 | 軸 2〜5 の上位集合であることの確認にのみ用いた |

引いた結果の帰着（**除外したものと理由を明記する**。規則 6）:

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `deploy/opend/entrypoint.sh` / `entrypoint.test.sh` | **更新** | 本作業の実体 |
| `deploy/opend/k8s/opend.yaml` | **更新** | サイドカーと共有 emptyDir |
| `deploy/helm/.../templates/opend.yaml` / `values.yaml` | **更新** | 同上（chart 側） |
| `deploy/opend/README.md` | **更新** | 運用手順（画面／CLI／Headlamp の関係） |
| `scripts/k8s-local-images.sh` | **更新** | サイドカーのイメージが無いと Pod ごと NotReady になる |
| `.github/workflows/helm.yml` | **更新** | 既定 off・PVC 非マウント・Ingress 不在を描画で検査する |
| `deploy/opend/k8s/bootstrap-pod.yaml` | 除外 | 使い捨ての検証用 Pod。常駐しないので投入面を持つ意味が無い |
| `docs/operations/operations.md` L101 | 除外 | 段階 2→3 の切替手順における**初回の有人検証**の記述。初回は `attach` のままであり
（サイドカーは OpenD 起動後にしか使えない）、SPA 画面が入るまで運用手順は変わらない。**画面が入る PR で更新する** |
| `deploy/helm/.../values.yaml` L120 のコメント | 除外 | 同上（初回検証の注記） |
| `.ai-context/adr/IADR-0053` / `IADR-0060` | 除外 | **凍結記録**。本文を書き換えない（新 IADR-0320 で記録する） |
| `.ai-context/specs/2026*` 5 件 | 除外 | 同上（凍結記録。段 1 の仕様書を含む） |
| `docs/blocked-tasks.md` | 除外 | SPA / BFF の裁定待ちは**計画側 issue（planning#594）で追跡中**であり、
本リポの「実機・権限が要る作業」の一覧とは性質が違う。画面を作る PR で B 群へ起票するかを判断する |

**規則 10（是正で新たに誤りになる自分の記述を引き直す）**: 本 PR で「Headlamp がそのまま入力面になる」と
書いた箇所（段 1 で追加した `entrypoint.sh` のコメントと `README.md`）が、本 PR 以後は「唯一の入力面」ではなく
なる。`[Hh]eadlamp` の 4 件を引き直し、live な 2 件（`entrypoint.sh` / `README.md`）を是正した。
残る 2 件は凍結記録（`.ai-context/specs/`）であり触らない。

## 設計

### 1. コンソールの複製（`entrypoint.sh`）

サイドカーは `kubectl logs` を読めない（コンテナのログは Pod の外にある）。同一 Pod へ渡すには
**共有 emptyDir 上のファイル**にするのがいちばん素直である。

🔴 **`tee` は使えない。** OpenD の stdout がパイプになると C の stdio が行バッファから全バッファへ落ち、
`Command Tips` が 4KB のバッファに埋もれて `kubectl logs` にも attach にも出なくなる（段 1 の細部 3）。
**`script(1)` は疑似端末を与える**ので、子から見た fd 1 は依然として tty であり行バッファのままである。

実 OpenD コンテナで `script -q -f -c CMD FILE 0<> FIFO` の形を実測し、次の 4 点を確認済み（2026-09-09）:

- FIFO からの入力が子プロセスへ届く
- コンテナの標準出力にも従来どおり出る（`kubectl logs` が壊れない）
- 複製ファイルにも同じ出力が入る
- 子から `[ -t 0 ]` が真＝本物の tty に見える（C stdio が行バッファのまま）

`script` と `bash` は OpenD イメージ（Ubuntu 22.04 ベース）に存在する。`python3` と `socat` は無い。

**上限**: OpenD は週単位で常駐するので複製は際限なく積む。`OPEND_CONSOLE_MAX_BYTES`（既定 1MiB）を超えたら
`: > file` で切り詰める。🔴 **これが成立するのは `script -a`（O_APPEND）で開いているからである** ——
O_APPEND でないと `script` は自分のオフセットへ書き続け、切り詰めた直後のファイルが**NUL で埋まった穴あき
ファイル**になる（見かけのサイズが減らず、末尾を読むとゴミが混ざる）。

### 2. 画像 CAPTCHA の複写（`entrypoint.sh`）

🔴 **サイドカーに PVC をマウントさせない。** 画像の実体は `$HOME/.com.moomoo.OpenD/F3CNN/PicVerifyCode.png`
にあり、そこには**デバイス信頼の実体**（`Device.dat`）と `OpenD.xml`（ログイン資格情報の MD5）が同居する。
画像 1 枚のために口座の信頼状態を晒すことになる。**複写は OpenD 本体（PVC を持つ側）が行う。**

`$HOME` は chart の `opend.home` で可変（非 root 化で `/home/opend`）。`opend_captcha_source_path()` で
導出し、**`/root` を焼き付けない**。差し替えは同一ディレクトリ内の `mv`（rename＝不可分）で行うので、
読み手が半分書けた PNG を読むことはない。変化の判定は **mtime とサイズの両方**（同一秒内の差し替えを
取りこぼさない）。

### 3. サイドカー（`backend/Services/OpendAuthGateway/`）

公開するのは**ちょうど 3 本**。

| 口 | 役割 |
| --- | --- |
| `GET /opend-auth/state` | 整形済みのコンソール末尾（上限つき）／待たれているプロンプト／CAPTCHA の有無 |
| `GET /opend-auth/captcha` | `/run/opend/captcha.png` を `image/png` で返す。**固定パス・引数なし**。不在なら 404 |
| `POST /opend-auth/verify` | `{ "code": "..." }` **のみ**。種別は受け取らず、**待機中のプロンプトからサーバが決める** |
| `POST /opend-auth/resend` | 本文なし。`req_phone_verify_code`（引数なし）を書く |

安全要件（すべて試験を持つ。詳細は IADR-0320）:

1. **クライアントはコマンド文字列を渡さない。** 書かれ得る行は 3 つだけで、サーバが組み立てる。
2. **照合はアンカー付き全一致。** `\A[0-9]{4,8}\z` / `\A[A-Za-z0-9]{4}\z`。
   🔴 `^…$` は使わない（.NET の `$` は末尾 `\n` の直前にも一致する＝**改行注入が素通りする**）。
   🔴 `\d` は使わない（Unicode 数字に一致する）。
3. **fd をキャッシュしない。** 要求ごとに `O_WRONLY | O_NONBLOCK` で開く。`ENXIO`（読み手不在）は
   待たずに 503。再起動後の孤児 inode へ書き続ける事故を構造的に塞ぐ。
4. **1 行を 1 回の `write`** で書く（`PIPE_BUF` 以下＝不可分）。
5. **流量制限**（既定 5 件 / 60 秒・構成可能）。**サービス全体で数える**（守る資源が全体で 1 つ）。
   検証より**後**に置く（壊れた要求で正当な枠を使い切れないようにする）。
6. **コードを記録しない・返さない。** 応答にも載せず、ログにも出さず、
   `GET /state` が返すコンソール末尾では `-code=` / `-login_pwd=` の値を伏せ字にする
   （`script` の記録には運用者が打った行がそのまま残るため）。
7. **Ingress も Route も TLS も持たない。** Pod 網にだけ bind し、呼び出し元はクラスタ内の BFF だけである。

**なぜここまで締めるか**: OpenD のコンソールは `relogin -login_pwd=`・`exit`・`close_api_conn`・
`set_log_level`・`show_delay_report -detail_report_path=<path>`・`show_sub_info -sub_info_path=<path>` も
受け付ける。後ろ 2 つは **root 権限で呼び出し側が選んだパスへファイルを書く**（デバイス信頼の実体や
`OpenD.xml` を潰せる）。**濾過されない 1 行が通れば、それだけで実口座に対する重大な事故になる。**

### 4. 配備

- 共有 `emptyDir`（`sizeLimit: 16Mi`）を `/run/opend` に張り、**OpenD 本体には常に**、
  サイドカーには有効時にだけマウントする。**サイドカーは `opend-persist` を絶対にマウントしない。**
- 既存の fail-safe 既定は不変（`opend.enabled=false`・単一レプリカ・Recreate・liveness 無し・stdin/tty）。
  サイドカー自体も **`opend.authGateway.enabled=false` が既定**である。
- サイドカーの `securityContext` は OpenD 本体と**同じ値**を与える（共有ファイルは umask 077 で作られるため、
  uid がずれると非 root 化した瞬間に静かに読めなくなる）。
- Service `opend` に ClusterIP のポート（`auth`/8080）を足す。**Ingress も NodePort も作らない。**

## 受け入れ基準

- [x] `entrypoint.sh` がコンソールの複製を `/run/opend/console.log` へ落とし、標準出力も従来どおり出る
- [x] 複製に上限があり、超えたら切り詰める（`script -a` により穴あきにならない）
- [x] 画像 CAPTCHA が `$HOME` から導いたパスから `/run/opend/captcha.png` へ複写される（`/root` 直書きなし）
- [x] サイドカーが 3 本ちょうどを公開し、コマンド文字列を受け取る口が無い
- [x] 壊れた入力（CR/LF/NUL・非 ASCII 数字・前後空白・長すぎ・種別違反）はすべて 400 で、**1 バイトも書かれない**
- [x] 読み手不在（`ENXIO`）で 503。塞がらない
- [x] 流量制限が効き、構成できる
- [x] コードが応答にもログにも `GET /state` にも現れない
- [x] chart / 生 manifest ともサイドカーが PVC をマウントしない。Ingress を作らない
- [x] 既定描画にサイドカーが現れない（fail-safe）
- [ ] 実クラスタでの疎通（BFF ↔ サイドカー、画面からの投入）。**SPA / BFF は planning#594 の裁定待ちで未着手**

## 試験

- `backend/Services/OpendAuthGateway/Tests/`（xUnit v3 + AwesomeAssertions）:
  allowlist の網羅（受理された出力が 3 形のいずれかであること）・棄却ごとの「何も書かれない」・
  実 FIFO に対する往復（Linux のみ・非 Linux は skip）・FIFO 作り直し後も届くこと（fd 非キャッシュ）・
  流量制限・伏せ字・上限・固定パス。
- `deploy/opend/entrypoint.test.sh`: T-722-05〜09 を追加（複製・持ち越し無し・上限・複写・パス導出）。

### 🔴 既存 T-722-01〜04 の偽陽性／偽陰性の是正（本 PR で発見）

段 1 の探針（`fifo_mechanism_works`）は**1 回だけ**機序を走らせて判定していた。MSYS（Git Bash）では
同じ探針が**走らせるたびに緑と赤へ振れる**ため、たまたま成立と判定された回に群が実行され、
**偽の赤**が出る。段 1 の試験を `b504b809` のまま 5 回連続で走らせた実測:

```
base run 1: 7 passed, 1 failed  (T-722-03 NG)
base run 2: 3 passed, 0 failed  (群ごと skip)
base run 3: 3 passed, 0 failed  (群ごと skip)
base run 4: 7 passed, 1 failed  (T-722-03 NG)
base run 5: 6 passed, 2 failed  (T-722-01/02 NG)
```

**探針を「3 回連続で成立したときだけ成立と見なす」へ強化した。** 決定的に成立する Linux は従来どおり
実行され、確率的にしか通らない環境は安定して skip へ落ちる。強化後の 5 回連続は 0 failed。

## 残余リスク

- 名前空間内の他 Pod からもサイドカーへ到達できる（NetworkPolicy 未整備）。IADR-0320 に記録。
- `script` が無いイメージでは複製を諦めて従来どおり起動する（OpenD を上げないほうが害が大きい）。
  そのとき画面からの投入は使えない。警告を stderr へ出す。
- サイドカーは `kubectl exec`／`attach` の経路を**置き換えない**。両方が生き続ける
  （FIFO へ書ける主体は「`pods/exec` を持つ者」と「サイドカー」の 2 つになる）。
- 実クラスタでの疎通は未実施。SPA / BFF が入る PR で行う。
