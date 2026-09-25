---
title: 運用 Runbook — ホスト側の死活監視（クラスタの外から損切りの生存を見張る）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
---
<!-- trace:
ids: [FR-10, UC-02]
adrs: [ADR-0040]
iadrs: [IADR-0344, IADR-0365, IADR-0380, IADR-0210]
specs: [20260925_937_host-liveness-monitor]
issues: [#937, #909, #902]
-->

# 運用 Runbook: ホスト側の死活監視

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。** 状態の単一情報源は運用仕様書に置き、本書は手順に特化する。
>
> 🔴 **本書の手順はすべてオーナーが行う。** タスクスケジューラへの登録・Windows の設定変更・Rancher Desktop の設定は、
> AI（コーディングエージェント）は行わない。

## なぜこの文書があるか

ソフトウェア逆指値（S1）は、**クラスタの中**の市場監視サービスが損切りラインへの到達を検知し、発注執行サービスが成行で決済する。
ブローカー側に逆指値は置かない。したがって **ホスト PC と Kubernetes（Rancher Desktop）が止まっている間、S1 は何もできない。**

2026-09-23 22:37 JST（米国の開場 7 分後）にホスト PC が再起動し、Rancher Desktop が自動起動しなかった。
オーナーが手で起動するまでの約 4 分間、保有していた 707 株は無保護だった（#937）。
**クラスタの中の監視（アラート・Discord 通知サービス）はクラスタと一緒に止まる** ため、この停止はどこからも知らされなかった。

本書は次の 3 つを定める。

1. **ホストの上でクラスタの外から見張るスクリプト**（`scripts/host-liveness-monitor.ps1`）の登録
2. **Rancher Desktop の自動起動**
3. **場中に Windows Update で再起動しない設定**

## この手順を実行する条件（いつ走らせるか）

| 場面 | 行う節 |
| --- | --- |
| 初回（ホストの準備） | 「手順」の 1〜6 をすべて |
| リポジトリのクローン先・PowerShell の場所を変えた | 手順 4（タスクの操作のパス）を直す |
| 死活監視の通知が届いた | 「失敗したときの分岐」 |
| ホストが予期せず再起動した | 手順 6（原因の確認） |

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | ホストのログオンユーザー（タスクの登録・Rancher Desktop の設定）。グループポリシーを使う場合だけ管理者 |
| 必要なツール | PowerShell 7（`pwsh.exe`。既定の場所は `C:\Program Files\PowerShell\7\pwsh.exe`）・`kubectl`（Rancher Desktop が入れる）・kubeconfig のコンテキスト `rancher-desktop` |
| 所要時間の目安 | 30 分 |

> **Windows PowerShell 5.1（`powershell.exe`）では動かない。** スクリプトは PowerShell 7 を要求する（先頭の `#Requires -Version 7.0`）。

## スクリプトが見るもの

**米国株の通常取引時間（米東 9:30–16:00。休場日と半日取引日の午後を除く）だけ**、5 分ごとに次を読む。時間外は何もせずに終わる。
**読むだけ**であり、再起動・コンテナへの接続・設定の変更は一切しない。

| 確認 | 読み方 |
| --- | --- |
| Kubernetes API に届くか | `kubectl get --raw /readyz` |
| 市場監視・発注執行の Pod が Ready か | `kubectl get pods -l app=<名前>` |
| 市場監視の生存要約 | ログの `損切り評価は稼働中: 保有 N 件を評価`（保有を評価している間、5 分に 1 回） |
| 発注執行の生存要約 | ログの `ソフトウェア逆指値（S1）の保護は継続中: Active N 件`（S1 の保護記録がある間、5 分に 1 回。取引時間に依らない） |

休場日・半日取引日はサービスと同じ規則で計算する（元日・キング牧師記念日・ワシントン誕生日・グッドフライデー・戦没者追悼日・
ジューンティーンス・独立記念日・レイバーデー・感謝祭・クリスマスと振替、半日は独立記念日前日・感謝祭翌日・クリスマスイブ）。
**臨時休場（国葬など）は規則で書けない** ので、クラスタへ臨時休場日を設定したときは同じ日付をスクリプトの `-ExtraHolidays` にも渡す
（渡さなければ、その日はスクリプトだけが「場中」と読み、要約が出ないことを警告し得る）。

### 状態と通知

| 状態 | 意味 | 通知 |
| --- | --- | --- |
| `CLUSTER_UNREACHABLE` | Kubernetes API に届かない（Rancher Desktop が止まっている） | 🔴 鳴らす |
| `SERVICE_NOT_READY` | 市場監視か発注執行に Ready の Pod が無い | 🔴 鳴らす |
| `UNREADABLE` | Pod の照会かログの読み取りに**失敗**した（確かめられないことを平常とは読まない。ログが空なのは失敗ではない） | 🔴 鳴らす |
| `EVALUATION_SILENT` | **S1 の保護記録があるのに、損切り評価の生存要約が 10 分を超えて出ていない**（到達を検知できていない恐れ） | 🔴 鳴らす |
| `SCRIPT_ERROR` | スクリプト自身の失敗（`kubectl` が見つからない・`-ExtraHolidays` / `-ExtraHalfDays` の日付を読めない等。日付の誤りは時間外でも鳴る） | 🔴 鳴らす |
| `HEARTBEATS_STOPPED` | この取引時間中は保有を見ていたのに、生存要約が両方止まった。建玉を閉じた（損切りが約定した）なら正常 | ⚠️ 1 回だけ鳴らす |
| `PROTECTED` | S1 の保護記録があり、損切り評価も稼働中 | 鳴らさない |
| `EVALUATING` | 損切り評価は稼働中。S1 の保護記録の要約は出ていない（S1 以外の手法か、発注執行が内蔵 paper 構成） | 鳴らさない |
| `WARMING_UP` | 寄り付き・再起動の直後で、要約の初回を待っている | 鳴らさない |
| `NO_HEARTBEAT_UNKNOWN` | 要約がどちらも出ていない。**保有なしなら正常だが、外からは「保有なし」と「両方が無音」を区別できない**（保有は不明と記録する） | 鳴らさない |

- 🔴 鳴らす状態は、**遷移したときに鳴らし、続く間は 30 分ごとに鳴らし直す**。平常へ戻ったら「回復」を 1 回知らせる。平常時は何も出さない。
- 通知の経路は**クラスタに依らないものだけ**である: Windows の通知（トースト）・任意の Discord Webhook・ログファイル。

## 手順

### 1. 試走する（何も書き換えない）

リポジトリのクローン（以下 `<repo>`）で次を実行する。`-DryRun` は通知・状態ファイル・ログファイルに触らず、判定を 1 行表示するだけである。
`-IgnoreMarketHours` を付けると時間外でも判定まで走る。

```powershell
pwsh -NoProfile -File <repo>\scripts\host-liveness-monitor.ps1 -DryRun -IgnoreMarketHours
```

期待（どれも読み取りに成功した表示である。ログが空なのは失敗ではない）:

| 試走した時間 | S1 の保護記録 | 出る状態 |
| --- | --- | --- |
| 場中 | あり | `PROTECTED`（市場監視の初回の要約を待っていれば `WARMING_UP`） |
| 場中 | なし・保有あり | `EVALUATING` |
| 場中 | なし・保有なし | `NO_HEARTBEAT_UNKNOWN` |
| 時間外 | あり | `EVALUATION_SILENT`——市場監視は閉場中に要約を出さないため。**時間外の試走に固有の表示**で、登録した運用（場中だけ判定する）では起きない。場中にこれが出たら異常である |
| 時間外 | なし | `NO_HEARTBEAT_UNKNOWN` |

`UNREADABLE` / `SERVICE_NOT_READY` / `CLUSTER_UNREACHABLE` / `SCRIPT_ERROR` が出たら、時間に依らず「失敗したときの分岐」へ進む。

### 2. 通知の経路を試す（クラスタに触らない）

存在しないコンテキストを指定すると、Rancher Desktop を止めずに「API に届かない」経路を再現できる。状態ファイルは一時フォルダへ逃がす。

```powershell
pwsh -NoProfile -File <repo>\scripts\host-liveness-monitor.ps1 -IgnoreMarketHours -Context does-not-exist -StateDirectory $env:TEMP\ast-liveness-test
```

期待: Windows の通知に「AST 死活監視: CLUSTER_UNREACHABLE」が出る（Discord を設定していれば Discord にも届く）。終了コードは 1。
確かめたら `$env:TEMP\ast-liveness-test` を消してよい。

> 🔴 **動作確認のために Rancher Desktop を止めない。** 止めている間は S1 が働かない。

### 3.（任意）Discord へも送る

クラスタの Discord 通知とは**別の Webhook**（別チャンネル）を作り、ユーザー環境変数へ入れる。
Webhook の URL はスクリプトの表示・ログには出ない。**チャットやリポジトリへ貼らない。**

```powershell
[Environment]::SetEnvironmentVariable('AST_HOST_LIVENESS_DISCORD_WEBHOOK', '<Webhook の URL>', 'User')
```

- 値はユーザーのレジストリ（`HKCU\Environment`）に平文で残る。ホストのユーザーアカウントを守ることが前提である。
- ユーザー環境変数の変更は、その後に起動したプロセスから見える。**登録済みのタスクがログオンし直さずに新しい値を読むかは確かめていない**
  ——確実にするには、設定した後に一度サインアウトしてサインインし直し、手順 2 で Discord に届くことを確かめる。
- 未設定なら Discord へは送らない（Windows の通知とログファイルだけ）。

### 4. タスクスケジューラへ登録する

PowerShell（管理者でなくてよい）で次を実行する。`<repo>` は develop を追っているクローンを指すこと
（作業ブランチのワークツリーを指すと、ブランチの切り替えで監視の中身が変わる）。

```powershell
$pwsh   = (Get-Command pwsh).Source
$script = '<repo>\scripts\host-liveness-monitor.ps1'
$action = New-ScheduledTaskAction -Execute $pwsh `
  -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -File `"$script`""
# ログオン時に 1 回＋5 分ごと（時間外はスクリプトが何もせずに終わるので、常時 5 分ごとでよい）
$atLogOn = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$every5  = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 5)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
  -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 4)
