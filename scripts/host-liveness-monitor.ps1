#Requires -Version 7.0
# FR-10, UC-02, ADR-0040 決定1（S1）, #937, IADR-0344 追記（17）: クラスタの**外**（ホスト）から、
# 米国株の通常取引時間だけ「Kubernetes API に届くか」と「S1 の生存要約が新しいか」を見張る。
#
# 🔴 クラスタの中の監視（Prometheus / Alertmanager / Discord 通知サービス）は**クラスタと一緒に止まる**。
#    2026-09-23 にホストが場中に再起動し、Rancher Desktop が自動起動せず、S1 が約 4 分間働かなかった（#937）。
#    本スクリプトはクラスタに依らない経路（Windows の通知・任意の Discord Webhook・ログファイル）でだけ知らせる。
#
# 🔴 読むだけ。呼ぶのは `kubectl get --raw /readyz` / `kubectl get pods` / `kubectl logs` の 3 つだけで、
#    再起動・exec・変更は一切しない。
#
# 使い方（登録はオーナーが行う。手順は docs/operations/host-liveness-monitor-runbook.md）:
#   pwsh -NoProfile -File scripts/host-liveness-monitor.ps1                      # 定期実行の本体
#   pwsh -NoProfile -File scripts/host-liveness-monitor.ps1 -DryRun -IgnoreMarketHours   # 試走（通知・状態ファイルを触らない）
#
# 終了コード: 0 = 平常／通常取引時間外、1 = 警報（alert / warn）の状態、2 = スクリプト自身の失敗。
#
# テスト: pwsh -NoProfile -File scripts/host-liveness-monitor.test.ps1
#   AST_HOST_LIVENESS_LIB=1 で dot-source すると関数定義だけを読み込む
#   （scripts/cutover-count-reconcile.sh の AST_CUTOVER_LIB=1 と同じ idiom）。
[CmdletBinding()]
param(
  # kubeconfig のコンテキスト。Rancher Desktop の既定名。
  [string]$Context = 'rancher-desktop',
  [string]$Namespace = 'ai-stock-trading',
  # 生存要約がこの分数を超えて無ければ「止まっている」と読む。正常時の間隔は最大でおよそ 6 分
  # （要約の間隔 5 分＋市場監視の巡回 60 秒）。
  [int]$StaleAfterMinutes = 10,
  # 同じ警報が続くときに鳴らし直す間隔。
  [int]$ReAlertMinutes = 30,
  # 臨時休場日・臨時の半日取引日（米東の日付 yyyy-MM-dd）。規則計算の休場日には**足す**だけで外せない
  # （共有カーネルの MarketHolidays と同じ）。クラスタの Monitor:Holidays:UnitedStates と同じ値を渡すこと。
  [string[]]$ExtraHolidays = @(),
  [string[]]$ExtraHalfDays = @(),
  # 状態ファイル・ログファイルの置き場。空なら %LOCALAPPDATA%\ai-stock-trading\host-liveness。
  [string]$StateDirectory = '',
  # 通常取引時間の外でも判定まで走らせる（試走用）。
  [switch]$IgnoreMarketHours,
  # 判定を表示するだけで、通知・状態ファイル・ログファイルに触らない（試走用）。
  [switch]$DryRun
)

Set-StrictMode -Version Latest

# ---- 照合語（本番コードの実文言。変えたら本スクリプトも追随させる） ---------------------------------------
# market-monitor:  backend/Services/MarketMonitorService/Hosted/StopLossLivenessReporter.cs
#   "損切り評価は稼働中: 保有 {Count} 件を評価（要約は {Interval} に 1 回）: {Details}"
#   保有を評価している巡回でだけ出る（保有 0 件・閉場中は出ない）。間隔 300 秒・初回即時。
# order-execution: backend/Services/OrderExecutionService/Features/OrderExecution/GuardProtectiveStops/SoftwareStopLivenessReporter.cs
#   "ソフトウェア逆指値（S1）の保護は継続中: Active {Count} 件（…）"
#   Active な S1 行があるときだけ出る。取引時間に依らない（常駐ガードは 24 時間回る）。moomoo 構成のみ。
$script:MonitorHeartbeatPattern = '損切り評価は稼働中: 保有 (?<count>\d+) 件を評価'
$script:ExecutionHeartbeatPattern = 'ソフトウェア逆指値（S1）の保護は継続中: Active (?<count>\d+) 件'
$script:MonitorDeployment = 'market-monitor-service'
$script:ExecutionDeployment = 'order-execution-service'
$script:DiscordWebhookVariable = 'AST_HOST_LIVENESS_DISCORD_WEBHOOK'

# ---- 米国市場の開場判定（共有カーネル MarketHours / MarketSessions / MarketHolidays の写し） ------------------
# 🔴 判定は米東の現地時刻で行う。固定オフセットで換算しない（夏時間の切替は TimeZoneInfo が吸収する）。
# 開始は包含・終了は排他（16:00:00 ちょうどは場中ではない）。半日取引日は 13:00 終了。

