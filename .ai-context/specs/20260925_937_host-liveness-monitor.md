---
title: クラスタの外（ホスト）から、米国株の通常取引時間だけ Kubernetes API と S1 の生存要約の鮮度を見張るスクリプトと手順書を用意し、S1 は「ホストとクラスタが生きている間しか守れない」を IADR-0344 に明記する
type: spec
status: accepted
related_ids: [FR-10, FR-03, UC-02, ADR-0040, IADR-0344, IADR-0365, IADR-0380, IADR-0210]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040 (決定1 S1)
---

# 仕様書: ホスト側の死活監視（#937）

## 起点

- [#937](https://github.com/endazon/ai-stock-trading/issues/937)。稼働 PoC（2026-09-23 JST）で **22:37:16 にホスト PC が再起動**し、
  Rancher Desktop が自動起動しなかった。22:41:31 にオーナーが手で起動するまで **約 4 分間、AAPL 707 株は無保護**だった
  （S1 はソフトウェア逆指値でブローカー側に逆指値が無い）。**クラスタの中の監視はクラスタと一緒に止まる。**
- オーナー方針（#937 のコメント・2026-09-25）:
  - AI 側: クラスタの**外から**死活を見張る仕組み（ホスト側のスクリプトと手順書）／IADR-0344 に「S1 はホストとクラスタが生きている間しか守れない」を追記。
  - オーナー側: Rancher Desktop の自動起動、場中に Windows Update で再起動しない設定、ホスト側の定期実行の登録。
  - 🔴 **AI はタスクの登録・Windows 設定の変更・稼働クラスタへの変更を行わない。** 読み取りだけの試走（`kubectl get` / `logs`）は 1 回まで可。

## 🔴 実測（コードで確認・`origin/develop` = `9b80cd72`）

| 対象 | 場所 | 事実 |
| --- | --- | --- |
| 市場監視の生存要約 | `backend/Services/MarketMonitorService/Hosted/StopLossLivenessReporter.cs` L181-183 | `"損切り評価は稼働中: 保有 {Count} 件を評価（要約は {Interval} に 1 回）: {Details}"`。**保有を評価している巡回でだけ**出る（`Observe` の `evaluations.Count > 0`）。間隔は `Monitor:StopLossSummaryIntervalSeconds`（既定 300）・**初回は即時**・保有 0 件で状態を捨てる。閉場中の市場は `Observe` に来ない（IADR-0380） |
| 発注執行の生存要約 | `backend/Services/OrderExecutionService/Features/OrderExecution/GuardProtectiveStops/SoftwareStopLivenessReporter.cs` L47-49 | `"ソフトウェア逆指値（S1）の保護は継続中: Active {Count} 件（…）"`。**Active な S1 行があるときだけ**出る。常駐ガード（既定 30 秒）の後に間隔（`ProtectiveStopGuard:SoftwareStopSummaryInterval` 既定 5 分）に 1 回。**取引時間に依らない**（ガードは 24 時間回る） |
| ガードの配線 | `backend/Services/OrderExecutionService/Program.cs` L236-280 | ガードと要約は **moomoo 構成でのみ**登録される（paper は常駐そのものが無い）＝paper では発注執行の要約は出ない |
| 米国市場の開場判定 | `backend/Shared/AiStockTrading.Shared.Kernel/Trading/MarketHours.cs` / `MarketSessions.cs` / `MarketHolidays.cs` | 米東 9:30–16:00（開始包含・終了排他）、半日 13:00。休場日は**規則計算**（10 日・振替・元日の土曜は振替なし・ジューンティーンスは 2022 年から）。臨時休場・臨時半日は構成（`Monitor:Holidays:<Market>`）から**足す** |
| 配備名 | `deploy/helm/ai-stock-trading/templates/deployment.yaml` L83-100 | Deployment 名 `<name>-service`・ラベル `app: <name>-service`。名前空間は `ai-stock-trading` |
| ログの形 | 稼働クラスタ（読み取りのみ・`kubectl logs --timestamps`） | 行頭に RFC3339 ナノ秒のタイムスタンプ（`2026-09-25T18:55:29.256607676+09:00 `）＋ `[HH:mm:ss INF] 本文` |

## 射程

1. **ホスト側スクリプト** `scripts/host-liveness-monitor.ps1`（PowerShell 7。本ホストに `pwsh.exe` があることを確認済み）。
   - 米国株の通常取引時間の外では何もせず終わる（共有カーネルの `MarketHours` と同じ規則を写す。**休場日の規則計算は全 10 日を写す**。
     臨時休場・臨時半日は `-ExtraHolidays` / `-ExtraHalfDays` で足す。クラスタの構成と同じ値を渡さなければ食い違い得る——これが唯一の近似）。
   - 場中は (a) Kubernetes API（`/readyz`）(b) 市場監視・発注執行の Pod の Ready (c) 2 つの生存要約の鮮度を読む。**読むだけ**（`get` / `logs` 以外を呼ばない）。
   - 判定（純関数）と通知の要否（純関数）を分け、テストで固定する。
   - 通知はクラスタに依らない経路だけ: Windows の通知（NotifyIcon のバルーン＝Windows 11 ではトースト）・任意の Discord Webhook（環境変数・値を表示しない）・ログファイル。
   - **遷移で鳴らし、続く間は間隔を空けて鳴らし直し、回復を 1 回知らせる。** 平常時は何も出さない（ログファイルへ 1 行だけ）。
2. **手順書** `docs/operations/host-liveness-monitor-runbook.md`: タスクスケジューラの登録・Rancher Desktop の自動起動・Windows Update のアクティブ時間と場中の再起動抑止（**オーナーが行う**）。
3. **IADR-0344 の追記（17）** と索引行の更新（既存の追記はすべて残す）。**新しい IADR は起こさない**（予約 IADR-0426 は使わない）——決定は S1 の射程の開示であり、スクリプトの内部設計は本仕様書に置けば足りる。
4. **テスト** `scripts/host-liveness-monitor.test.ps1`（T-10-1040〜T-10-1049）。**フレームワークを足さない**——既存の `*.test.sh` と同じ「素のスクリプト＋アサート関数＋失敗があれば exit 1」の形を PowerShell で写す。関数だけを読み込む idiom も `AST_CUTOVER_LIB=1` と同じ（`AST_HOST_LIVENESS_LIB=1`）。CI の `static-checks` へ 1 ステップ足す（ubuntu-latest は `pwsh` を持つ）。
5. 文書の追随: 運用仕様書の監視節と関連文書表、FR-10 機能仕様書の S1 残余リスク、FR-10 テスト仕様書（T-10-1040〜1048）、`scripts/README.md`。

### 判定（状態）

各要約の状態を 3 つに分ける（`StaleAfterMinutes` 既定 10。正常時の要約の間隔は最大でおよそ 6 分＝5 分＋巡回周期）:

- **Fresh**: 最後の要約から `StaleAfter` 以内。
- **Warmup**: Fresh でないが、基準時刻（コンテナの起動時刻。市場監視は当日の寄り付きも）から `StaleAfter` 以内——初回の要約を待っている。
- **Silent**: それ以外。

| # | 条件 | 状態 | 重さ |
| --- | --- | --- | --- |
| 1 | API に届かない | `CLUSTER_UNREACHABLE` | alert |
| 2 | 市場監視か発注執行に Ready の Pod が無い | `SERVICE_NOT_READY` | alert |
| 3 | どちらかの Pod の照会かログの読み取りに**失敗**した（exit≠0・JSON を読めない。**空で成功は失敗ではない**） | `UNREADABLE` | alert |
| 4 | 発注執行が Fresh（S1 行が Active）・市場監視が Fresh | `PROTECTED` | ok |
| 5 | 発注執行が Fresh・市場監視が Warmup | `WARMING_UP` | ok |
| 6 | 発注執行が Fresh・市場監視が Silent | `EVALUATION_SILENT`（**保有を知っているのに評価が止まっている＝S1 の監視が死んでいる**） | alert |
| 7 | 発注執行が Fresh でない・市場監視が Fresh | `EVALUATING`（保有を評価中。S1 行の要約が無いのは S1 以外の手法か paper 構成） | ok |
| 8 | どちらも Fresh でなく、どちらかが Warmup | `WARMING_UP` | ok |
| 9 | どちらも Silent で、**この取引時間中に保有を見ていた** | `HEARTBEATS_STOPPED`（建玉を閉じたなら正常。そうでなければ停止を疑う） | warn（遷移で 1 回だけ） |
| 10 | どちらも Silent で、この取引時間中に保有を見ていない | `NO_HEARTBEAT_UNKNOWN`（**保有なしと両方の無音を外からは区別できない**。「保有なし」と書かない） | ok（holding=unknown） |

- **unknown ≠ none**: 保有の有無を外から確かめる手段は生存要約しか無い（市場監視・発注執行の HTTP は認可つきで、スクリプトは資格情報を持たない）。
  「保有を見ていた」の記憶は状態ファイルに持ち、**その取引時間の寄り付き以降に見たものだけ**を数える（前日に閉じた建玉で翌日鳴らさない）。
- 1〜3 は保有の有無に依らず alert にする（クラスタが見えない間は保有も分からない）。

### 通知の要否

| 前回 → 今回 | 通知 |
| --- | --- |
| alert 以外 → alert、または alert の状態が変わった | 鳴らす |
| 同じ alert が続く | `ReAlertMinutes`（既定 30）ごとに鳴らし直す |
| warn へ遷移 | 1 回だけ鳴らす（同じ warn が続く間は鳴らさない＝建玉を閉じた日に 30 分ごとに鳴らさない） |
| alert / warn → ok | 回復を 1 回知らせる |
| ok → ok | 何もしない |

- 通常取引時間の外では判定も通知もしない（状態ファイルも触らない）。
- スクリプト自身の例外（`kubectl` が無い等）は `SCRIPT_ERROR`（alert）として同じ経路で鳴らし、exit 2。

## 母集合の引き直し（規則 1〜6・9〜10）

| 軸 | 引いたもの | 結果 | 採否 |
| --- | --- | --- | --- |
| 生存要約の文字列（誤りの側＝issue の文言を信じない） | `grep -rn "損切り評価は稼働中\|保護は継続中" backend`（拡張子で絞らない） | 本番コード 2 箇所（上表）＋テスト 3 箇所 | 本番 2 箇所の実文言をスクリプトの照合語にした |
| S1 の射程の開示（「開場中だけ」の既存開示） | `git grep -nE "開場中(しか\|だけ)\|通常取引時間だけ"`（docs・deploy・scripts・backend） | 市場監視のログ文言・日報の注記・FR-10 機能仕様書・FR-10 テスト仕様書 | **FR-10 機能仕様書に追記**（人が読む残余リスクの一覧）。日報の注記・S1 配置通知の文面は**変えない**（下の除外） |
| ホスト・クラスタ停止への言及 | `git grep -nE "ホスト.{0,10}(再起動\|停止)\|クラスタ.{0,6}(停止\|止ま)\|Rancher Desktop"` | 環境説明（dev は Rancher Desktop）とスクリプトのランタイム判定のみ。**S1 の残余として書いた箇所は 0 件** | 追記の必要を確認（IADR-0344・FR-10 機能仕様書） |
| 運用の監視の入口 | `docs/operations/operations.md` §監視・アラート・§関連文書 | クラスタ内のアラートだけ | 外側の監視を 1 行と関連文書 1 行で足す |
| テスト ID | `git grep -n "T-10-104[0-9]"` | 0 件（予約どおり空き） | T-10-1040〜1048 を使う |
| 既存の PowerShell | `git ls-files \| grep -iE "\.ps1\|\.psm1\|pester"` | 0 件（`.gitattributes` に `*.ps1 text eol=crlf` の宣言だけある） | 新規。`.psm1` を足すと `.gitattributes` に無い拡張子になるため、関数ライブラリも同じ `.ps1` に置き環境変数で読み分ける |
| コミット種別 | `scripts/check-commit-messages.js` `VALID_TYPES` | `ops` は無い | `feat` / `docs` を使う |

### 除外とその理由

- **S1 配置通知（`SoftwareStopArmed`）の文面・日報の注記は変えない。** 配置通知は既に「発注執行・市場監視・メッセージ基盤のいずれかが止まっている間は決済されない」を明記している（FR-10 機能仕様書 §S1 の動作）。ホスト・クラスタの停止はその上位の場合であり、文面の追加はオーナー方針の AI 側 3 項目の外（通知の文面変更はテスト・ゴールデンの追随を伴う別の変更）。
- **東証（Japan）は見張らない。** 稼働 PoC の建玉は米国株で、オーナー方針も米国の通常取引時間に限っている。東証の休場日は共有カーネルでも射程外（#21）。
- **ホストそのものが落ちている・ログオンしていない間は、ホスト上の監視では検知できない。** 外部の死活監視（dead man's switch）は本 issue の射程外として手順書の「限界」に書く。

## 受け入れ基準

- [ ] 通常取引時間の外では何もせず exit 0（夏時間の両端・休場日・半日取引日を共有カーネルと同じ fixture で固定）。
- [ ] API 不達・Pod 非 Ready・ログ読めずは alert。
- [ ] S1 行が Active（発注執行の要約が新しい）なのに市場監視の要約が `StaleAfter` を超えて無ければ alert。寄り付き直後・再起動直後は猶予。
- [ ] どちらの要約も無いとき、この取引時間中に保有を見ていなければ「不明」（「保有なし」と書かない）で鳴らさない。見ていたら 1 回だけ warn。
- [ ] 平常時は通知しない。遷移で鳴らし、alert は間隔で鳴らし直し、回復を 1 回知らせる。
- [ ] Discord Webhook の URL を表示・ログに出さない（送信失敗の例外文言に URL が含まれても出さない）。
- [ ] 手順書にオーナーの操作（タスク登録・自動起動・Windows Update）と限界を書く。AI は登録・設定変更をしない。
- [ ] IADR-0344 追記（17）・索引行を足し、既存の追記を 1 つも落とさない。

## ［2026-09-25 追記 / PR #998 監査］監査 NO-GO の是正

- **B1**: `Get-ServiceLogs` が配列を素で返していたため、**kubectl が空で成功した**ときに PowerShell が空配列を `$null` へ展開し、
  呼び出し側が「ログを読めない」と読んでいた。市場監視は保有 0 件・閉場中は何も出さないので、**保有の無い日に毎日警報**になり、
  `NO_HEARTBEAT_UNKNOWN` / `HEARTBEATS_STOPPED` へ到達しなかった（監査の稼働環境の試走で `LOGS_UNREADABLE` を実測）。
  → `{ Ok; Lines }` を返し、空で成功と失敗を分ける。kubectl を関数で差し替えたテスト（T-10-1049）で固定。
- **B2**: スクリプトの `-Verbose` / `-Debug` が呼び出し先へ伝わり、`Invoke-RestMethod` が Webhook の URL を出し得た。
  → `-Verbose:$false -Debug:$false -ErrorAction Stop` と、送信関数の中で詳細・デバッグ・情報の既定値を止め、**送信器の呼び出しを `*> $null` にする**。
  情報ストリームは既定値 `SilentlyContinue` でも記録が流れて `*>&1` で捕まるため、**漏れを実際に塞いだのは `*> $null` である**（既定値の停止だけでは
  差し替えた送信器の `Write-Information` が漏れた。CI の T-10-1048 が検出）。副作用: 差し替えた送信器の非終了エラーも飲み込む（既定の送信器は `-ErrorAction Stop`）。
  ストリームを捕まえるテスト（T-10-1048）。
- **N1**: 読めない `-ExtraHolidays` / `-ExtraHalfDays` を黙って捨てていた → `SCRIPT_ERROR`（時間外でも。開場を判定できないため）。
- **N2**: `kubectl get pods` の失敗を `SERVICE_NOT_READY` にしていた → 状態 `UNREADABLE`（Pod の照会の失敗とログの読み取りの失敗をまとめる。旧 `LOGS_UNREADABLE` を置き換え）。
- **N3**: 前の取引時間に書かれた状態で翌日の最初の実行が偽の「回復」を出した → 状態に確認時刻（`checkedAtMs`）を持ち、寄り付きより前の状態（と確認時刻の無い旧形式）は持ち越さない。
- **N4**: 観測の層と本体のテストを足した（T-10-1049。kubectl のスタブ・通知のスタブ・一時フォルダの状態ファイル。kubectl は get / logs だけを呼ぶことも見る）。
- **N6**: `kubectl logs deploy/<名前>` は Pod を 1 つだけ読む（ロールアウト中の見落とし）を Runbook の限界に書いた。
- **N7**: 「登録済みのタスクはログオンし直した後に新しい環境変数を読む」は未確認だったので、未確認と書き直した。