Register-ScheduledTask -TaskName 'AST host liveness monitor' -Action $action `
  -Trigger @($atLogOn, $every5) -Settings $settings `
  -Description 'クラスタの外から、米国株の通常取引時間だけ Kubernetes API と損切りの生存要約を見張る（読むだけ）'
```

- **「ユーザーがログオンしているときのみ実行する」のまま登録する**（既定）。Windows の通知はログオン中のデスクトップにしか出ない。
- `-WindowStyle Hidden` でもウィンドウが一瞬表示されることがある（PowerShell の仕様）。
- タスクスケジューラの画面で登録する場合も同じ設定にする: 操作＝`pwsh.exe` と上の引数、トリガー＝「ログオン時」と「1 回・5 分ごとに無期限に繰り返す」、
  設定＝「新しいインスタンスを開始しない」「スケジュールされた時刻にタスクを開始できなかった場合、すぐにタスクを実行する」、
  条件＝「コンピューターを AC 電源で使用している場合のみタスクを開始する」を外す。

### 5. Rancher Desktop をログオン時に自動起動する

Rancher Desktop の「Preferences」→「Application」→「Behavior」で次を有効にする（表記は版により異なる）。

- **Automatically start at login**（ログオン時に自動起動）
- **Start in the background**（任意。ウィンドウを出さずに起動）

確認: タスク マネージャーの「スタートアップ アプリ」に Rancher Desktop が「有効」で載っていること。
次回ログオン後、`kubectl --context rancher-desktop get nodes` が `Ready` を返すまでの時間を一度測っておく（復帰に掛かる時間の目安になる）。

### 6. 場中に Windows Update で再起動させない

米国の通常取引時間は日本時間で **22:30–翌 5:00（米国の夏時間）／23:30–翌 6:00（冬時間）** である。

1. 「設定」→「Windows Update」→「詳細オプション」→「アクティブ時間」を**手動**にし、例えば **21:00〜7:00** にする
   （両方の時期を覆う。アクティブ時間の間は自動の再起動が行われない。上限は 18 時間）。
2. 同じ「詳細オプション」で **「最新の状態にする」（更新後できるだけ早く再起動）をオフ**、
   **「更新を完了するために再起動が必要な場合に通知する」をオン**にする。
3. （任意・Pro のみ・管理者）`gpedit.msc` の「コンピューターの構成」→「管理用テンプレート」→「Windows コンポーネント」→「Windows Update」配下にある
   **「スケジュールされた自動更新のインストールで、ログオンしているユーザーがいる場合には自動的に再起動しない」** を有効にする
   （配下のフォルダ名は版により異なる。見つからなければ 1・2 だけでよい）。
4. 「設定」→「システム」→「電源」で、電源接続時の **スリープ・休止状態を「なし」** にする（スリープ中もクラスタは止まり、S1 は働かない）。

**予期しない再起動の原因を確かめる**（再起動のたびに行う）:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 1074, 6008, 41 } -MaxEvents 20 |
  Format-Table TimeCreated, Id, ProviderName, Message -Wrap
```