function Get-UsEasternZone {
  $id = if ($IsWindows) { 'Eastern Standard Time' } else { 'America/New_York' }
  [TimeZoneInfo]::FindSystemTimeZoneById($id)
}

function Get-IntDiv([int]$a, [int]$b) { [int][Math]::Floor($a / $b) }

function Get-NthWeekday([int]$Year, [int]$Month, [DayOfWeek]$Weekday, [int]$N) {
  $first = [datetime]::new($Year, $Month, 1)
  $shift = ([int]$Weekday - [int]$first.DayOfWeek + 7) % 7
  $first.AddDays($shift + (($N - 1) * 7))
}

function Get-LastWeekday([int]$Year, [int]$Month, [DayOfWeek]$Weekday) {
  $last = [datetime]::new($Year, $Month, [datetime]::DaysInMonth($Year, $Month))
  $last.AddDays(-1 * ((([int]$last.DayOfWeek - [int]$Weekday) + 7) % 7))
}

# グレゴリオ暦の復活祭（anonymous Gregorian algorithm）の 2 日前。
function Get-GoodFriday([int]$Year) {
  $a = $Year % 19
  $b = Get-IntDiv $Year 100
  $c = $Year % 100
  $d = Get-IntDiv $b 4
  $e = $b % 4
  $f = Get-IntDiv ($b + 8) 25
  $g = Get-IntDiv ($b - $f + 1) 3
  $h = ((19 * $a) + $b - $d - $g + 15) % 30
  $i = Get-IntDiv $c 4
  $k = $c % 4
  $l = (32 + (2 * $e) + (2 * $i) - $h - $k) % 7
  $m = Get-IntDiv ($a + (11 * $h) + (22 * $l)) 451
  $month = Get-IntDiv ($h + $l - (7 * $m) + 114) 31
  $day = (($h + $l - (7 * $m) + 114) % 31) + 1
  [datetime]::new($Year, $month, $day).AddDays(-2)
}

# 土曜の休日は前日の金曜へ、日曜の休日は翌月曜へ振り替える（元日の土曜は振り替えない）。
function Get-ObservedDate([datetime]$Date, [bool]$BackwardOnSaturday = $true) {
  if ($Date.DayOfWeek -eq [DayOfWeek]::Saturday) { if ($BackwardOnSaturday) { return $Date.AddDays(-1) } else { return $Date } }
  if ($Date.DayOfWeek -eq [DayOfWeek]::Sunday) { return $Date.AddDays(1) }
  return $Date
}

function Test-Weekend([datetime]$Date) {
  ($Date.DayOfWeek -eq [DayOfWeek]::Saturday) -or ($Date.DayOfWeek -eq [DayOfWeek]::Sunday)
}

function Test-UsHoliday([datetime]$Date) {
  $d = $Date.Date
  $y = $d.Year
  if ((Get-ObservedDate ([datetime]::new($y, 1, 1)) $false) -eq $d) { return $true }
  if ($d -eq (Get-NthWeekday $y 1 Monday 3) -or $d -eq (Get-NthWeekday $y 2 Monday 3)) { return $true }
  if ($d -eq (Get-GoodFriday $y)) { return $true }
  if ($d -eq (Get-LastWeekday $y 5 Monday) -or $d -eq (Get-NthWeekday $y 9 Monday 1) -or $d -eq (Get-NthWeekday $y 11 Thursday 4)) {
    return $true
  }
  if ($y -ge 2022 -and (Get-ObservedDate ([datetime]::new($y, 6, 19))) -eq $d) { return $true }
  return ((Get-ObservedDate ([datetime]::new($y, 7, 4))) -eq $d) -or ((Get-ObservedDate ([datetime]::new($y, 12, 25))) -eq $d)
}

function Test-UsHalfDay([datetime]$Date) {
  $d = $Date.Date
  if ((Test-Weekend $d) -or (Test-UsHoliday $d)) { return $false }
  if ($d -eq (Get-NthWeekday $d.Year 11 Thursday 4).AddDays(1)) { return $true }
  if ($d -eq [datetime]::new($d.Year, 7, 3)) { return $true }
  return $d -eq [datetime]::new($d.Year, 12, 24)
}

function Test-DateInList([datetime]$Date, [datetime[]]$List) {
  foreach ($x in @($List)) { if ($null -ne $x -and $x.Date -eq $Date.Date) { return $true } }
  return $false
}

function Test-UsTradingDay([datetime]$Date, [datetime[]]$ExtraHolidays = @()) {
  $d = $Date.Date
  -not (Test-Weekend $d) -and -not (Test-UsHoliday $d) -and -not (Test-DateInList $d $ExtraHolidays)
}

