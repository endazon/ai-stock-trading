#Requires -Version 7.0
# FR-10, UC-02, #937, IADR-0344 追記（17）: scripts/host-liveness-monitor.ps1（ホスト側の死活監視）の純ロジックを固定する。
#
#   pwsh -NoProfile -File scripts/host-liveness-monitor.test.ps1
#
# 実クラスタ・kubectl・通知は要らない。対象スクリプトは AST_HOST_LIVENESS_LIB=1 で dot-source すると
# 関数定義だけを読み込む（scripts/cutover-count-reconcile.test.sh の AST_CUTOVER_LIB=1 と同じ idiom）。
# フレームワークは足さない（既存の *.test.sh と同じ「素のスクリプト＋アサート＋失敗があれば exit 1」）。
#
# 固定する不変条件（テスト仕様書 FR-10_risk-controls-tests.md の T-10-1040〜T-10-1049）:
#   - 開場判定は共有カーネル MarketHours と同じ規則（米東 9:30–16:00・夏時間・規則計算の休場日・半日 13:00）。
#     fixture は backend/Shared/AiStockTrading.Shared.Kernel.Tests/Trading/MarketHoursTests.cs と同じ値を使う。
#   - 生存要約の鮮度で S1 の監視の死を検知し、「要約が無い」を「保有なし」と書かない（unknown ≠ none）。
#   - 平常時は鳴らさず、遷移で鳴らし、alert は間隔で鳴らし直し、回復を 1 回知らせる。
#   - Discord Webhook の URL を結果の文字列にも詳細・デバッグ・情報のストリームにも出さない。
#   - 観測の層（kubectl のスタブ）: 空で成功した読み取りを失敗と読まない／照会の失敗を「Ready でない」と混ぜない／読み取り以外を呼ばない。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$env:AST_HOST_LIVENESS_LIB = '1'
. (Join-Path $PSScriptRoot 'host-liveness-monitor.ps1')
Remove-Item Env:AST_HOST_LIVENESS_LIB

$script:passed = 0
$script:failed = @()

function Assert-Equal($Expected, $Actual, [string]$Name) {
  if ($Expected -eq $Actual) { $script:passed++ }
  else { $script:failed += "FAIL: $Name — expected [$Expected] actual [$Actual]" }
}

function Assert-True([bool]$Condition, [string]$Name) { Assert-Equal $true $Condition $Name }

function Utc([int]$y, [int]$mo, [int]$d, [int]$h, [int]$mi, [int]$s = 0) {
  [DateTimeOffset]::new($y, $mo, $d, $h, $mi, $s, [TimeSpan]::Zero)
}

# ---- T-10-1040: 米国市場は通常取引時間の内側だけ場中（夏時間の両端でも同じ現地時刻が同じ判定） ----------------
$cases = @(
  @(2026, 9, 23, 13, 29, $false), @(2026, 9, 23, 13, 30, $true), @(2026, 9, 23, 19, 59, $true),
  @(2026, 9, 23, 20, 0, $false), @(2026, 9, 23, 23, 0, $false), @(2026, 9, 26, 13, 30, $false), @(2026, 9, 27, 13, 30, $false),
  @(2026, 3, 6, 14, 30, $true), @(2026, 3, 6, 13, 30, $false), @(2026, 3, 9, 13, 30, $true), @(2026, 3, 9, 12, 30, $false),
  @(2026, 10, 30, 13, 30, $true), @(2026, 11, 2, 14, 30, $true), @(2026, 11, 2, 13, 30, $false)
)
foreach ($c in $cases) {
  $at = Utc $c[0] $c[1] $c[2] $c[3] $c[4]
  Assert-Equal $c[5] (Test-UsMarketOpen -Instant $at) "T-10-1040 $($at.ToString('u')) の場中判定"
}
# 境界: 16:00:00 ちょうどは場中ではない（終了は排他）。15:59:59 は場中。
Assert-Equal $true (Test-UsMarketOpen -Instant (Utc 2026 9 23 19 59 59)) 'T-10-1040 15:59:59 ET は場中'
Assert-Equal $false (Test-UsMarketOpen -Instant (Utc 2026 9 23 20 0 0)) 'T-10-1040 16:00:00 ET は場外'

# ---- T-10-1041: 規則計算の休場日（構成が空でも閉場）と隣接営業日 --------------------------------------------------
$holidays = @(
  @(2026, 1, 1), @(2026, 1, 19), @(2026, 2, 16), @(2026, 4, 3), @(2026, 5, 25), @(2026, 6, 19),
  @(2026, 7, 3), @(2026, 9, 7), @(2026, 11, 26), @(2026, 12, 25), @(2023, 1, 2)
)
foreach ($h in $holidays) {
  $d = [datetime]::new($h[0], $h[1], $h[2])
  Assert-True (Test-UsHoliday $d) "T-10-1041 $($d.ToString('yyyy-MM-dd')) は休場日"
  $open = [DateTimeOffset]::new($d.Add([TimeSpan]::new(15, 0, 0)), [TimeSpan]::Zero) # 10:00〜11:00 ET 相当
  Assert-Equal $false (Test-UsMarketOpen -Instant $open) "T-10-1041 $($d.ToString('yyyy-MM-dd')) の日中は場外"
}
$adjacent = @(@(2026, 1, 2), @(2026, 4, 6), @(2026, 7, 6), @(2026, 11, 30), @(2022, 1, 3), @(2021, 6, 18))
foreach ($a in $adjacent) {
  $d = [datetime]::new($a[0], $a[1], $a[2])
  Assert-Equal $false (Test-UsHoliday $d) "T-10-1041 $($d.ToString('yyyy-MM-dd')) は休場日ではない"
  $open = [DateTimeOffset]::new($d.Add([TimeSpan]::new(15, 0, 0)), [TimeSpan]::Zero)
  Assert-Equal $true (Test-UsMarketOpen -Instant $open) "T-10-1041 $($d.ToString('yyyy-MM-dd')) の日中は場中"
}
# 2021-12-31（金）は開く: 2022-01-01 が土曜でも元日は前倒ししない。
Assert-Equal $false (Test-UsHoliday ([datetime]::new(2021, 12, 31))) 'T-10-1041 元日が土曜の年の前日（12/31）は休場日ではない'