- `1074`: 再起動を始めたプロセスと理由（Windows Update なら `TrustedInstaller.exe` や `MoUsoCoreWorker.exe` 等が載る）
- `6008` / `41`: 予期しないシャットダウン（電源断・フリーズ）

## 確認（この手順が成功したと言える条件）

- 手順 1 の試走が状態を 1 行出し、手順 2 で Windows の通知（と設定していれば Discord）が届いた。
- タスクスケジューラの「AST host liveness monitor」の「前回の実行結果」が場中に更新され、平常時は `0x0` である（警報の状態では `0x1`、スクリプトの失敗は `0x2`）。
- 場中に `%LOCALAPPDATA%\ai-stock-trading\host-liveness\host-liveness.log` へ 5 分ごとに 1 行ずつ増えている。
- ホストを再起動（場外に行う）してログオンすると、Rancher Desktop が自動で起動し、次の場中の実行が `CLUSTER_UNREACHABLE` を出さない。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| `CLUSTER_UNREACHABLE` が届いた | Rancher Desktop が起動していない・起動中・Kubernetes が落ちた | Rancher Desktop を起動する。**起動するまで S1 は働かない**。建玉と損切りラインを証券会社の画面で確かめ、急ぐなら画面から手で守る。復帰後は「回復」が届く |
| `SERVICE_NOT_READY` が届いた | 市場監視・発注執行の Pod が再起動中・起動に失敗 | `kubectl -n ai-stock-trading get pods` と該当 Pod のログを見る。数分で戻らなければ証券会社の画面で建玉を守る |
| `EVALUATION_SILENT` が届いた | 市場監視の巡回が止まった・価格の取得で詰まっている・ログの文言が変わった | 市場監視のログを見る（`kubectl -n ai-stock-trading logs deploy/market-monitor-service --since=15m`）。巡回が止まっていれば、到達を検知できないので証券会社の画面で建玉を守る |
| `HEARTBEATS_STOPPED` が届いた | 建玉を閉じた（損切りが約定した）／両サービスが無音 | 証券会社の画面か日報で建玉が無いことを確かめる。建玉が残っていれば停止を疑い、上の 2 行と同じ手順で調べる |
| `UNREADABLE` が届いた | `kubectl get pods` / `kubectl logs` が失敗（API の一時的な不調・権限） | 本文に出る対象（Pod の照会かログか）について、同じコマンドを手で実行して原因を見る |
| `SCRIPT_ERROR` が届いた | `kubectl` が PATH に無い・臨時休場日 / 臨時半日の書式が `yyyy-MM-dd` でない・スクリプトの不具合 | 手順 1 の試走で原因を表示させる。直らなければ issue を起票する |
| 通知が一度も来ない（試走では来た） | ログオンしていない・タスクが無効・`pwsh` のパスが違う | タスクスケジューラの履歴と「前回の実行結果」を見る |
| 休場日に要約が無いと警告された | その日の臨時休場をスクリプトに渡していない | `-ExtraHolidays yyyy-MM-dd` をタスクの引数へ足す |