function Test-UsMarketOpen {
  param(
    [Parameter(Mandatory)][DateTimeOffset]$Instant,
    [datetime[]]$ExtraHolidays = @(),
    [datetime[]]$ExtraHalfDays = @()
  )
  $local = [TimeZoneInfo]::ConvertTime($Instant, (Get-UsEasternZone))
  $date = $local.DateTime.Date
  if (-not (Test-UsTradingDay $date $ExtraHolidays)) { return $false }
  $half = (Test-UsHalfDay $date) -or (Test-DateInList $date $ExtraHalfDays)
  $close = if ($half) { [TimeSpan]::new(13, 0, 0) } else { [TimeSpan]::new(16, 0, 0) }
  $tod = $local.DateTime.TimeOfDay
  return ($tod -ge [TimeSpan]::new(9, 30, 0)) -and ($tod -lt $close)
}

# その瞬間の米東の日付の寄り付き（9:30 ET）。取引日でなければ $null。
function Get-UsSessionOpen {
  param(
    [Parameter(Mandatory)][DateTimeOffset]$Instant,
    [datetime[]]$ExtraHolidays = @()
  )
  $zone = Get-UsEasternZone
  $local = [TimeZoneInfo]::ConvertTime($Instant, $zone)
  $date = $local.DateTime.Date
  if (-not (Test-UsTradingDay $date $ExtraHolidays)) { return $null }
  $naive = [datetime]::SpecifyKind($date.Add([TimeSpan]::new(9, 30, 0)), [DateTimeKind]::Unspecified)
  return [DateTimeOffset]::new($naive, $zone.GetUtcOffset($naive))
}

# ---- ログの読み取り（純関数） ---------------------------------------------------------------------------------

# `kubectl logs --timestamps` の行頭（RFC3339。小数は 9 桁まで）を読む。読めなければ $null。
function ConvertFrom-KubectlTimestamp([string]$Line) {
  $m = [regex]::Match($Line, '^(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(?<frac>\d+))?(?<tz>Z|[+-]\d{2}:\d{2})\s')
  if (-not $m.Success) { return $null }
  $frac = if ($m.Groups['frac'].Success) { '.' + $m.Groups['frac'].Value.PadRight(7, '0').Substring(0, 7) } else { '' }
  $tz = if ($m.Groups['tz'].Value -eq 'Z') { '+00:00' } else { $m.Groups['tz'].Value }
  return [DateTimeOffset]::ParseExact(
    $m.Groups['ts'].Value + $frac + $tz,
    [string[]]@("yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz"),
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::None)
}

# 照合語に当たる行のうち最も新しいものの時刻と件数。無ければ $null。
function Get-LatestHeartbeat([string[]]$Lines, [string]$Pattern) {
  $latest = $null
  foreach ($line in @($Lines)) {
    if ([string]::IsNullOrEmpty($line)) { continue }
    $m = [regex]::Match($line, $Pattern)
    if (-not $m.Success) { continue }
    $at = ConvertFrom-KubectlTimestamp $line
    if ($null -eq $at) { continue }
    if ($null -eq $latest -or $at -gt $latest.At) {
      $latest = [pscustomobject]@{ At = $at; Count = [int]$m.Groups['count'].Value }
    }
  }
  return $latest
}

# ---- 判定（純関数） -------------------------------------------------------------------------------------------

# Fresh = 最後の要約から StaleAfter 以内 / Warmup = 基準時刻（起動・寄り付き）から StaleAfter 以内で初回を待っている / Silent。
function Get-ReporterState {
  param(
    [object]$LastBeatAt,
    [Parameter(Mandatory)][DateTimeOffset]$Now,
    [Parameter(Mandatory)][TimeSpan]$StaleAfter,
    [object[]]$References = @()
  )
  if ($null -ne $LastBeatAt -and ($Now - [DateTimeOffset]$LastBeatAt) -le $StaleAfter) { return 'Fresh' }
  foreach ($r in @($References)) {
    if ($null -eq $r) { continue }
    $since = $Now - [DateTimeOffset]$r
    if ($since -ge [TimeSpan]::Zero -and $since -le $StaleAfter) { return 'Warmup' }
  }
  return 'Silent'
}

function Format-BeatAge([object]$Beat, [DateTimeOffset]$Now) {
  if ($null -eq $Beat) { return '読んだ範囲に無し' }
  $minutes = [int][Math]::Floor(($Now - [DateTimeOffset]$Beat.At).TotalMinutes)
  return "$minutes 分前（$($Beat.Count) 件）"
}

<#
  Observation（hashtable）:
    Now, SessionOpen（$null 可）, StaleAfter, ApiReachable, ApiDetail,
    NotReadyServices（string[]）, Unreadable（string[]。Pod の照会かログの読み取りに失敗したもの。「空で成功」は含めない）,
    MonitorBeat / ExecutionBeat（Get-LatestHeartbeat の戻り・$null 可）,
    MonitorStartedAt / ExecutionStartedAt（$null 可）, HoldingSeenAt（前回までに保有を見た時刻・$null 可）
  戻り: Status / Severity（ok|warn|alert）/ Holding（yes|unknown）/ HoldingObserved / MonitorState / ExecutionState / Message
  🔴 保有の有無を外から確かめる手段は生存要約しか無い。**「要約が無い」を「保有なし」と書かない**（unknown ≠ none）。