# ---- T-10-1042: 半日取引日は 13:00 ET で閉場し、構成の臨時休場・臨時半日は足される ---------------------------------
$halfCases = @(
  @(2026, 11, 27, 17, 59, $true), @(2026, 11, 27, 18, 0, $false), @(2026, 11, 27, 20, 59, $false),
  @(2026, 12, 24, 18, 0, $false), @(2026, 12, 24, 17, 59, $true), @(2025, 7, 3, 18, 0, $false)
)
foreach ($c in $halfCases) {
  $at = Utc $c[0] $c[1] $c[2] $c[3] $c[4]
  Assert-Equal $c[5] (Test-UsMarketOpen -Instant $at) "T-10-1042 $($at.ToString('u')) の半日判定"
}
$extraDay = [datetime]::new(2026, 9, 23)
Assert-Equal $false (Test-UsMarketOpen -Instant (Utc 2026 9 23 15 0) -ExtraHolidays @($extraDay)) 'T-10-1042 臨時休場日は場外'
Assert-Equal $false (Test-UsMarketOpen -Instant (Utc 2026 9 23 18 0) -ExtraHalfDays @($extraDay)) 'T-10-1042 臨時半日は 13:00 ET で閉場'
Assert-Equal $true (Test-UsMarketOpen -Instant (Utc 2026 9 23 16 59) -ExtraHalfDays @($extraDay)) 'T-10-1042 臨時半日の 12:59 ET は場中'
# 寄り付きの時刻（猶予の基準）: 夏時間は 13:30Z、冬時間は 14:30Z、休場日・週末は $null。
Assert-Equal (Utc 2026 9 23 13 30) (Get-UsSessionOpen -Instant (Utc 2026 9 23 18 0)) 'T-10-1042 夏時間の寄り付きは 13:30Z'
Assert-Equal (Utc 2026 11 2 14 30) (Get-UsSessionOpen -Instant (Utc 2026 11 2 18 0)) 'T-10-1042 冬時間の寄り付きは 14:30Z'
Assert-Equal $null (Get-UsSessionOpen -Instant (Utc 2026 9 26 15 0)) 'T-10-1042 土曜は寄り付きなし'
Assert-Equal $null (Get-UsSessionOpen -Instant (Utc 2026 11 26 15 0)) 'T-10-1042 感謝祭は寄り付きなし'

# ---- T-10-1043: ログ行から最新の生存要約（時刻・件数）を読む --------------------------------------------------------
$lines = @(
  '2026-09-25T18:40:19.820230816+09:00 [09:40:19 INF] 市場を閉場と判定しています（UnitedStates）。',
  '2026-09-25T22:31:05.100000000+09:00 [13:31:05 INF] 損切り評価は稼働中: 保有 1 件を評価（要約は 00:05:00 に 1 回）: AAPL/UnitedStates Buy 1428株',
  '2026-09-25T22:36:07.5+09:00 [13:36:07 INF] 損切り評価は稼働中: 保有 2 件を評価（要約は 00:05:00 に 1 回）: …',
  '2026-09-25T13:37:00Z [13:37:00 INF] ソフトウェア逆指値（S1）の保護は継続中: Active 2 件（要約は 00:05:00 に 1 回。価格との比較は市場監視が行う）: …',
  'タイムスタンプの無い行 損切り評価は稼働中: 保有 9 件を評価',
  ''
)
$mm = Get-LatestHeartbeat $lines $script:MonitorHeartbeatPattern
Assert-Equal (Utc 2026 9 25 13 36 7).AddMilliseconds(500) $mm.At 'T-10-1043 市場監視の最新の要約の時刻（+09:00・小数 1 桁）'
Assert-Equal 2 $mm.Count 'T-10-1043 市場監視の最新の要約の保有件数'
$oe = Get-LatestHeartbeat $lines $script:ExecutionHeartbeatPattern
Assert-Equal (Utc 2026 9 25 13 37 0) $oe.At 'T-10-1043 発注執行の要約の時刻（Z）'
Assert-Equal 2 $oe.Count 'T-10-1043 発注執行の Active 件数'
Assert-Equal $null (Get-LatestHeartbeat @('2026-09-25T13:37:00Z 無関係な行') $script:MonitorHeartbeatPattern) 'T-10-1043 当たらなければ $null'
Assert-Equal (Utc 2026 9 25 9 55 29).AddTicks(2566076) (ConvertFrom-KubectlTimestamp '2026-09-25T18:55:29.256607676+09:00 x') 'T-10-1043 小数 9 桁は 7 桁へ切り詰める'
# 照合語は本番コードの実文言に当たること（文言が変われば本テストが赤になるよう、実ファイルから引く）。
$repo = Split-Path -Parent $PSScriptRoot
$mmSource = Get-Content -Raw -Encoding utf8 (Join-Path $repo 'backend/Services/MarketMonitorService/Hosted/StopLossLivenessReporter.cs')
$oeSource = Get-Content -Raw -Encoding utf8 (Join-Path $repo 'backend/Services/OrderExecutionService/Features/OrderExecution/GuardProtectiveStops/SoftwareStopLivenessReporter.cs')
Assert-True ([regex]::IsMatch(($mmSource -replace '\{Count\}', '3'), $script:MonitorHeartbeatPattern)) 'T-10-1043 市場監視の照合語が本番の文言に当たる'
Assert-True ([regex]::IsMatch(($oeSource -replace '\{Count\}', '3'), $script:ExecutionHeartbeatPattern)) 'T-10-1043 発注執行の照合語が本番の文言に当たる'