## 記録

- 判定は `%LOCALAPPDATA%\ai-stock-trading\host-liveness\host-liveness.log` に 1 実行 1 行で残る（1 MB を超えたら直近 2000 行へ切り詰める）。
- 前回の状態（通知の要否の判断に使う）は同じフォルダの `state.json`。消すと次の実行は「前回なし」として判定する（最悪でも 1 回多く鳴るだけ）。
- ホストの停止・再起動があったときは、手順 6 のイベントを添えて issue に残す。

## 限界（この手順で担保できないこと）

- 🔴 **ホストそのものが止まっている・誰もログオンしていない間は、ホスト上のスクリプトも止まっている。** 再起動の後にログオンするまでは、
  Rancher Desktop も本スクリプトも起動しない。ホストの外から見張る仕組み（ホストが定期的に外へ生存を送り、途絶えたら外側が知らせる形）は用意していない。
- **Windows の通知はログオン中のデスクトップにしか出ない。** 離席中に確実に知るには Discord の設定（手順 3）が要る。
- **検知までの遅れ**: API に届かない状態は次の実行（最大 5 分後）で分かる。評価の停止（`EVALUATION_SILENT`）は要約の途絶が 10 分を超えてからなので、最大でおよそ 15 分掛かる。
- **保有の有無は外から確かめられない。** 生存要約が両方出ていないとき、「保有なし」と「両方が無音」を区別できない（`NO_HEARTBEAT_UNKNOWN`）。
  この取引時間中に保有を見ていた場合だけ `HEARTBEATS_STOPPED` として 1 回知らせる。
- **発注執行の生存要約は moomoo 構成でだけ出る**（内蔵 paper 構成では常駐そのものが無い）。paper では評価の停止を `EVALUATION_SILENT` として検知できない。
- **ログは Pod を 1 つだけ読む**（`kubectl logs deploy/<名前>` が 1 つを選ぶ）。ロールアウト中は旧 Pod のログを読み、新 Pod の要約を見落としたり、
  止まりかけの旧 Pod の要約を新しいと読んだりすることがある。Ready の判定は全 Pod を見るが、ログは見ない。
- **東証の取引時間は見ない**（米国株だけ）。
- 本スクリプトは**知らせるだけ**で、Rancher Desktop の起動・Pod の再起動・建玉の手当ては行わない。
- 閉場中の建玉は、クラスタが生きていても次の寄り付きまで無保護である（S1 の性質。運用仕様書と機能仕様書の注記を参照）。