#>
function Get-HostLivenessVerdict([hashtable]$Observation) {
  $o = $Observation
  $now = [DateTimeOffset]$o.Now
  $verdict = {
    param($status, $severity, $holding, $observed, $mState, $eState, $message)
    [pscustomobject]@{
      Status = $status; Severity = $severity; Holding = $holding; HoldingObserved = $observed
      MonitorState = $mState; ExecutionState = $eState; Message = $message
    }
  }

  if (-not $o.ApiReachable) {
    return & $verdict 'CLUSTER_UNREACHABLE' 'alert' 'unknown' $false $null $null (
      "Kubernetes API に届きません（$($o.ApiDetail)）。クラスタが止まっている間、S1（ソフトウェア逆指値）は損切りを実行できません。" +
      'Rancher Desktop が起動しているかを確認してください。建玉の有無はクラスタが戻るまで分かりません。')
  }
  $notReady = @($o.NotReadyServices | Where-Object { $_ })
  if ($notReady.Count -gt 0) {
    return & $verdict 'SERVICE_NOT_READY' 'alert' 'unknown' $false $null $null (
      "Ready の Pod がありません: $($notReady -join ', ')。止まっている間、S1 は損切りを検知・実行できません。")
  }
  # PR #998 監査 N2: Pod の照会の失敗を「Ready でない」と混ぜない（読めないことと止まっていることは別）。
  $unreadable = @($o.Unreadable | Where-Object { $_ })
  if ($unreadable.Count -gt 0) {
    return & $verdict 'UNREADABLE' 'alert' 'unknown' $false $null $null (
      "読み取りに失敗しました: $($unreadable -join ', ')。S1 の生存を確かめられません（確かめられないことを平常とは読みません）。")
  }

  $stale = [TimeSpan]$o.StaleAfter
  $mState = Get-ReporterState -LastBeatAt ($o.MonitorBeat ? $o.MonitorBeat.At : $null) -Now $now -StaleAfter $stale `
    -References @($o.MonitorStartedAt, $o.SessionOpen)
  $eState = Get-ReporterState -LastBeatAt ($o.ExecutionBeat ? $o.ExecutionBeat.At : $null) -Now $now -StaleAfter $stale `
    -References @($o.ExecutionStartedAt)
  $observed = ($mState -eq 'Fresh') -or ($eState -eq 'Fresh')
  $holding = if ($observed) { 'yes' } else { 'unknown' }
  $mAge = Format-BeatAge $o.MonitorBeat $now
  $eAge = Format-BeatAge $o.ExecutionBeat $now
  $staleMin = [int]$stale.TotalMinutes

  if ($eState -eq 'Fresh') {
    if ($mState -eq 'Fresh') {
      return & $verdict 'PROTECTED' 'ok' $holding $observed $mState $eState "S1 行が Active（$eAge）で、損切り評価も稼働中（$mAge）。"
    }
    if ($mState -eq 'Warmup') {
      return & $verdict 'WARMING_UP' 'ok' $holding $observed $mState $eState "S1 行が Active（$eAge）。損切り評価の初回の要約を待っています（寄り付き・再起動の直後）。"
    }
    return & $verdict 'EVALUATION_SILENT' 'alert' $holding $observed $mState $eState (
      "S1 行が Active（$eAge）なのに、損切り評価の生存要約が $staleMin 分を超えて出ていません（最後: $mAge）。" +
      'S1 の到達を検知できていない可能性があります。市場監視サービスのログを確認してください。')
  }

  if ($mState -eq 'Fresh') {
    return & $verdict 'EVALUATING' 'ok' $holding $observed $mState $eState (
      "損切り評価は稼働中（$mAge）。S1 行の要約は出ていません（S1 以外の手法か、発注執行が paper 構成）。")
  }
  if ($mState -eq 'Warmup' -or $eState -eq 'Warmup') {
    return & $verdict 'WARMING_UP' 'ok' 'unknown' $false $mState $eState '寄り付き・再起動の直後で、生存要約の初回を待っています。'
  }

  $sessionOpen = $o.SessionOpen
  $seenThisSession = $null -ne $o.HoldingSeenAt -and $null -ne $sessionOpen -and ([DateTimeOffset]$o.HoldingSeenAt -ge [DateTimeOffset]$sessionOpen)
  if ($seenThisSession) {
    return & $verdict 'HEARTBEATS_STOPPED' 'warn' 'unknown' $false $mState $eState (
      "この取引時間中は保有を見ていたのに、生存要約が両方とも $staleMin 分を超えて出ていません（評価: $mAge / S1: $eAge）。" +
      '建玉を閉じた（損切りが約定した）なら正常です。そうでなければ市場監視と発注執行の停止を疑ってください。')
  }
  return & $verdict 'NO_HEARTBEAT_UNKNOWN' 'ok' 'unknown' $false $mState $eState (
    '生存要約はどちらも出ていません。保有なしなら正常ですが、外からは「保有なし」と「両方が無音」を区別できません（保有は不明）。')
}