# ---- 判定の土台 ---------------------------------------------------------------------------------------------------
$now = Utc 2026 9 25 15 0
$open = Utc 2026 9 25 13 30
function New-Observation([hashtable]$Overrides = @{}) {
  $o = @{
    Now = $now; SessionOpen = $open; StaleAfter = [TimeSpan]::FromMinutes(10)
    ApiReachable = $true; ApiDetail = ''; NotReadyServices = @(); Unreadable = @()
    MonitorBeat = $null; ExecutionBeat = $null
    MonitorStartedAt = (Utc 2026 9 25 9 0); ExecutionStartedAt = (Utc 2026 9 25 9 0); HoldingSeenAt = $null
  }
  foreach ($k in $Overrides.Keys) { $o[$k] = $Overrides[$k] }
  return $o
}
function Beat([int]$MinutesAgo, [int]$Count = 1) { [pscustomobject]@{ At = $now.AddMinutes(-$MinutesAgo); Count = $Count } }

# ---- T-10-1044: クラスタが見えない・Pod が Ready でない・ログを読めないは保有に依らず alert -------------------------
$v = Get-HostLivenessVerdict (New-Observation @{ ApiReachable = $false; ApiDetail = 'connection refused' })
Assert-Equal 'CLUSTER_UNREACHABLE' $v.Status 'T-10-1044 API 不達の状態'
Assert-Equal 'alert' $v.Severity 'T-10-1044 API 不達は alert'
Assert-Equal 'unknown' $v.Holding 'T-10-1044 API 不達の保有は不明'
Assert-True ($v.Message -match 'Rancher Desktop') 'T-10-1044 API 不達の文面が Rancher Desktop を指す'
$v = Get-HostLivenessVerdict (New-Observation @{ NotReadyServices = @('market-monitor-service') })
Assert-Equal 'SERVICE_NOT_READY' $v.Status 'T-10-1044 Pod 非 Ready の状態'
Assert-Equal 'alert' $v.Severity 'T-10-1044 Pod 非 Ready は alert'
$v = Get-HostLivenessVerdict (New-Observation @{ Unreadable = @('order-execution-service（ログ）') })
Assert-Equal 'UNREADABLE' $v.Status 'T-10-1044 読み取りの失敗の状態'
Assert-Equal 'alert' $v.Severity 'T-10-1044 読み取りの失敗は alert（確かめられないを平常と読まない）'
$v = Get-HostLivenessVerdict (New-Observation @{ Unreadable = @('order-execution-service（Pod の照会）'); NotReadyServices = @() })
Assert-Equal 'UNREADABLE' $v.Status 'T-10-1044 Pod の照会の失敗は SERVICE_NOT_READY ではなく UNREADABLE（監査 N2）'

# ---- T-10-1045: S1 行が Active なのに評価の要約が止まれば alert。寄り付き・再起動の直後は猶予 -----------------------
$v = Get-HostLivenessVerdict (New-Observation @{ ExecutionBeat = (Beat 2 2); MonitorBeat = (Beat 4 1) })
Assert-Equal 'PROTECTED' $v.Status 'T-10-1045 両方新しければ PROTECTED'
Assert-Equal 'ok' $v.Severity 'T-10-1045 PROTECTED は ok'
Assert-Equal 'yes' $v.Holding 'T-10-1045 PROTECTED の保有は yes'
$v = Get-HostLivenessVerdict (New-Observation @{ ExecutionBeat = (Beat 2 2); MonitorBeat = (Beat 11 1) })
Assert-Equal 'EVALUATION_SILENT' $v.Status 'T-10-1045 S1 行 Active・評価の要約が 11 分前 → EVALUATION_SILENT'
Assert-Equal 'alert' $v.Severity 'T-10-1045 EVALUATION_SILENT は alert'
$v = Get-HostLivenessVerdict (New-Observation @{ ExecutionBeat = (Beat 2 2); MonitorBeat = (Beat 10 1) })
Assert-Equal 'PROTECTED' $v.Status 'T-10-1045 ちょうど StaleAfter（10 分）はまだ新しい'
$v = Get-HostLivenessVerdict (New-Observation @{ ExecutionBeat = (Beat 2 2); MonitorBeat = $null })
Assert-Equal 'EVALUATION_SILENT' $v.Status 'T-10-1045 評価の要約が読んだ範囲に無い → EVALUATION_SILENT'
$v = Get-HostLivenessVerdict (New-Observation @{ Now = (Utc 2026 9 25 13 35); ExecutionBeat = [pscustomobject]@{ At = (Utc 2026 9 25 13 33); Count = 2 }; MonitorBeat = $null })
Assert-Equal 'WARMING_UP' $v.Status 'T-10-1045 寄り付き 5 分後は評価の初回を待つ（猶予）'
$v = Get-HostLivenessVerdict (New-Observation @{ ExecutionBeat = (Beat 2 2); MonitorBeat = $null; MonitorStartedAt = $now.AddMinutes(-3) })
Assert-Equal 'WARMING_UP' $v.Status 'T-10-1045 市場監視の再起動 3 分後は猶予'
$v = Get-HostLivenessVerdict (New-Observation @{ ExecutionBeat = $null; MonitorBeat = (Beat 3 1) })
Assert-Equal 'EVALUATING' $v.Status 'T-10-1045 S1 行の要約が無く評価が新しい → EVALUATING（S1 以外の手法・paper）'
Assert-Equal 'ok' $v.Severity 'T-10-1045 EVALUATING は ok'

