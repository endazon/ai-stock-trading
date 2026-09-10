---
title: OpenD の標準入力を FIFO 経由にし、検証コードを画面（Headlamp）から入れられるようにする
issue: "#722"
plan_refs:
  - NFR
adr_refs:
  - ADR-0002
  - IADR-0053
  - IADR-0060
status: done
created: 2026-09-09
---

# 作業仕様書: OpenD の標準入力を FIFO 経由にする（#722）

## 起点

- issue #722（NFR・無採番。ADR-0002「無人運用の成立性」／IADR-0053「常駐モデル」の運用面）。
- 実測（2026-09-09）: 稼働中の OpenD は `Command Tips: input_phone_verify_code -code=123456` で停止しており、
  入力手段は `kubectl attach -it deploy/opend` のみ。`/proc/1/fd/0 -> /dev/pts/0` のため
  **`kubectl exec` では PID 1 の標準入力へ書けない**。
- 利用者裁定（2026-09-09）: k3s は最後に完全アンインストールする（`opend-persist` も破棄する）。
  したがって**再認証は必ず起きる**。その再認証を画面から行えるようにすることが本作業の目的である。

## 設計

**標準入力だけを FIFO へ移す。** それ以外は何も足さない。

```
FIFO=/run/opend/stdin
mkdir -p /run/opend && unlink "$FIFO" 2>/dev/null; mkfifo -m 600 "$FIFO"
( exec cat > "$FIFO" ) <&0 &      # tty → FIFO
exec ./OpenD 0<> "$FIFO"
```

| 決定 | 理由 |
| --- | --- |
| 入力面は **既配備の Headlamp**（`headlamp.localhost`）を使う | FIFO にした時点で `pods/exec` が標準入力へ届く。Headlamp は OIDC・TLS 終端・`oidc:developer → cluster-admin` の束縛が既に live である。**新しい信頼の基点を作らない** |
| 専用の HTTP 面・サイドカーを**作らない** | OpenD のコンソールには `show_delay_report -detail_report_path=<path>` など **root 権限で任意パスへ書けるコマンド**がある。標準入力への書き込みは実口座に対する強い権限であり、自前のトークン検査で守る対象ではない。exec なら認可は apiserver の RBAC がそのまま効く |
| FIFO は**作る前に消す** | `emptyDir` はコンテナ再起動を跨いで残る。`set -euo pipefail` の下で EEXIST になると OpenD が CrashLoop する（稼働 Pod は既に 5 回再起動している） |
| **保持用の書き手プロセスを置かない**（`0<>` を使う） | 保持プロセスが死ぬと OpenD が EOF を見て終了し、再起動 → SMS 再送になる。`O_RDWR` なら読み手自身が書き手なので EOF が来ない |
| **fd 1 / 2 は pty のまま**にする | `tee` を挟むと C の stdio が全バッファリングへ切り替わり、`Command Tips` が 4KB バッファに埋もれて `kubectl logs` にも attach にも出なくなる |
| `kubectl attach` の既存手順を**残す** | tty を FIFO へ流す背景プロセスを置くことで両方が同じ標準入力へ届く。既存の運用手順書を無効化しない |

### 採らなかった案

- **専用サイドカー ＋ HTTP 面 ＋ `opend.localhost` の Ingress**（当初案）。FIFO 化だけで exec が届く以上、
  新しい能力を何も足さないまま攻撃面・実装・クロスリポの証明書配線を増やすだけになる。
  認可も `TokenReview` ＋ `SubjectAccessReview` を自前で組み直すことになり、RBAC の再実装に等しい。
- **Headlamp の attach 機能を使う**。v0.43.0 のフロントエンドに pod attach の UI は無い（バンドル走査で確認）。

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `deploy/opend/entrypoint.sh` | 末尾の `exec ./OpenD` を FIFO 経由へ。関数 `start_opend_with_fifo` として切り出し、試験から呼べるようにする |
| `deploy/opend/entrypoint.test.sh` | FIFO 経路の試験を追加（既存の `AST_OPEND_LIB=1` idiom） |
| `deploy/opend/README.md` | 画面からの手順を追記。**111 行目の再送コマンドの誤り**（`relogin` → `req_phone_verify_code`）を訂正 |

`deploy/opend/k8s/opend.yaml` と chart の `templates/opend.yaml` は**変えない**。`/run` は tmpfs でなくてよく、
コンテナのファイルシステムに作るので volume の追加は要らない（サイドカーと共有しないため）。

## 受け入れ基準

- [x] FIFO へ 1 行書くと OpenD へ届く（実 OpenD で実測）
- [x] `kubectl attach` の既存手順が従来どおり動く
- [x] コンテナを再起動しても EEXIST で落ちない
- [x] `kubectl logs` に `Command Tips` が従来どおり出る
- [ ] Headlamp の Terminal から実際に検証コードを入れてログインできる（**利用者の手が要る。SMS は利用者の端末へ届く**）
- [x] `bash deploy/opend/entrypoint.test.sh` が全緑
- [x] README の再送コマンドの誤りが直っている

## ［2026-09-10 追記 / #722］着地と実機での確認

PR AST#723 で着地し、稼働 k3s へ配備した。**7 項目中 6 項目を実測で確認した。**

| 確認したこと | 実測 |
| --- | --- |
| FIFO へ書いた行が届く | Linux コンテナで実測。`exec` 経路・`tty` 経路の両方 |
| `kubectl attach` を壊していない | 同上（tty→FIFO の複写経路） |
| 再起動で EEXIST に落ちない | 残存 FIFO を置いた状態から起動して確認 |
| `kubectl logs` に `Command Tips` が出る | 稼働 Pod のログで確認（`script` の pty により行バッファのまま） |
| 試験が全緑 | Linux で 6 回連続 41 件・skip ゼロ。Windows は 3 回とも失敗ゼロ |
| README の訂正 | 再送は `relogin` ではなく `req_phone_verify_code` |

**配備後の Pod で `/proc/1/fd/0 -> /run/opend/stdin` を確認済み。**

残る 1 項目は**利用者の手が要る** —— SMS は利用者の端末へ届くため、代わりに入力できない。

## 計画書との差異

差異なし。ADR-0002 の「初回のみ有人」という結論も、IADR-0053 の常駐モデルも変えない。
**変えるのは有人操作の入力面だけ**である。

## 未決事項・残余リスク

- **OpenD が非 tty の標準入力からコマンドを受けるかは未実測である。** 受けない場合、本方式は成立しない。
  実 OpenD で確かめるまで結論を書かない。確かめる手段は `deploy/opend/k8s/bootstrap-pod.yaml`。
- 画像 CAPTCHA が要求された場合、画像は PVC 上（`$HOME/F3CNN/PicVerifyCode.png`）にあり、
  Headlamp の Terminal からは `base64` で取り出して読む必要がある。手順を README に書く。
- 検証コードには有効期限がある。FIFO に溜めた古いコードが次のプロンプトで消費され、
  再送後の 1 回を食う経路がある。運用注意として README に書く。