# 前回の状態と今回の判定から、通知するか。戻り: alert | realert | recovered | none。
function Get-NotificationAction {
  param(
    [object]$Previous,
    [Parameter(Mandatory)][object]$Verdict,
    [Parameter(Mandatory)][DateTimeOffset]$Now,
    [Parameter(Mandatory)][TimeSpan]$ReAlertInterval
  )
  $prevSeverity = if ($null -ne $Previous) { $Previous.Severity } else { 'ok' }
  $prevStatus = if ($null -ne $Previous) { $Previous.Status } else { $null }
  switch ($Verdict.Severity) {
    'alert' {
      if ($prevSeverity -ne 'alert' -or $prevStatus -ne $Verdict.Status) { return 'alert' }
      if ($null -eq $Previous.LastAlertAt -or ($Now - [DateTimeOffset]$Previous.LastAlertAt) -ge $ReAlertInterval) { return 'realert' }
      return 'none'
    }
    'warn' {
      if ($prevStatus -ne $Verdict.Status) { return 'alert' }
      return 'none'
    }
    default {
      if ($prevSeverity -in 'alert', 'warn') { return 'recovered' }
      return 'none'
    }
  }
}

# 次回へ持ち越す状態。
function Get-NextState {
  param([object]$Previous, [Parameter(Mandatory)][object]$Verdict, [Parameter(Mandatory)][string]$Action, [Parameter(Mandatory)][DateTimeOffset]$Now)
  $lastAlertAt = if ($Action -in 'alert', 'realert') { $Now }
  elseif ($Verdict.Severity -eq 'ok') { $null }
  elseif ($null -ne $Previous) { $Previous.LastAlertAt }
  else { $null }
  $holdingSeenAt = if ($Verdict.HoldingObserved) { $Now } elseif ($null -ne $Previous) { $Previous.HoldingSeenAt } else { $null }
  [pscustomobject]@{
    Status = $Verdict.Status; Severity = $Verdict.Severity; LastAlertAt = $lastAlertAt; HoldingSeenAt = $holdingSeenAt; CheckedAt = $Now
  }
}

# PR #998 監査 N3: 前の取引時間に書かれた状態は持ち越さない（翌日の最初の実行で偽の「回復」を出さない。
# 前日の警報を今日の鳴らし直しの間隔に数えない）。寄り付きが分からない（試走で休場日）ときは持ち越す。
function Select-SessionState {
  param([object]$Previous, [object]$SessionOpen)
  if ($null -eq $Previous) { return $null }
  if ($null -eq $SessionOpen) { return $Previous }
  if ($null -eq $Previous.CheckedAt -or [DateTimeOffset]$Previous.CheckedAt -lt [DateTimeOffset]$SessionOpen) { return $null }
  return $Previous
}

# ---- 通知（クラスタに依らない経路だけ） ------------------------------------------------------------------------

