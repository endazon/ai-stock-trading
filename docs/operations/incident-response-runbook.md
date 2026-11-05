---
title: 運用 Runbook — 障害対応（検知・初動・証跡の保全・資格情報の入れ替え・復旧・連絡・振り返り）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
---
<!-- trace:
ids: [NFR, NFR-03, NFR-05, NFR-06, NFR-10, NFR-18, FR-09, FR-10, FR-11, UC-06]
adrs: [ADR-0002, ADR-0006, ADR-0009, ADR-0024]
iadrs: [IADR-0613, IADR-0060, IADR-0062, IADR-0098, IADR-0341, IADR-0566]
specs: [20261105_1530_out-of-band-stop-and-incident-runbook]
issues: [#1530, MSP#2402]
-->
<!-- 起点 ID・関連 ADR/IADR・仕様書名・修飾付き issue 参照は本文へ書かず、上の trace ブロックへ入れる（scripts/check-trace-blocks.js が検査する） -->

# 運用 Runbook: 障害対応

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。** 個々の症状の切り分け（どのログを見るか）は運用仕様書の障害対応の表にあり、
> 本書は**障害が起きてから閉じるまでの流れ**を定める。止める操作の詳細は [Discord を経ない取引の停止 Runbook](out-of-band-trading-stop-runbook.md) にある。
>
> 🔴 **本書の手順はすべてオーナーが行う。** 稼働クラスタ・OpenD・証券会社・Discord・Keycloak・Vault の操作と、秘密の値の入力は、AI（コーディングエージェント）は行わない。
> AI に頼めるのは、手元にコピーした証跡（ログの写し等）の読み取りと、振り返りの下書きだけである。

## 原則

1. **先に止める、調べるのは後。** 資産を守る向き（新規建ての停止）は迷ったら実行する。止める操作は手仕舞い・損切りを止めない。
2. **消す前に写す。** Pod の削除・再起動・資格情報の入れ替えは証跡を消す。止めた直後に、§証跡の保全 を先に行う。
3. **秘密の値を表示しない・書き残さない。** 入れ替えた値はチャット・issue・振り返りに書かない。証跡の写しからも伏せる。
4. **再開は止めるより重い。** 再開（kill switch の解除）は、原因と影響の範囲を確かめ、資格情報を入れ替えてから行う。

## 重さの区分（最初の 5 分で決める）

| 区分 | 例 | 初動 |
| --- | --- | --- |
| **S1（資産に直結）** | 想定しない発注・約定。Discord・証券会社・ノードのいずれかのアカウントを他人に使われている疑い。資格情報の漏れ | §初動 の 1〜4 をすべて。§資格情報の入れ替え まで終えてから再開 |
| **S2（統制の不全）** | 損切り・保護の逆指値が働いていない疑い。kill switch が効いたと確かめられない。OpenD が落ちて建玉の照会ができない | §初動 の 1・2。保有がある間は証券会社のアプリで保護を確かめる |
| **S3（機能の停止）** | 報告書が作られない・情報収集が止まった・Discord の通知が届かない（発注と統制は正常） | 止めなくてよい。運用仕様書の障害対応の表で切り分ける |

## 1. 検知

| 経路 | 何で気付くか | 系の停止に耐えるか |
| --- | --- | --- |
| 外部の死活監視（heartbeat の途絶） | 外部の監視サービスからの通知（Discord 以外の経路を 1 つ登録しておく） | **耐える**（クラスタの外にある。基盤の作業で配備される。登録はオーナー） |
| 基盤の警報（Alertmanager） | Discord の警報チャンネル（＋登録した別の経路） | クラスタが生きている間だけ |
| AST の業務の警報 | 同上（ルールは `deploy/observability/alerts/`） | 同上 |
| 業務の通知（統制の発動・発注・約定・エラー） | Discord の専用チャンネル | 通知サービスが生きている間だけ |
| **証券会社のアプリの約定通知** | スマートフォンの通知 | **耐える**（本システムと独立。必ず有効にしておく） |
| 夜間の台帳の要約 | 翌朝の確認（[夜間の台帳の要約 Runbook](nightly-ledger-summary-runbook.md)） | 台帳が残っていれば |
| SSH からの状態確認 | `bash scripts/ast-trading-stop.sh status` | ノードが生きている間 |

- 🔴 **Discord の通知が来ないことは「正常」を意味しない。** 通知サービス・クラスタ・Discord のどれかが止まっていても同じに見える。
- 気付いた時刻・経路・最初に見た内容を、§記録 の様式へすぐ書く（後で時系列を組むため）。

## 2. 初動（止める・切り離す）

S1 は 1〜4 をすべて、S2 は 1・2 を行う。

1. **新規建てを止める。**
   - Discord が使え、アカウントを使われている疑いが無い: `/killswitch`（確認ボタン＋確認フレーズ）。
   - Discord が使えない・アカウントを使われている疑いがある: SSH から `bash scripts/ast-trading-stop.sh --live hold --reason "<理由>"`（[停止の Runbook](out-of-band-trading-stop-runbook.md) 手順 2）。**保持の間は Discord から解除できない。**
   - どちらも効いたと確かめられない: 同 Runbook の「最後の手段」（証券会社のアプリで未約定を取り消す → 発注執行を 0 台 → ログインのパスワードを変える）。
2. **保有と保護を証券会社のアプリで確かめる。** 建玉・未約定の注文・逆指値の有無を見る。保護の無い建玉があれば、アプリで逆指値を置くか手仕舞う（本システムを経ない）。
3. **Discord を切り離す**（Discord のアカウントを使われている疑いがあるとき）: `bash scripts/ast-trading-stop.sh --live isolate-discord`（ArgoCD の自動同期の扱いは停止の Runbook 手順 3）。Discord のアカウントのパスワードを変え、2 要素認証の設定と、ログイン中の端末（セッション）を確かめて、心当たりの無いものを切る。
4. **疑わしい経路を断つ**（どこから入られたかが分からないとき）:
   - 証券口座が疑わしい: §OpenD と証券口座 の「口座が疑わしいとき」。
   - ノードが疑わしい: VPS の管理画面で、ファイアウォールの SSH の許可元を自分の接続元だけに絞る。不明な SSH 鍵（`~/.ssh/authorized_keys`）を消す前に §証跡の保全 を行う。
   - 基盤（Keycloak・Vault）が疑わしい: 基盤リポジトリの手順書に従う（本書の外）。

## 3. 証跡の保全（消す前に写す）

止めた直後に、Pod を作り直す前に行う。写しはノードの外（手元の端末の暗号化した場所）へ置く。**写しの中の秘密の値は伏せる。**

```bash
NS=ai-stock-trading
OUT="incident-$(date -u +%Y%m%dT%H%M%SZ)"; mkdir -p "$OUT"; chmod 700 "$OUT"
K="k3s kubectl"
$K -n "$NS" get pods -o wide > "$OUT/pods.txt"
$K -n "$NS" get events --sort-by=.lastTimestamp > "$OUT/events.txt"
for d in risk-management-service order-execution-service trade-decision-service notification-service market-monitor-service opend; do
  $K -n "$NS" logs "deployment/$d" --all-containers --since=48h > "$OUT/$d.log" 2>&1
  $K -n "$NS" logs "deployment/$d" --all-containers --previous > "$OUT/$d.previous.log" 2>&1 || true
done
# Secret は中身を写さない。名前と更新時刻だけ。
$K -n "$NS" get secrets -o custom-columns=NAME:.metadata.name,CREATED:.metadata.creationTimestamp > "$OUT/secrets-names.txt"
```

- **ログの保持は短い**（Pod の再起動・作り直しで消える）。上を先に行う。
- 台帳と監査の記録（監査ログ・設定の変更履歴・発注の台帳）は DB に残る。§復旧 の前に、運用仕様書の「切替前後の件数突合」の手順（`scripts/cutover-count-reconcile.sh snapshot`）で件数と指紋を写しておくと、後で消えていないことを確かめられる（読み取りだけ）。
- ノード: `last`・`journalctl -u ssh --since "-48h"`（または `/var/log/auth.log`）を写す。VPS の管理画面で**スナップショット**を取る（費用は小さい。調べ終えたら消す）。
- Discord: 専用チャンネルのメッセージ（コマンドの応答・通知）を画面の写しで残す。ボットのコマンドの記録は監査ログにも残る。
- 証券会社: アプリの注文・約定の履歴を画面の写しで残す（注文番号・時刻・数量）。

## 4. 資格情報の入れ替え

漏れた・漏れた疑いがある資格情報だけでなく、**同じ場所に置かれていたもの**を入れ替える（ボットの Pod が疑わしいなら、その Pod が読める鍵すべて）。
値は Vault（`ai-stock-trading/app-secrets` ほか）で変え、ExternalSecret が同期し、Reloader が Pod を作り直す（[Vault 秘匿 Runbook](vault-secrets-runbook.md)）。

| 資格情報 | どこで作り直すか | Vault のキー（同期先） | 手順 |
| --- | --- | --- | --- |
| Discord の Bot のトークン | Discord の開発者ポータル（Bot → Reset Token） | `discord-bot-token`（`ast-secrets`） | 作り直すと旧い方は即座に無効になる。新しい値を Vault へ書く |
| Discord の Webhook の URL | Discord（新しい Webhook を作り、旧い方を消す） | `discord-webhook-url` | [Discord Webhook 再発行 Runbook](discord-webhook-rotation-runbook.md) |
| 確認フレーズ | オーナーが決める | `discord-bot-killswitch-phrase` | 新しい語を Vault へ書く。語はどこにも書き残さない |
| ボットの所有者の機密クライアントの secret | Keycloak（クライアント `ai-stock-trading-owner` の資格情報の再生成） | `discord-owner-auth-client-secret` | 基盤リポジトリの秘密情報のローテーション手順書（`docs/operations/secret-rotation-runbook.md` の「認証基盤と対で回す」）に従う。**Keycloak の再生成と Vault の書き込みの間は、ボットの操作が失敗する**（止める向きは SSH の経路を使う） |
| サービス間の機密クライアントの secret（s2s・KB・LLM） | Keycloak | `service-auth-client-secret`・`kb-auth-client-secret`・`kb-reader-auth-client-secret`・`llm-auth-client-secret` | 同上 |
| 外部の API の鍵（Finnhub・FRED・EDINET ほか） | 各サービスの管理画面 | `finnhub-api-key`・`marketdata-finnhub-api-key`・`fred-api-key`・`edinet-subscription-key` | 旧い鍵を失効させてから新しい鍵を書く |
| 基盤の資格情報（DB・オブジェクトストレージ・Vault の管理） | 基盤 | — | 基盤リポジトリの `scripts/rotate-infra-credentials.sh --live <対象>`（値を表示しない・引数に載せない） |
| OpenD のログインの資格情報 | 証券会社のアプリ（ログインのパスワードの変更） | `login-pwd-md5`（`moomoo-credentials`。MD5 で書く。平文は不可） | §OpenD と証券口座 |
| OpenD の暗号鍵（RSA） | オーナーが作る鍵の組 | `opend_rsa.pem`（`moomoo-rsa`） | 発注執行と OpenD が同じ鍵を使う。両方の Pod を作り直す |
| OpenD の認証サイドカーの呼び出し元のシークレット | 乱数（32 文字以上） | 基盤の BFF と AST の両方が同じ値を読む | 両方を同時に入れ替える（片方だけだとサイドカーが 401 を返す＝再認証の画面が使えない。取引には影響しない） |
| ノードの SSH の鍵・VPS の管理画面のパスワード・API のトークン | 手元の端末・VPS の管理画面 | — | 新しい鍵を登録してから旧い鍵を消す（自分を締め出さない） |

- 入れ替えの後に、各 Pod が新しい値で動いていること（`kubectl get pods` の作り直しの時刻）と、ログに値が平文で出ていないこと（件数だけ数える）を確かめる。
- 🔴 **確認フレーズ・ボットのトークン・所有者の機密クライアントの secret は、ボットの Pod が読める**。ボットの Pod が疑わしいときは 3 つとも入れ替える。

### OpenD と証券口座

- **口座が疑わしいとき**（想定しない注文・約定・ログインの通知）:
  1. 証券会社のアプリで未約定の注文を取り消し、保有を確かめる（§初動 2）。
  2. 証券会社のアプリでログインのパスワード（と取引のパスワード）を変える。OpenD は次のログインから失敗する（止める向き）。
     いま動いている OpenD の接続がその場で切れるかは証券会社の仕様に依存し、本書では確かめていない。**確実に断つには OpenD を 0 台にする**
     （`k3s kubectl -n ai-stock-trading scale deployment opend --replicas=0`。発注・建玉の照会・ソフトウェアの逆指値も止まる）。
  3. 証券会社のアプリで、ログイン中の端末を確かめ、心当たりの無いものを外す。
  4. 証券会社の窓口へ連絡する（§連絡）。
- **OpenD の端末の信頼を取り消すとき**（ノードが疑わしい・OpenD の永続ボリュームを持ち出された疑いがある）: OpenD の永続ボリューム（PVC `opend-persist`）には端末の信頼の実体とログインの資格情報の MD5 が置かれる。
  パスワードを変えたうえで、PVC を消して作り直す（`resource-policy: keep` のため明示の削除が要る）。次の起動で SMS の再認証が要る。
- **OpenD を戻すとき**: Vault の `login-pwd-md5` を新しいパスワードの MD5 にし、OpenD の Pod を作り直す。SMS の検証コードを入れる（入れ方は `deploy/opend/README.md` の「標準入力の与え方」。画面を持たない構成では `kubectl` の経路を使う）。
  🔴 **OpenD が止まっている間は、建玉の照会・ソフトウェアの逆指値・手仕舞いの発注も止まる**。保有があるなら、証券会社のアプリで保護する。

## 5. 復旧

1. 原因と影響の範囲（いつから・どの資格情報・どの注文）を §記録 に書き終える。
2. §資格情報の入れ替え を終え、§証跡の保全 の写しがノードの外にあることを確かめる。
3. 構成を正しい状態へ戻す（Git の宣言どおりに。ArgoCD の自動同期を止めたなら戻す）。
4. Discord を戻す: `bash scripts/ast-trading-stop.sh --live restore-discord`。
5. 保持を外す: `bash scripts/ast-trading-stop.sh --live release`（**kill switch は起動したまま**）。
6. 状態を確かめる: Discord の `/status`（統制・段階・保有）、証券会社のアプリ（保有・未約定）、台帳と証券会社の建玉の一致（運用仕様書の突合の手順）。
7. 再開する: Discord の `/killswitch off`（確認ボタン＋確認フレーズ）。最初の 1 営業日は通知とアプリを普段より頻繁に見る。

## 6. 連絡

オーナーが 1 人で運用する前提で、連絡は**外部**と**記録**が中心になる。

| 相手 | いつ | 何を |
| --- | --- | --- |
| 証券会社（moomoo）の窓口 | 口座への不正なアクセス・想定しない約定の疑いがあるとき（S1） | 時刻・注文番号・行った措置（パスワードの変更・取消）。取引の取り消しや調査の可否を問う |
| Discord | アカウントを他人に使われた疑いがあるとき | アカウントの回復・不審なログインの報告（Discord のサポート） |
| VPS の事業者 | ノードへの不正なアクセス・不正な利用（外部への攻撃の踏み台）の疑いがあるとき | 時刻・行った措置 |
| 計画リポジトリ（GitHub の issue） | 計画の前提（統制・要件）が足りなかったと分かったとき | 中立の語で（例: 「Discord だけで運用するときの停止の手段」）。攻撃の手順・秘密の値・脆弱性の詳細は書かない |

- AI のセッション・自動化（CI・定期の点検）が異常に気付いたときは、**Discord と別に**オーナーへ届く経路（外部の監視サービスの通知先と同じ）を使う。Discord だけに投げない。
- 🔴 **公開の場（issue・PR・コミット）へは、悪用の手順・残っている弱点・秘密の値を書かない。** 必要なら非公開の経路（リポジトリの非公開のセキュリティ報告）を使う。

## 7. 事後の振り返り

再開から 5 営業日以内に行う。下の様式で、非難ではなく仕組みを直す観点で書く。

```text
# 障害の振り返り（<YYYY-MM-DD>）

- 重さの区分: S1 / S2 / S3
- 時系列（UTC と JST）: 検知 → 初動 → 止まったと確かめた時刻 → 復旧 → 再開
- 検知の経路と、検知までの遅れ（何で気付いたか・もっと早く気付けたか）
- 影響: 想定しない注文・約定（件数・金額）、保護の無かった時間、失った・漏れた資格情報
- 原因（分かったこと／分からないこと）
- 行った措置（止める・切り離す・入れ替えた資格情報の種類〔値は書かない〕）
- うまくいったこと・いかなかったこと（本書と停止の Runbook の手順のどこで詰まったか）
- 再発の防止（直すもの・期限・担当）: コード（issue）／手順書（本書の更新）／計画（計画リポジトリの issue）
```

- 振り返りの置き場は**非公開**にする（手元か、非公開の保管先）。公開のリポジトリへ入れるのは、中立の語で書いた是正の issue と手順書の更新だけである。
- 本書と停止の Runbook で詰まった手順は、その場で直す（手順書の更新は PR で行う）。

## 記録

- 様式は §事後の振り返り と同じ。障害の最中は時系列だけを先に書き、残りは振り返りで埋める。
- kill switch の起動・解除、設定の変更、Discord からの操作は、監査ログと設定の変更履歴に自動で残る（保持の起動は actor `operator-hold`）。

## 限界（この手順で担保できないこと）

- **外部の死活監視は基盤の作業とオーナーの登録に依存する。** 配備と登録が済むまで、クラスタ・ノードの停止に気付く手段は、証券会社のアプリの通知とオーナーの定期的な確認だけである。
- **証跡の保全は手作業である。** ログはクラスタの外へ自動では送られていない（ログの集約は最初の稼働では入れない）。Pod の作り直しより前に写さないと失われる。
- **Discord のアカウントの乗っ取りは、Discord の側の本人確認に依存する。** 本システムの Discord の操作は、許可したユーザー ID と確認ステップで守られているが、同じアカウントを使われると区別できない。危険な操作を止める窓口は SSH の経路で補う。
- 証券会社の側の措置（取引の取り消し・調査）は証券会社の判断による。