# ---- T-10-1046: 要約が両方無いとき、「保有なし」と書かない（unknown ≠ none） ------------------------------------------
$v = Get-HostLivenessVerdict (New-Observation @{})
Assert-Equal 'NO_HEARTBEAT_UNKNOWN' $v.Status 'T-10-1046 どちらも無く保有を見ていない → NO_HEARTBEAT_UNKNOWN'
Assert-Equal 'ok' $v.Severity 'T-10-1046 NO_HEARTBEAT_UNKNOWN は鳴らさない'
Assert-Equal 'unknown' $v.Holding 'T-10-1046 保有は unknown（none ではない）'
Assert-True ($v.Message -match '区別できません') 'T-10-1046 文面が区別できないことを明記する'
Assert-True (-not ($v.Message -match '^保有なしです|保有はありません')) 'T-10-1046 文面が保有なしと断定しない'
$v = Get-HostLivenessVerdict (New-Observation @{ HoldingSeenAt = $now.AddMinutes(-20) })
Assert-Equal 'HEARTBEATS_STOPPED' $v.Status 'T-10-1046 この取引時間中に保有を見ていた → HEARTBEATS_STOPPED'
Assert-Equal 'warn' $v.Severity 'T-10-1046 HEARTBEATS_STOPPED は warn'
$v = Get-HostLivenessVerdict (New-Observation @{ HoldingSeenAt = $open.AddHours(-12) })
Assert-Equal 'NO_HEARTBEAT_UNKNOWN' $v.Status 'T-10-1046 前の取引時間に見た保有は数えない（前日に閉じた建玉で鳴らさない）'
$v = Get-HostLivenessVerdict (New-Observation @{ SessionOpen = $null; HoldingSeenAt = $now.AddMinutes(-20) })
Assert-Equal 'NO_HEARTBEAT_UNKNOWN' $v.Status 'T-10-1046 寄り付きが無い（試走で場外）ときは記憶を数えない'
$v = Get-HostLivenessVerdict (New-Observation @{ ExecutionBeat = (Beat 2 2); MonitorBeat = (Beat 4 1) })
Assert-True $v.HoldingObserved 'T-10-1046 要約が新しいときだけ「保有を見た」を記録する'

# ---- T-10-1047: 通知は遷移で鳴らし、alert は間隔で鳴らし直し、warn は 1 回、回復を 1 回 -------------------------------
$re = [TimeSpan]::FromMinutes(30)
function V([string]$Status, [string]$Severity) { [pscustomobject]@{ Status = $Status; Severity = $Severity; HoldingObserved = $false } }
function P([string]$Status, [string]$Severity, [object]$LastAlertAt) { [pscustomobject]@{ Status = $Status; Severity = $Severity; LastAlertAt = $LastAlertAt; HoldingSeenAt = $null } }
Assert-Equal 'none' (Get-NotificationAction -Previous $null -Verdict (V 'PROTECTED' 'ok') -Now $now -ReAlertInterval $re) 'T-10-1047 初回の平常は鳴らさない'
Assert-Equal 'none' (Get-NotificationAction -Previous (P 'PROTECTED' 'ok' $null) -Verdict (V 'EVALUATING' 'ok') -Now $now -ReAlertInterval $re) 'T-10-1047 平常同士の遷移は鳴らさない'
Assert-Equal 'alert' (Get-NotificationAction -Previous $null -Verdict (V 'CLUSTER_UNREACHABLE' 'alert') -Now $now -ReAlertInterval $re) 'T-10-1047 初回の alert は鳴らす'
Assert-Equal 'alert' (Get-NotificationAction -Previous (P 'PROTECTED' 'ok' $null) -Verdict (V 'EVALUATION_SILENT' 'alert') -Now $now -ReAlertInterval $re) 'T-10-1047 ok → alert は鳴らす'
Assert-Equal 'none' (Get-NotificationAction -Previous (P 'CLUSTER_UNREACHABLE' 'alert' $now.AddMinutes(-29)) -Verdict (V 'CLUSTER_UNREACHABLE' 'alert') -Now $now -ReAlertInterval $re) 'T-10-1047 同じ alert は間隔内なら鳴らさない'
Assert-Equal 'realert' (Get-NotificationAction -Previous (P 'CLUSTER_UNREACHABLE' 'alert' $now.AddMinutes(-30)) -Verdict (V 'CLUSTER_UNREACHABLE' 'alert') -Now $now -ReAlertInterval $re) 'T-10-1047 同じ alert は間隔で鳴らし直す'
Assert-Equal 'alert' (Get-NotificationAction -Previous (P 'CLUSTER_UNREACHABLE' 'alert' $now.AddMinutes(-1)) -Verdict (V 'SERVICE_NOT_READY' 'alert') -Now $now -ReAlertInterval $re) 'T-10-1047 alert の種類が変われば鳴らす'
Assert-Equal 'alert' (Get-NotificationAction -Previous (P 'PROTECTED' 'ok' $null) -Verdict (V 'HEARTBEATS_STOPPED' 'warn') -Now $now -ReAlertInterval $re) 'T-10-1047 warn への遷移は鳴らす'
Assert-Equal 'none' (Get-NotificationAction -Previous (P 'HEARTBEATS_STOPPED' 'warn' $now.AddHours(-2)) -Verdict (V 'HEARTBEATS_STOPPED' 'warn') -Now $now -ReAlertInterval $re) 'T-10-1047 同じ warn は鳴らし直さない'
Assert-Equal 'recovered' (Get-NotificationAction -Previous (P 'CLUSTER_UNREACHABLE' 'alert' $now.AddMinutes(-5)) -Verdict (V 'PROTECTED' 'ok') -Now $now -ReAlertInterval $re) 'T-10-1047 alert → ok は回復を知らせる'
Assert-Equal 'recovered' (Get-NotificationAction -Previous (P 'HEARTBEATS_STOPPED' 'warn' $null) -Verdict (V 'NO_HEARTBEAT_UNKNOWN' 'ok') -Now $now -ReAlertInterval $re) 'T-10-1047 warn → ok は回復を知らせる'
$next = Get-NextState -Previous (P 'CLUSTER_UNREACHABLE' 'alert' $now.AddMinutes(-40)) -Verdict (V 'CLUSTER_UNREACHABLE' 'alert') -Action 'realert' -Now $now
Assert-Equal $now $next.LastAlertAt 'T-10-1047 鳴らし直したら最終通知時刻を進める'
$next = Get-NextState -Previous (P 'CLUSTER_UNREACHABLE' 'alert' $now.AddMinutes(-10)) -Verdict (V 'CLUSTER_UNREACHABLE' 'alert') -Action 'none' -Now $now
Assert-Equal $now.AddMinutes(-10) $next.LastAlertAt 'T-10-1047 鳴らさなければ最終通知時刻を保つ'
$next = Get-NextState -Previous (P 'CLUSTER_UNREACHABLE' 'alert' $now.AddMinutes(-10)) -Verdict (V 'PROTECTED' 'ok') -Action 'recovered' -Now $now
Assert-Equal $null $next.LastAlertAt 'T-10-1047 回復したら最終通知時刻を捨てる'
$seen = [pscustomobject]@{ Status = 'PROTECTED'; Severity = 'ok'; HoldingObserved = $true }
Assert-Equal $now (Get-NextState -Previous $null -Verdict $seen -Action 'none' -Now $now).HoldingSeenAt 'T-10-1047 保有を見たら記録する'
Assert-Equal $now.AddMinutes(-7) (Get-NextState -Previous ([pscustomobject]@{ Status = 'PROTECTED'; Severity = 'ok'; LastAlertAt = $null; HoldingSeenAt = $now.AddMinutes(-7) }) -Verdict (V 'NO_HEARTBEAT_UNKNOWN' 'ok') -Action 'none' -Now $now).HoldingSeenAt 'T-10-1047 見ていない回は記録を保つ'