# 🔴 Webhook の URL は表示・ログに出さない。失敗の報告は例外の型と HTTP ステータスだけにする
#    （例外の文言は実装によって URL を含み得る）。戻りは結果の短い文字列。
function Send-DiscordNotification {
  param(
    [string]$Text,
    [string]$WebhookUrl,
    [scriptblock]$Invoker = {
      param($uri, $body)
      # PR #998 監査 B2: -Verbose / -Debug で起動されても Invoke-RestMethod に「POST <URL>」を出させない。
      Invoke-RestMethod -Method Post -Uri $uri -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 10 `
        -ErrorAction Stop -Verbose:$false -Debug:$false | Out-Null
    }
  )
  # PR #998 監査 B2: スクリプトの -Verbose / -Debug は呼び出し先へ既定値として伝わる。この関数の中では詳細・デバッグの出力を止める
  # （Invoker が差し替えられても、URL を含み得る出力が流れないように）。
  $VerbosePreference = 'SilentlyContinue'
  $DebugPreference = 'SilentlyContinue'
  $InformationPreference = 'SilentlyContinue'
  if ([string]::IsNullOrWhiteSpace($WebhookUrl)) { return 'discord=未設定' }
  $content = if ($Text.Length -gt 1900) { $Text.Substring(0, 1900) + '…' } else { $Text }
  $body = @{ content = $content; allowed_mentions = @{ parse = @() } } | ConvertTo-Json -Depth 3 -Compress
  try {
    # 送信器のすべてのストリームを捨てる（情報ストリームは既定値 SilentlyContinue でも記録が流れ、*>&1 で捕まるため
    # 既定値の停止だけでは足りない）。失敗は例外で受ける（既定の送信器は -ErrorAction Stop）。
    # 🔴 *> $null は差し替えた送信器の非終了エラーも飲み込む（送信扱いになる）。既定の送信器は -ErrorAction Stop で例外にしている。
    & $Invoker $WebhookUrl $body *> $null
    return 'discord=送信'
  }
  catch {
    $status = ''
    $response = $_.Exception.PSObject.Properties['Response']
    if ($null -ne $response -and $null -ne $response.Value -and $null -ne $response.Value.PSObject.Properties['StatusCode']) {
      $status = " HTTP $([int]$response.Value.StatusCode)"
    }
    return "discord=失敗（$($_.Exception.GetType().Name)$status）"
  }
}

# Windows の通知（NotifyIcon のバルーン。Windows 11 ではトーストとして出る）。Windows 以外・対話セッションが無いときは出せない。
function Show-HostNotification([string]$Title, [string]$Text, [bool]$IsError) {
  if (-not $IsWindows) { return 'toast=非Windows' }
  try {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $icon = [System.Windows.Forms.NotifyIcon]::new()
    $icon.Icon = if ($IsError) { [System.Drawing.SystemIcons]::Error } else { [System.Drawing.SystemIcons]::Information }
    $icon.Visible = $true
    $tipIcon = if ($IsError) { [System.Windows.Forms.ToolTipIcon]::Error } else { [System.Windows.Forms.ToolTipIcon]::Info }
    $body = if ($Text.Length -gt 250) { $Text.Substring(0, 250) + '…' } else { $Text }
    $icon.ShowBalloonTip(30000, $Title, $body, $tipIcon)
    Start-Sleep -Seconds 10
    $icon.Dispose()
    return 'toast=表示'
  }
  catch {
    return "toast=失敗（$($_.Exception.GetType().Name)）"
  }
}

# ---- 観測（kubectl を読むだけ） -------------------------------------------------------------------------------

function ConvertTo-Instant([object]$Value) {
  if ($null -eq $Value -or "$Value" -eq '') { return $null }
  if ($Value -is [DateTimeOffset]) { return $Value }
  if ($Value -is [datetime]) { return [DateTimeOffset]::new($Value.ToUniversalTime(), [TimeSpan]::Zero) }
  return [DateTimeOffset]::Parse("$Value", [Globalization.CultureInfo]::InvariantCulture)
}

# 戻り: Readable（照会できたか）/ Ready / StartedAt。
# PR #998 監査 N2: 照会の失敗（exit≠0・JSON を読めない）は Readable=$false とし、「Ready でない」と混ぜない。
function Get-ServicePods([string]$Deployment) {
  $json = & kubectl --context $Context --request-timeout=15s -n $Namespace get pods -l "app=$Deployment" -o json 2>$null
  if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ Readable = $false; Ready = $false; StartedAt = $null } }
  try { $parsed = ($json -join "`n") | ConvertFrom-Json -ErrorAction Stop }
  catch { return [pscustomobject]@{ Readable = $false; Ready = $false; StartedAt = $null } }
  if ($null -eq $parsed -or $null -eq $parsed.PSObject.Properties['items']) {
    return [pscustomobject]@{ Readable = $false; Ready = $false; StartedAt = $null }
  }
  $items = @($parsed.items)
  $ready = @($items | Where-Object {
      $_.status.phase -eq 'Running' -and $null -ne $_.status.PSObject.Properties['containerStatuses'] -and
      @($_.status.containerStatuses | Where-Object { -not $_.ready }).Count -eq 0
    })
  if ($ready.Count -eq 0) { return [pscustomobject]@{ Readable = $true; Ready = $false; StartedAt = $null } }
  $started = @($ready | ForEach-Object {
      $running = $_.status.containerStatuses[0].state.PSObject.Properties['running']
      if ($null -ne $running) { ConvertTo-Instant $running.Value.startedAt }
    } | Where-Object { $null -ne $_ } | Sort-Object -Descending)
  return [pscustomobject]@{ Readable = $true; Ready = $true; StartedAt = ($started.Count -gt 0 ? $started[0] : $null) }
}

# 戻り: Ok（読めたか）/ Lines（string[]。空でもよい）。
# 🔴 PR #998 監査 B1: **「空で成功」と「失敗」を区別する。** 市場監視は保有 0 件・閉場中は何も出さないため、
# 空のログは平常である。配列を素で返すと空配列が $null へ展開され、失敗と読まれていた（保有の無い日に毎日警報）。
function Get-ServiceLogs([string]$Deployment, [int]$SinceMinutes) {
  $lines = & kubectl --context $Context --request-timeout=30s -n $Namespace logs "deploy/$Deployment" --since="$($SinceMinutes)m" --timestamps 2>$null
  if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ Ok = $false; Lines = [string[]]@() } }
  return [pscustomobject]@{ Ok = $true; Lines = [string[]]@($lines | Where-Object { $null -ne $_ } | ForEach-Object { "$_" }) }
}

function Get-ClusterObservation([DateTimeOffset]$Now, [object]$SessionOpen, [TimeSpan]$StaleAfter, [object]$HoldingSeenAt) {
  $obs = @{
    Now = $Now; SessionOpen = $SessionOpen; StaleAfter = $StaleAfter; HoldingSeenAt = $HoldingSeenAt
    ApiReachable = $false; ApiDetail = ''; NotReadyServices = @(); Unreadable = @()
    MonitorBeat = $null; ExecutionBeat = $null; MonitorStartedAt = $null; ExecutionStartedAt = $null
  }
  $readyz = & kubectl --context $Context --request-timeout=10s get --raw /readyz 2>&1
  if ($LASTEXITCODE -ne 0) {
    # 詳細は 1 行目だけ（接続拒否・タイムアウトの別が分かれば足りる）。
    $obs.ApiDetail = (@($readyz | ForEach-Object { "$_" }) | Select-Object -First 1)
    return $obs
  }
  $obs.ApiReachable = $true

  $since = [Math]::Max(1, [int]$StaleAfter.TotalMinutes * 3)
  foreach ($pair in @(@($script:MonitorDeployment, 'Monitor', $script:MonitorHeartbeatPattern), @($script:ExecutionDeployment, 'Execution', $script:ExecutionHeartbeatPattern))) {
    $deployment, $key, $pattern = $pair
    $pods = Get-ServicePods $deployment
    if (-not $pods.Readable) { $obs.Unreadable += "$deployment（Pod の照会）"; continue }
    if (-not $pods.Ready) { $obs.NotReadyServices += $deployment; continue }
    $obs["$($key)StartedAt"] = $pods.StartedAt
    # 🔴 kubectl logs deploy/<名前> は Pod を 1 つだけ選ぶ。ロールアウト中は旧 Pod のログを読むことがある（Runbook の限界）。
    $logs = Get-ServiceLogs $deployment $since
    if (-not $logs.Ok) { $obs.Unreadable += "$deployment（ログ）"; continue }
    $obs["$($key)Beat"] = Get-LatestHeartbeat $logs.Lines $pattern
  }
  return $obs
}

# ---- 状態ファイル・ログファイル -------------------------------------------------------------------------------

function ConvertTo-UnixMs([object]$Instant) { if ($null -eq $Instant) { $null } else { ([DateTimeOffset]$Instant).ToUnixTimeMilliseconds() } }
function ConvertFrom-UnixMs([object]$Ms) { if ($null -eq $Ms) { $null } else { [DateTimeOffset]::FromUnixTimeMilliseconds([long]$Ms) } }

function Read-MonitorState([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return $null }
  try {
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
    $checkedAt = $null
    if ($null -ne $raw.PSObject.Properties['checkedAtMs']) { $checkedAt = ConvertFrom-UnixMs $raw.checkedAtMs }
    return [pscustomobject]@{
      Status = $raw.status; Severity = $raw.severity
      LastAlertAt = ConvertFrom-UnixMs $raw.lastAlertAtMs; HoldingSeenAt = ConvertFrom-UnixMs $raw.holdingSeenAtMs
      CheckedAt = $checkedAt
    }
  }
  catch { return $null } # 壊れた状態ファイルは「前回なし」と読む（最悪でも 1 回多く鳴るだけ）
}

function Write-MonitorState([string]$Path, [object]$State) {
  @{
    status = $State.Status; severity = $State.Severity
    lastAlertAtMs = ConvertTo-UnixMs $State.LastAlertAt; holdingSeenAtMs = ConvertTo-UnixMs $State.HoldingSeenAt
    checkedAtMs = ConvertTo-UnixMs $State.CheckedAt
  } | ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding utf8
}

function Add-MonitorLog([string]$Path, [string]$Line) {
  Add-Content -LiteralPath $Path -Value $Line -Encoding utf8
  if ((Get-Item -LiteralPath $Path).Length -gt 1MB) {
    $keep = Get-Content -LiteralPath $Path -Tail 2000 -Encoding utf8
    Set-Content -LiteralPath $Path -Value $keep -Encoding utf8
  }
}

# PR #998 監査 N1: 読めない日付を黙って捨てない（捨てると臨時休場日に「要約が無い」と鳴り続ける／臨時半日の午後を場中と読む）。
# 空・空白だけの要素は無視し、それ以外で yyyy-MM-dd として読めないものがあれば例外にする（呼び出し側が SCRIPT_ERROR にする）。
function ConvertTo-DateList([string[]]$Values) {
  $result = [System.Collections.Generic.List[datetime]]::new()
  foreach ($v in @($Values)) {
    if ([string]::IsNullOrWhiteSpace($v)) { continue }
    $parsed = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($v.Trim(), 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None, [ref]$parsed)) {
      throw "臨時休場日・臨時半日の指定 '$v' を yyyy-MM-dd として読めません。"
    }
    $result.Add($parsed)
  }
  return , $result.ToArray()
}