# 監査 N3: 前の取引時間に書かれた状態は持ち越さない（翌日の最初の実行で偽の「回復」を出さない）。
$yesterdayState = [pscustomobject]@{ Status = 'CLUSTER_UNREACHABLE'; Severity = 'alert'; LastAlertAt = $open.AddHours(-20); HoldingSeenAt = $null; CheckedAt = $open.AddHours(-20) }
Assert-Equal $null (Select-SessionState -Previous $yesterdayState -SessionOpen $open) 'T-10-1047 前の取引時間の状態は捨てる'
$todayState = [pscustomobject]@{ Status = 'CLUSTER_UNREACHABLE'; Severity = 'alert'; LastAlertAt = $open.AddMinutes(5); HoldingSeenAt = $null; CheckedAt = $open.AddMinutes(5) }
Assert-Equal 'CLUSTER_UNREACHABLE' (Select-SessionState -Previous $todayState -SessionOpen $open).Status 'T-10-1047 同じ取引時間の状態は持ち越す'
$legacyState = [pscustomobject]@{ Status = 'CLUSTER_UNREACHABLE'; Severity = 'alert'; LastAlertAt = $null; HoldingSeenAt = $null; CheckedAt = $null }
Assert-Equal $null (Select-SessionState -Previous $legacyState -SessionOpen $open) 'T-10-1047 確認時刻の無い（旧形式の）状態は捨てる'
Assert-Equal 'CLUSTER_UNREACHABLE' (Select-SessionState -Previous $yesterdayState -SessionOpen $null).Status 'T-10-1047 寄り付きが無い（試走で休場日）ときは持ち越す'
Assert-Equal 'none' (Get-NotificationAction -Previous (Select-SessionState -Previous $yesterdayState -SessionOpen $open) -Verdict (V 'PROTECTED' 'ok') -Now $now -ReAlertInterval $re) 'T-10-1047 前日の警報のあと今日の最初が平常なら「回復」を出さない'
Assert-Equal $now (Get-NextState -Previous $null -Verdict (V 'PROTECTED' 'ok') -Action 'none' -Now $now).CheckedAt 'T-10-1047 次の状態に確認時刻を残す'

# ---- T-10-1048: Discord Webhook の URL を結果に出さない ------------------------------------------------------------
$secret = 'https://discord.example.invalid/api/webhooks/000/SECRET-TOKEN'
$captured = @{}
$ok = Send-DiscordNotification -Text '本文' -WebhookUrl $secret -Invoker { param($uri, $body) $captured.uri = $uri; $captured.body = $body }
Assert-Equal 'discord=送信' $ok 'T-10-1048 送信成功の結果'
Assert-Equal $secret $captured.uri 'T-10-1048 URL は送信先にだけ使う'
Assert-True (-not ($captured.body -match 'SECRET-TOKEN')) 'T-10-1048 本文に URL を載せない'
Assert-True ($captured.body -match '"parse":\[\]') 'T-10-1048 メンションを展開しない'
$failedResult = Send-DiscordNotification -Text '本文' -WebhookUrl $secret -Invoker { param($uri, $body) throw [System.Net.Http.HttpRequestException]::new("POST $uri failed") }
Assert-True (-not ($failedResult -match 'SECRET-TOKEN|discord\.example')) 'T-10-1048 失敗の結果に URL を出さない（例外の文言が URL を含んでも）'
Assert-True ($failedResult -match 'HttpRequestException') 'T-10-1048 失敗の結果は例外の型を示す'
Assert-Equal 'discord=未設定' (Send-DiscordNotification -Text '本文' -WebhookUrl '') 'T-10-1048 未設定なら送らない'
$long = Send-DiscordNotification -Text ('あ' * 3000) -WebhookUrl $secret -Invoker { param($uri, $body) $captured.body = $body }
Assert-True (($captured.body | ConvertFrom-Json).content.Length -le 1901) 'T-10-1048 本文は Discord の上限（2000 字）に収める'

# 監査 B2: -Verbose / -Debug で起動されても URL を出さない（詳細・デバッグ・情報の各ストリームを捕まえて見る）。
$sentinel = 'SENTINEL-B2-TOKEN'
$streams = & {
  $VerbosePreference = 'Continue'; $DebugPreference = 'Continue'; $InformationPreference = 'Continue'
  Send-DiscordNotification -Text '本文' -WebhookUrl "https://discord.example.invalid/api/webhooks/0/$sentinel" -Invoker {
    param($uri, $body) Write-Verbose "POST $uri"; Write-Debug "POST $uri"; Write-Information "POST $uri"
  }
} *>&1 | Out-String
Assert-True (-not ($streams -match $sentinel)) 'T-10-1048 詳細・デバッグ・情報のストリームに URL を出さない（差し替えた送信器）'
Assert-True ($streams -match 'discord=送信') 'T-10-1048 差し替えた送信器の結果は送信'
$streams = & {
  $VerbosePreference = 'Continue'; $DebugPreference = 'Continue'
  Send-DiscordNotification -Text '本文' -WebhookUrl "http://127.0.0.1:9/api/webhooks/0/$sentinel"
} *>&1 | Out-String
Assert-True (-not ($streams -match $sentinel)) 'T-10-1048 既定の送信器（Invoke-RestMethod）も -Verbose / -Debug の下で URL を出さない'
Assert-True ($streams -match 'discord=失敗') 'T-10-1048 既定の送信器の接続失敗は失敗として返る（黙って送信扱いにしない）'

# ---- T-10-1049: 観測の層（kubectl のスタブ）と本体。空で成功した読み取りを失敗と読まない ----------------------------
# kubectl を関数で差し替える（関数は外部コマンドより先に解決される）。呼び出しはすべて記録し、読み取り以外が無いことも見る。
$script:Stub = $null
$script:Toasts = [System.Collections.Generic.List[string]]::new()
function PodJson([bool]$Ready = $true) {
  @{ items = @(@{ status = @{ phase = 'Running'; containerStatuses = @(@{ ready = $Ready; state = @{ running = @{ startedAt = '2026-09-25T09:00:00Z' } } }) } }) } |
    ConvertTo-Json -Depth 10
}
function Reset-Stub {
  $script:Stub = @{
    Readyz = @{ Exit = 0; Out = @('ok') }
    Pods = @{ 'market-monitor-service' = @{ Exit = 0; Out = @(PodJson) }; 'order-execution-service' = @{ Exit = 0; Out = @(PodJson) } }
    Logs = @{ 'market-monitor-service' = @{ Exit = 0; Out = @() }; 'order-execution-service' = @{ Exit = 0; Out = @() } }
    Calls = [System.Collections.Generic.List[string]]::new()
  }
  $script:Toasts.Clear()
}
function kubectl {
  $argv = @($args | ForEach-Object { "$_" })
  $script:Stub.Calls.Add($argv -join ' ')
  if ($argv -contains '--raw') { $r = $script:Stub.Readyz }
  elseif ($argv -contains 'pods') { $r = $script:Stub.Pods[((@($argv | Where-Object { $_ -like 'app=*' }))[0] -replace '^app=', '')] }
  elseif ($argv -contains 'logs') { $r = $script:Stub.Logs[((@($argv | Where-Object { $_ -like 'deploy/*' }))[0] -replace '^deploy/', '')] }
  else { throw "想定外の kubectl 呼び出し: $($argv -join ' ')" }
  $global:LASTEXITCODE = $r.Exit
  foreach ($line in @($r.Out)) { $line }
}
# 本体が出す Windows の通知を差し替える（テストでデスクトップに通知を出さない）。
function Show-HostNotification([string]$Title, [string]$Text, [bool]$IsError) { $script:Toasts.Add($Title); 'toast=stub' }
[Environment]::SetEnvironmentVariable($script:DiscordWebhookVariable, $null, 'Process')