# ---- 本体 -----------------------------------------------------------------------------------------------------

# -At はテスト用（既定は現在時刻）。他の設定はスクリプトの引数（同じスコープの変数）を読む。
function Invoke-HostLivenessCheck {
  param([object]$At = $null)
  # kubectl の出力（UTF-8）を日本語の照合語と突き合わせるため、ネイティブ出力の復号を UTF-8 に固定する。
  # 既定（日本語 Windows は CP932）のままだと照合語が一致せず、要約が「無い」と読まれる。
  [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
  $current = if ($null -ne $At) { [DateTimeOffset]$At } else { [DateTimeOffset]::UtcNow }

  $configError = $null
  $holidays = [datetime[]]@()
  $halfDays = [datetime[]]@()
  try {
    $holidays = ConvertTo-DateList $ExtraHolidays
    $halfDays = ConvertTo-DateList $ExtraHalfDays
  }
  catch { $configError = $_.Exception.Message }

  # 設定の誤り（N1）は開場を判定できないので、時間に依らず SCRIPT_ERROR として知らせる。
  if ($null -eq $configError -and -not $IgnoreMarketHours -and
    -not (Test-UsMarketOpen -Instant $current -ExtraHolidays $holidays -ExtraHalfDays $halfDays)) {
    Write-Verbose '米国株の通常取引時間外のため何もしません。'
    return 0
  }

  $dir = if ($StateDirectory) { $StateDirectory } else {
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ai-stock-trading/host-liveness'
  }
  $statePath = Join-Path $dir 'state.json'
  $logPath = Join-Path $dir 'host-liveness.log'
  if (-not $DryRun -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

  $staleAfter = [TimeSpan]::FromMinutes($StaleAfterMinutes)
  $sessionOpen = if ($null -eq $configError) { Get-UsSessionOpen -Instant $current -ExtraHolidays $holidays } else { $null }
  $previous = Select-SessionState -Previous (Read-MonitorState $statePath) -SessionOpen $sessionOpen

  try {
    if ($null -ne $configError) { throw $configError }
    if ($null -eq (Get-Command kubectl -ErrorAction SilentlyContinue)) { throw 'kubectl が PATH にありません。' }
    $observation = Get-ClusterObservation $current $sessionOpen $staleAfter ($null -ne $previous ? $previous.HoldingSeenAt : $null)
    $verdict = Get-HostLivenessVerdict $observation
  }
  catch {
    $verdict = [pscustomobject]@{
      Status = 'SCRIPT_ERROR'; Severity = 'alert'; Holding = 'unknown'; HoldingObserved = $false
      MonitorState = $null; ExecutionState = $null
      Message = "死活監視スクリプト自身が失敗しました（$($_.Exception.GetType().Name): $($_.Exception.Message)）。S1 の生存を確かめられていません。"
    }
  }

  $action = Get-NotificationAction -Previous $previous -Verdict $verdict -Now $current -ReAlertInterval ([TimeSpan]::FromMinutes($ReAlertMinutes))
  $stamp = $current.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
  $line = "$stamp status=$($verdict.Status) severity=$($verdict.Severity) holding=$($verdict.Holding) " +
  "monitor=$($verdict.MonitorState) execution=$($verdict.ExecutionState) action=$action $($verdict.Message)"

  if ($DryRun) {
    Write-Host "[dry-run] $line"
  }
  else {
    $delivery = ''
    if ($action -ne 'none') {
      $title = switch ($action) {
        'recovered' { "AST 死活監視: 回復（$($verdict.Status)）" }
        default { "AST 死活監視: $($verdict.Status)" }
      }
      $text = "$title — $($verdict.Message)"
      $toast = Show-HostNotification $title $verdict.Message ($verdict.Severity -eq 'alert')
      $discord = Send-DiscordNotification -Text $text -WebhookUrl ([Environment]::GetEnvironmentVariable($script:DiscordWebhookVariable))
      $delivery = " delivery=[$toast; $discord]"
    }
    Add-MonitorLog $logPath ($line + $delivery)
    Write-MonitorState $statePath (Get-NextState -Previous $previous -Verdict $verdict -Action $action -Now $current)
    if ($action -ne 'none') { Write-Host ($line + $delivery) }
  }

  if ($verdict.Status -eq 'SCRIPT_ERROR') { return 2 }
  if ($verdict.Severity -in 'alert', 'warn') { return 1 }
  return 0
}

if ($env:AST_HOST_LIVENESS_LIB -eq '1') { return }
exit (Invoke-HostLivenessCheck)