$tradingAt = Utc 2026 9 25 15 0      # 金曜 11:00 EDT（場中）
$saturdayAt = Utc 2026 9 26 15 0     # 土曜
$script:TempDirs = [System.Collections.Generic.List[string]]::new()
function New-StateDir {
  $d = Join-Path ([IO.Path]::GetTempPath()) ("ast-liveness-test-" + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $d | Out-Null
  $script:TempDirs.Add($d)
  $d
}
function Read-StateStatus([string]$Dir) { (Get-Content -Raw -Encoding utf8 (Join-Path $Dir 'state.json') | ConvertFrom-Json).status }
$IgnoreMarketHours = $false; $DryRun = $false; $ExtraHolidays = @(); $ExtraHalfDays = @(); $StaleAfterMinutes = 10; $ReAlertMinutes = 30

# 監査 B1: 読み取りが「空で成功」
Reset-Stub
$logs = Get-ServiceLogs 'market-monitor-service' 30
Assert-Equal $true $logs.Ok 'T-10-1049 空で成功したログは Ok（失敗ではない）'
Assert-Equal 0 @($logs.Lines).Count 'T-10-1049 空で成功したログの行は 0 件'
$script:Stub.Logs['market-monitor-service'] = @{ Exit = 1; Out = @() }
Assert-Equal $false (Get-ServiceLogs 'market-monitor-service' 30).Ok 'T-10-1049 exit≠0 のログは Ok でない'
$script:Stub.Logs['market-monitor-service'] = @{ Exit = 0; Out = @('2026-09-25T14:58:00Z [14:58:00 INF] 損切り評価は稼働中: 保有 1 件を評価（要約は 00:05:00 に 1 回）: x') }
Assert-Equal 1 @((Get-ServiceLogs 'market-monitor-service' 30).Lines).Count 'T-10-1049 1 行だけのログも配列で返る'

Reset-Stub
$pods = Get-ServicePods 'market-monitor-service'
Assert-True ($pods.Readable -and $pods.Ready) 'T-10-1049 Running かつ全コンテナ Ready の Pod は Ready'
Assert-Equal ([DateTimeOffset]::new(2026, 9, 25, 9, 0, 0, [TimeSpan]::Zero)) $pods.StartedAt 'T-10-1049 起動時刻を読む'
$script:Stub.Pods['market-monitor-service'] = @{ Exit = 0; Out = @(PodJson $false) }
$pods = Get-ServicePods 'market-monitor-service'
Assert-True ($pods.Readable -and -not $pods.Ready) 'T-10-1049 Ready でない Pod は Readable かつ Ready でない'
$script:Stub.Pods['market-monitor-service'] = @{ Exit = 0; Out = @('{"items":[]}') }
Assert-True ((Get-ServicePods 'market-monitor-service').Readable -and -not (Get-ServicePods 'market-monitor-service').Ready) 'T-10-1049 Pod が 0 件なら Ready でない'
$script:Stub.Pods['market-monitor-service'] = @{ Exit = 1; Out = @() }
Assert-Equal $false (Get-ServicePods 'market-monitor-service').Readable 'T-10-1049 照会の失敗は Readable でない（監査 N2）'
$script:Stub.Pods['market-monitor-service'] = @{ Exit = 0; Out = @('not json') }
Assert-Equal $false (Get-ServicePods 'market-monitor-service').Readable 'T-10-1049 JSON を読めなければ Readable でない'

# 本体（Invoke-HostLivenessCheck）
function Invoke-Case([object]$At, [scriptblock]$Arrange) {
  Reset-Stub
  if ($Arrange) { & $Arrange }
  $script:CaseDir = New-StateDir
  $script:StateDirectory = $script:CaseDir
  return Invoke-HostLivenessCheck -At $At
}

$code = Invoke-Case $tradingAt $null
Assert-Equal 0 $code 'T-10-1049 保有なし（両方のログが空で成功）は exit 0（監査 B1: 以前は毎日 UNREADABLE の警報）'
Assert-Equal 'NO_HEARTBEAT_UNKNOWN' (Read-StateStatus $script:CaseDir) 'T-10-1049 保有なし（空で成功）は NO_HEARTBEAT_UNKNOWN'
Assert-Equal 0 $script:Toasts.Count 'T-10-1049 保有なしは通知しない'

$code = Invoke-Case $tradingAt { $script:Stub.Logs['market-monitor-service'] = @{ Exit = 1; Out = @() } }
Assert-Equal 1 $code 'T-10-1049 ログの読み取りの失敗は exit 1'
Assert-Equal 'UNREADABLE' (Read-StateStatus $script:CaseDir) 'T-10-1049 ログの読み取りの失敗は UNREADABLE'
Assert-Equal 1 $script:Toasts.Count 'T-10-1049 読み取りの失敗は通知する'

$code = Invoke-Case $tradingAt { $script:Stub.Pods['order-execution-service'] = @{ Exit = 1; Out = @() } }
Assert-Equal 'UNREADABLE' (Read-StateStatus $script:CaseDir) 'T-10-1049 Pod の照会の失敗は UNREADABLE（SERVICE_NOT_READY ではない。監査 N2）'

$code = Invoke-Case $tradingAt { $script:Stub.Pods['order-execution-service'] = @{ Exit = 0; Out = @(PodJson $false) } }
Assert-Equal 'SERVICE_NOT_READY' (Read-StateStatus $script:CaseDir) 'T-10-1049 Ready でない Pod は SERVICE_NOT_READY'

$code = Invoke-Case $tradingAt { $script:Stub.Readyz = @{ Exit = 1; Out = @('connection refused') } }
Assert-Equal 1 $code 'T-10-1049 API 不達は exit 1'
Assert-Equal 'CLUSTER_UNREACHABLE' (Read-StateStatus $script:CaseDir) 'T-10-1049 API 不達は CLUSTER_UNREACHABLE'
Assert-Equal 1 $script:Stub.Calls.Count 'T-10-1049 API 不達なら Pod・ログを読みに行かない'

$code = Invoke-Case $tradingAt {
  $script:Stub.Logs['market-monitor-service'] = @{ Exit = 0; Out = @('2026-09-25T14:58:00Z [14:58:00 INF] 損切り評価は稼働中: 保有 1 件を評価（要約は 00:05:00 に 1 回）: x') }
  $script:Stub.Logs['order-execution-service'] = @{ Exit = 0; Out = @('2026-09-25T14:57:00Z [14:57:00 INF] ソフトウェア逆指値（S1）の保護は継続中: Active 1 件（要約は 00:05:00 に 1 回。価格との比較は市場監視が行う）: x') }
}
Assert-Equal 0 $code 'T-10-1049 両方の要約が新しければ exit 0'
Assert-Equal 'PROTECTED' (Read-StateStatus $script:CaseDir) 'T-10-1049 両方の要約が新しければ PROTECTED'

$code = Invoke-Case $tradingAt {
  $script:Stub.Logs['order-execution-service'] = @{ Exit = 0; Out = @('2026-09-25T14:57:00Z [14:57:00 INF] ソフトウェア逆指値（S1）の保護は継続中: Active 1 件（…）: x') }
}
Assert-Equal 'EVALUATION_SILENT' (Read-StateStatus $script:CaseDir) 'T-10-1049 S1 行が Active で評価のログが空なら EVALUATION_SILENT'

# 読み取り以外の kubectl を呼ばない（全ケースの呼び出しを通して）
$allCalls = [System.Collections.Generic.List[string]]::new()
foreach ($arrange in @($null, { $script:Stub.Logs['market-monitor-service'] = @{ Exit = 1; Out = @() } })) {
  $null = Invoke-Case $tradingAt $arrange
  $allCalls.AddRange($script:Stub.Calls)
}
$writes = @($allCalls | Where-Object { $_ -match '(^|\s)(delete|exec|apply|patch|scale|rollout|edit|annotate|label|cordon|drain|replace|create|set)(\s|$)' })
Assert-Equal 0 $writes.Count 'T-10-1049 kubectl は get / logs だけを呼ぶ（読み取り専用）'
Assert-True (@($allCalls | Where-Object { $_ -match 'opend' }).Count -eq 0) 'T-10-1049 opend を読まない'

# 通常取引時間外は kubectl を呼ばず、状態ファイルも書かない
$code = Invoke-Case $saturdayAt $null
Assert-Equal 0 $code 'T-10-1049 時間外は exit 0'
Assert-Equal 0 $script:Stub.Calls.Count 'T-10-1049 時間外は kubectl を呼ばない'
Assert-Equal $false (Test-Path (Join-Path $script:CaseDir 'state.json')) 'T-10-1049 時間外は状態ファイルを書かない'

# 監査 N1: 読めない臨時休場日は黙って捨てず SCRIPT_ERROR（時間外でも）
$ExtraHolidays = @('2026-9-1x')
$code = Invoke-Case $saturdayAt $null
Assert-Equal 2 $code 'T-10-1049 読めない臨時休場日は exit 2'
Assert-Equal 'SCRIPT_ERROR' (Read-StateStatus $script:CaseDir) 'T-10-1049 読めない臨時休場日は SCRIPT_ERROR'
Assert-Equal 0 $script:Stub.Calls.Count 'T-10-1049 設定の誤りでは kubectl を呼ばない'
$ExtraHolidays = @('2026-09-25')
$code = Invoke-Case $tradingAt $null
Assert-Equal 0 $script:Stub.Calls.Count 'T-10-1049 正しい臨時休場日は足され、その日は時間外として何もしない'
$ExtraHolidays = @(); $ExtraHalfDays = @('2026/09/25')
$code = Invoke-Case $tradingAt $null
Assert-Equal 2 $code 'T-10-1049 読めない臨時半日も exit 2'
$ExtraHalfDays = @()
$emptyDates = ConvertTo-DateList @('', '  ')
Assert-Equal 0 $emptyDates.Count 'T-10-1049 空・空白だけの指定は無視する（空の配列が返る）'

# 監査 N3: 前日の警報の状態を持ったまま今日の最初の実行が平常なら「回復」を通知しない
$prevDir = New-StateDir
Write-MonitorState (Join-Path $prevDir 'state.json') ([pscustomobject]@{ Status = 'CLUSTER_UNREACHABLE'; Severity = 'alert'; LastAlertAt = $tradingAt.AddDays(-1); HoldingSeenAt = $null; CheckedAt = $tradingAt.AddDays(-1) })
Reset-Stub
$StateDirectory = $prevDir
$code = Invoke-HostLivenessCheck -At $tradingAt
Assert-Equal 0 $script:Toasts.Count 'T-10-1049 前日の警報の状態から今日の最初が平常でも「回復」を通知しない（監査 N3）'
Write-MonitorState (Join-Path $prevDir 'state.json') ([pscustomobject]@{ Status = 'CLUSTER_UNREACHABLE'; Severity = 'alert'; LastAlertAt = $tradingAt.AddMinutes(-10); HoldingSeenAt = $null; CheckedAt = $tradingAt.AddMinutes(-10) })
Reset-Stub
$code = Invoke-HostLivenessCheck -At $tradingAt
Assert-Equal 1 $script:Toasts.Count 'T-10-1049 同じ取引時間の警報から平常へ戻れば「回復」を 1 回通知する'
Assert-True ($script:Toasts[0] -match '回復') 'T-10-1049 回復の通知の題名'

# 試走は状態ファイルを書かない
$DryRun = $true
$code = Invoke-Case $tradingAt $null
Assert-Equal $false (Test-Path (Join-Path $script:CaseDir 'state.json')) 'T-10-1049 試走（-DryRun）は状態ファイルを書かない'
Assert-Equal 0 $script:Toasts.Count 'T-10-1049 試走（-DryRun）は通知しない'
$DryRun = $false
foreach ($d in $script:TempDirs) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }

# ---- 結果 -----------------------------------------------------------------------------------------------------------
$total = $script:passed + $script:failed.Count
$script:failed | ForEach-Object { Write-Host $_ }
Write-Host "host-liveness-monitor.test.ps1: 総数 $total / 成功 $($script:passed) / 失敗 $($script:failed.Count)"
if ($script:failed.Count -gt 0) { exit 1 }
exit 0
