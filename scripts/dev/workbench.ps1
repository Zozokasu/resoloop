<#
.SYNOPSIS
    Start, connect, inspect, and stop the Resonite Workbench App for local
    ResoLoop development.

.DESCRIPTION
    workbench.ps1 manages the Workbench App (GUI) produced by the separate
    resonite-workbench repository and drives its named-pipe RPC endpoint via
    the rwb CLI (dotnet run --no-build --project src/ResoniteWorkbench.Cli).

    Subcommands:
      start    Launch ResoniteWorkbench.App minimized, record it in the state
               file, and wait until the RPC pipe answers.
      connect  Attach the Workbench to the single discovered ResoniteLink
               session. Refuses to guess when zero or multiple candidates
               exist, and refuses to change an existing connection.
      status   Print the recorded process state, pipe reachability, and the
               session.status result.
      stop     Stop ONLY the process this script started (verified by PID +
               process name + start time). Never touches other processes.
      help     Print usage. Launches nothing.

    Safety:
      - session.disconnect is never invoked.
      - Processes are never killed by name; only the exact recorded process
        is eligible for stop.
      - The state file lives under %LOCALAPPDATA%, never in the repository.
      - This script never runs dotnet build.

    Requires Windows PowerShell 5.1. ASCII only on purpose: comments and
    messages stay English so nothing mojibakes under 5.1.

.PARAMETER Command
    start | connect | status | stop | help (default: help).

.PARAMETER WorkbenchRepo
    Path of the resonite-workbench checkout. Used as the working directory
    for rwb and as the search root for ResoniteWorkbench.App.exe.

.PARAMETER AppExe
    Explicit path to ResoniteWorkbench.App.exe; skips the bin search.

.PARAMETER PipeName
    RPC pipe name (default: ResoniteWorkbench.Rpc.v1).

.PARAMETER StartTimeoutSec
    Seconds to wait for the pipe after launching the App (default: 60).

.PARAMETER ConnectTimeoutSec
    Seconds to poll for state Connected after session.connect (default: 10).

.PARAMETER StateFile
    Override path of the JSON state file. Default:
    %LOCALAPPDATA%\resoloop-dev\workbench-app.json

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev/workbench.ps1 start

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev/workbench.ps1 connect
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'connect', 'status', 'stop', 'help')]
    [string]$Command = 'help',
    [string]$WorkbenchRepo = 'C:\Users\jojoh\Documents\resonite-workbench',
    [string]$AppExe = '',
    [string]$PipeName = 'ResoniteWorkbench.Rpc.v1',
    [int]$StartTimeoutSec = 60,
    [int]$ConnectTimeoutSec = 10,
    [string]$StateFile = ''
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Small utilities
# ---------------------------------------------------------------------------

function Quote-Argv {
    param([AllowNull()][string]$Arg)
    if ($null -eq $Arg) { return '""' }
    if ($Arg.Length -gt 0 -and $Arg -notmatch '[\s"]') { return $Arg }
    $a = $Arg -replace '(\\*)"', '$1$1\"'
    $a = $a -replace '(\\+)$', '$1$1'
    '"' + $a + '"'
}

function Get-Prop {
    param($Obj, [string[]]$Names)
    if ($null -eq $Obj) { return $null }
    foreach ($n in $Names) {
        $p = $Obj.PSObject.Properties[$n]
        if ($null -ne $p -and $null -ne $p.Value) { return $p.Value }
    }
    return $null
}

function Test-PropExists {
    param($Obj, [string]$PropName)
    if ($null -eq $Obj) { return $false }
    return ($null -ne $Obj.PSObject.Properties[$PropName])
}

function Test-PipeReachable {
    param([Parameter(Mandatory = $true)][string]$Name, [int]$TimeoutMs = 500)
    $client = $null
    try {
        $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Name, [System.IO.Pipes.PipeDirection]::InOut)
        $client.Connect($TimeoutMs)
        return $true
    } catch { return $false } finally { if ($null -ne $client) { $client.Dispose() } }
}

function Test-EndpointMatch {
    param([AllowNull()][string]$A, [AllowNull()][string]$B)
    if ([string]::IsNullOrWhiteSpace($A) -or [string]::IsNullOrWhiteSpace($B)) { return $false }
    return [string]::Equals($A.Trim().TrimEnd('/'), $B.Trim().TrimEnd('/'), [System.StringComparison]::OrdinalIgnoreCase)
}

# ---------------------------------------------------------------------------
# State file (records only the process started by this script)
# ---------------------------------------------------------------------------

function Get-StatePath {
    if (-not [string]::IsNullOrWhiteSpace($StateFile)) { return $StateFile }
    return (Join-Path $env:LOCALAPPDATA 'resoloop-dev\workbench-app.json')
}

function Read-State {
    # Returns $null when no state file exists; throws on corrupt JSON.
    $path = Get-StatePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $raw = [System.IO.File]::ReadAllText($path)
    $obj = $null
    try {
        $obj = $raw | ConvertFrom-Json
    } catch {
        throw ("corrupt state file: " + $path)
    }
    if ($null -eq $obj -or $obj -isnot [pscustomobject]) {
        throw ("corrupt state file: " + $path)
    }
    return $obj
}

function Write-State {
    param([Parameter(Mandatory = $true)]$State)
    $path = Get-StatePath
    $dir = Split-Path -Parent $path
    if (-not [string]::IsNullOrEmpty($dir) -and -not (Test-Path -LiteralPath $dir)) {
        [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    }
    $json = $State | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Remove-State {
    $path = Get-StatePath
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Remove-Item -LiteralPath $path -Force
    }
}

function Test-RecordedProcess {
    # Checks whether the process recorded in $State is still the same process.
    # Returns @{ Found; Match; Process; Reason }:
    #   Found=$false -> no process with the recorded PID exists
    #   Match=$false -> a process exists but is a different one (PID reuse)
    param([Parameter(Mandatory = $true)]$State)
    $pidValue = Get-Prop $State @('pid')
    if ($null -eq $pidValue) {
        return @{ Found = $false; Match = $false; Process = $null; Reason = 'record has no pid' }
    }
    $proc = $null
    try {
        $proc = Get-Process -Id ([int]$pidValue) -ErrorAction Stop
    } catch {
        $proc = $null
    }
    if ($null -eq $proc) {
        return @{ Found = $false; Match = $false; Process = $null; Reason = 'process not running' }
    }
    $actualName = $null
    $actualStart = $null
    try {
        $actualName = $proc.ProcessName
        $actualStart = $proc.StartTime.ToUniversalTime()
    } catch {
        return @{ Found = $false; Match = $false; Process = $null; Reason = 'process exited' }
    }
    $recordedName = Get-Prop $State @('processName')
    if (-not [string]::IsNullOrEmpty($recordedName) -and $actualName -ne $recordedName) {
        return @{ Found = $true; Match = $false; Process = $proc; Reason = ("process name mismatch (recorded=" + $recordedName + " actual=" + $actualName + ")") }
    }
    $recordedStart = Get-Prop $State @('startTimeUtc')
    if (-not [string]::IsNullOrEmpty($recordedStart)) {
        $parsed = $null
        try {
            $parsed = [DateTime]::Parse($recordedStart, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
        } catch {
            return @{ Found = $true; Match = $false; Process = $proc; Reason = 'unparseable recorded startTimeUtc' }
        }
        $diff = [Math]::Abs(($actualStart - $parsed.ToUniversalTime()).TotalSeconds)
        if ($diff -gt 2) {
            return @{ Found = $true; Match = $false; Process = $proc; Reason = ("start time mismatch (" + $diff + "s > 2s)") }
        }
    }
    return @{ Found = $true; Match = $true; Process = $proc; Reason = '' }
}

# ---------------------------------------------------------------------------
# rwb (Workbench CLI) invocation
# ---------------------------------------------------------------------------

function Invoke-Rwb {
    # Runs: dotnet run --no-build --project src/ResoniteWorkbench.Cli --
    #         --pipe <PipeName> [--grant <cap>]... <method> @<tmpfile>
    # Returns an ordered hashtable: ExitCode / TimedOut / StdOut / StdErr.
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [AllowNull()]$Params,
        [string[]]$Grants = @()
    )
    if (-not (Test-Path -LiteralPath $WorkbenchRepo -PathType Container)) {
        throw ("rwb " + $Method + " failed: WorkbenchRepo not found: " + $WorkbenchRepo)
    }
    $tmpFile = $null
    try {
        $argList = @('run', '--no-build', '--project', 'src/ResoniteWorkbench.Cli', '--', '--pipe', $PipeName)
        foreach ($g in $Grants) {
            $argList += '--grant'
            $argList += $g
        }
        $argList += $Method

        $paramsObj = $Params
        if ($null -eq $paramsObj) { $paramsObj = @{} }
        $paramsJson = $paramsObj | ConvertTo-Json -Depth 12 -Compress
        if ([string]::IsNullOrEmpty($paramsJson)) { $paramsJson = '{}' }
        $tmpFile = Join-Path ([System.IO.Path]::GetTempPath()) ('rwb-' + [Guid]::NewGuid().ToString('N') + '.json')
        [System.IO.File]::WriteAllText($tmpFile, $paramsJson, (New-Object System.Text.UTF8Encoding($false)))
        $argList += ('@' + $tmpFile)

        $argString = ''
        foreach ($a in $argList) {
            $q = Quote-Argv $a
            if ($argString.Length -gt 0) { $argString += ' ' }
            $argString += $q
        }

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'dotnet'
        $psi.Arguments = $argString
        $psi.WorkingDirectory = $WorkbenchRepo
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true

        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi
        $null = $proc.Start()
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()
        $timedOut = $false
        $exited = $proc.WaitForExit(30000)
        if (-not $exited) {
            $timedOut = $true
            try { $proc.Kill() } catch { }
            $proc.WaitForExit()
        }
        $stdout = ''
        $stderr = ''
        try { $stdout = $stdoutTask.Result } catch { $stdout = '' }
        try { $stderr = $stderrTask.Result } catch { $stderr = '' }
        if ($null -eq $stdout) { $stdout = '' }
        if ($null -eq $stderr) { $stderr = '' }
        $exitCode = -1
        if ($exited) { $exitCode = $proc.ExitCode }
        $proc.Dispose()
        return [ordered]@{
            ExitCode = $exitCode
            TimedOut = $timedOut
            StdOut   = $stdout
            StdErr   = $stderr
        }
    } finally {
        if ($null -ne $tmpFile -and (Test-Path -LiteralPath $tmpFile)) {
            Remove-Item -LiteralPath $tmpFile -Force -ErrorAction SilentlyContinue
        }
    }
}

function Read-RwbJson {
    # rwb stdout is JSON {"result": ...} but warning lines may surround it.
    param([AllowNull()][string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    try {
        return ($Text | ConvertFrom-Json)
    } catch {
        $lines = $Text -split "`r?`n"
        for ($i = $lines.Count - 1; $i -ge 0; $i--) {
            $line = $lines[$i].Trim()
            if ($line.StartsWith('{')) {
                try {
                    return ($line | ConvertFrom-Json)
                } catch {
                    return $null
                }
            }
        }
        return $null
    }
}

function Get-StderrTail {
    param([AllowNull()][string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $lines = @($Text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -le 10) { return ($lines -join ' | ') }
    return ($lines[($lines.Count - 10)..($lines.Count - 1)] -join ' | ')
}

function Invoke-RwbChecked {
    # Invoke-Rwb plus success contract: exit 0, not timed out, JSON with a
    # 'result' property. Throws a descriptive error otherwise.
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [AllowNull()]$Params,
        [string[]]$Grants = @()
    )
    $hint = '(rwb build missing? run dotnet build in the workbench repo)'
    $r = Invoke-Rwb -Method $Method -Params $Params -Grants $Grants
    if ($r.TimedOut) {
        throw ("rwb " + $Method + " timed out after 30s " + $hint)
    }
    if ($r.ExitCode -ne 0) {
        throw ("rwb " + $Method + " exited with code " + $r.ExitCode + ": " + (Get-StderrTail $r.StdErr) + " " + $hint)
    }
    $json = Read-RwbJson $r.StdOut
    if ($null -eq $json) {
        throw ("rwb " + $Method + " returned unparseable output: " + (Get-StderrTail $r.StdErr) + " " + $hint)
    }
    if (-not (Test-PropExists $json 'result')) {
        throw ("rwb " + $Method + " response has no 'result' property: " + (Get-StderrTail $r.StdErr) + " " + $hint)
    }
    return $json.PSObject.Properties['result'].Value
}

function Get-CandidateList {
    param($Candidates)
    $items = @()
    foreach ($c in $Candidates) {
        $items += ("{sessionId=" + (Get-Prop $c @('sessionId')) + ", endpoint=" + (Get-Prop $c @('endpoint')) + "}")
    }
    return ($items -join '; ')
}

# ---------------------------------------------------------------------------
# Subcommands
# ---------------------------------------------------------------------------

function Invoke-Start {
    try {
        # 1. already started by this script?
        $state = Read-State
        if ($null -ne $state) {
            $check = Test-RecordedProcess $state
            if ($check.Found -and $check.Match) {
                Write-Host ("already started by this script (PID " + (Get-Prop $state @('pid')) + ")")
                return 0
            }
            Remove-State
        }

        # 2. a Workbench App not started by this script may already serve the pipe
        if (Test-PipeReachable -Name $PipeName -TimeoutMs 750) {
            Write-Host 'Workbench RPC pipe already answers (not started by this script; stop will not touch it)'
            return 0
        }

        # 3. resolve the App exe
        $exe = $AppExe
        if (-not [string]::IsNullOrWhiteSpace($exe)) {
            if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
                Write-Host ("FAIL: APP_EXE_NOT_FOUND specified AppExe does not exist: " + $exe)
                return 1
            }
        } else {
            $binRoot = Join-Path $WorkbenchRepo 'src\ResoniteWorkbench.App\bin'
            $found = @()
            if (Test-Path -LiteralPath $binRoot -PathType Container) {
                $found = @(Get-ChildItem -LiteralPath $binRoot -Recurse -Filter 'ResoniteWorkbench.App.exe' -File)
            }
            if ($found.Count -eq 0) {
                Write-Host ("FAIL: APP_EXE_NOT_FOUND no ResoniteWorkbench.App.exe under " + $binRoot + "; build the App first")
                return 1
            }
            $exe = ($found | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).FullName
        }

        if (-not (Test-Path -LiteralPath $WorkbenchRepo -PathType Container)) {
            Write-Host ("FAIL: WorkbenchRepo not found: " + $WorkbenchRepo)
            return 1
        }

        # 4. launch minimized and record BEFORE waiting on the pipe
        $proc = Start-Process -FilePath $exe -WorkingDirectory $WorkbenchRepo -WindowStyle Minimized -PassThru
        try {
            $record = [ordered]@{
                pid           = $proc.Id
                processName   = $proc.ProcessName
                startTimeUtc  = $proc.StartTime.ToUniversalTime().ToString('o')
                exePath       = $exe
                launchedAtUtc = [DateTime]::UtcNow.ToString('o')
                pipeName      = $PipeName
            }
            Write-State $record

            # 5. wait for the pipe
            $deadline = [DateTime]::UtcNow.AddSeconds($StartTimeoutSec)
            while ([DateTime]::UtcNow -lt $deadline) {
                $proc.Refresh()
                if ($proc.HasExited) {
                    Remove-State
                    Write-Host ("FAIL: process exited before pipe came up (PID " + $proc.Id + ")")
                    return 1
                }
                if (Test-PipeReachable -Name $PipeName -TimeoutMs 250) {
                    Write-Host ("started PID " + $proc.Id + ", pipe ready (window minimized)")
                    return 0
                }
                Start-Sleep -Milliseconds 250
            }
            try { $proc.Kill() } catch { }
            Remove-State
            Write-Host ("FAIL: pipe did not answer within " + $StartTimeoutSec + "s; killed PID " + $proc.Id)
            return 1
        } catch {
            # never leak a process this script launched but failed to record/observe
            try { if ($null -ne $proc -and -not $proc.HasExited) { $proc.Kill() } } catch { }
            throw
        }
    } catch {
        Write-Host ("FAIL: " + $_.Exception.Message)
        return 1
    }
}

function Invoke-Status {
    try {
        $path = Get-StatePath
        $state = Read-State
        if ($null -eq $state) {
            Write-Host ("state file: none (" + $path + ")")
        } else {
            Write-Host ("state file: " + $path)
            Write-Host ("  recorded: pid=" + (Get-Prop $state @('pid')) + " processName=" + (Get-Prop $state @('processName')) + " startTimeUtc=" + (Get-Prop $state @('startTimeUtc')))
            $check = Test-RecordedProcess $state
            if ($check.Found -and $check.Match) {
                Write-Host '  recorded process: alive (matches record)'
            } elseif ($check.Found) {
                Write-Host ("  recorded process: RUNNING but does not match record (" + $check.Reason + ")")
            } else {
                Write-Host '  recorded process: not running'
            }
        }
        if (-not (Test-PipeReachable -Name $PipeName -TimeoutMs 750)) {
            Write-Host 'pipe: not answering'
            return 1
        }
        Write-Host 'pipe: answering'
        $result = Invoke-RwbChecked -Method 'session.status' -Params @{}
        $st = Get-Prop $result @('state')
        $target = Get-Prop $result @('targetSession')
        $endpoint = Get-Prop $target @('endpoint')
        $displayName = Get-Prop $target @('displayName')
        $reason = Get-Prop $result @('disconnectReason')
        Write-Host ("state: " + $st)
        Write-Host ("targetSession.endpoint: " + $(if ($null -eq $endpoint) { '(null)' } else { $endpoint }))
        Write-Host ("targetSession.displayName: " + $(if ($null -eq $displayName) { '(null)' } else { $displayName }))
        Write-Host ("disconnectReason: " + $(if ($null -eq $reason) { '(null)' } else { $reason }))
        return 0
    } catch {
        Write-Host ("FAIL: " + $_.Exception.Message)
        return 1
    }
}

function Invoke-Connect {
    try {
        if (-not (Test-PipeReachable -Name $PipeName -TimeoutMs 750)) {
            Write-Host 'FAIL: pipe not answering; run `start` first'
            return 1
        }
        $statusResult = Invoke-RwbChecked -Method 'session.status' -Params @{}
        $discoverResult = Invoke-RwbChecked -Method 'session.discover' -Params @{}
        $cands = @()
        if ($null -ne $discoverResult) {
            $cands = @($discoverResult | Where-Object { $null -ne $_ })
        }
        $state = [string](Get-Prop $statusResult @('state'))
        $target = Get-Prop $statusResult @('targetSession')
        $currentEndpoint = [string](Get-Prop $target @('endpoint'))

        if ($state -eq 'Connected' -or $state -eq 'Connecting' -or $state -eq 'Reconnecting') {
            if ($cands.Count -eq 1) {
                $candEndpoint = Get-Prop $cands[0] @('endpoint')
                if (Test-EndpointMatch $currentEndpoint $candEndpoint) {
                    Write-Host ("already connected to " + $currentEndpoint)
                    return 0
                }
            }
            Write-Host ("FAIL: already connected to " + $(if ([string]::IsNullOrWhiteSpace($currentEndpoint)) { '(unknown endpoint)' } else { $currentEndpoint }) + "; refusing to change the connection (candidates: " + (Get-CandidateList $cands) + ")")
            return 1
        }

        if ($cands.Count -eq 0) {
            Write-Host 'FAIL: no ResoniteLink session discovered'
            return 1
        }
        if ($cands.Count -gt 1) {
            Write-Host ("FAIL: " + $cands.Count + " candidates; refusing to guess")
            foreach ($c in $cands) {
                Write-Host ("  sessionId=" + (Get-Prop $c @('sessionId')) + " displayName=" + (Get-Prop $c @('displayName')) + " endpoint=" + (Get-Prop $c @('endpoint')))
            }
            return 1
        }

        $sessionId = [string](Get-Prop $cands[0] @('sessionId'))
        if ([string]::IsNullOrWhiteSpace($sessionId)) {
            Write-Host 'FAIL: the single discovered candidate has no sessionId; refusing to connect'
            return 1
        }

        $connectResult = Invoke-RwbChecked -Method 'session.connect' -Params ([ordered]@{ sessionId = $sessionId }) -Grants @('session.control')
        $st2 = [string](Get-Prop $connectResult @('state'))
        if ($st2 -eq 'Connected') {
            $ep = Get-Prop (Get-Prop $connectResult @('targetSession')) @('endpoint')
            Write-Host ("connected to " + $(if ($null -eq $ep) { '(endpoint unknown)' } else { $ep }))
            return 0
        }
        $st3 = $st2
        $deadline = [DateTime]::UtcNow.AddSeconds($ConnectTimeoutSec)
        while ([DateTime]::UtcNow -lt $deadline -and $st3 -ne 'Connected') {
            Start-Sleep -Milliseconds 500
            $poll = Invoke-RwbChecked -Method 'session.status' -Params @{}
            $st3 = [string](Get-Prop $poll @('state'))
            if ($st3 -eq 'Connected') {
                $ep2 = Get-Prop (Get-Prop $poll @('targetSession')) @('endpoint')
                Write-Host ("connected to " + $(if ($null -eq $ep2) { '(endpoint unknown)' } else { $ep2 }))
                return 0
            }
        }
        Write-Host ("FAIL: session did not reach Connected within " + $ConnectTimeoutSec + "s (state=" + $st3 + ")")
        return 1
    } catch {
        Write-Host ("FAIL: " + $_.Exception.Message)
        return 1
    }
}

function Invoke-Stop {
    try {
        $state = Read-State
        if ($null -eq $state) {
            Write-Host 'nothing to stop (no process recorded by this script)'
            return 0
        }
        $pidText = (Get-Prop $state @('pid'))
        $check = Test-RecordedProcess $state
        if (-not $check.Found) {
            Remove-State
            Write-Host 'already exited'
            return 0
        }
        if (-not $check.Match) {
            Remove-State
            Write-Host ("FAIL: PID " + $pidText + " is now a different process; not killing (" + $check.Reason + ")")
            return 1
        }
        $proc = $check.Process
        $proc.Kill()
        if ($proc.WaitForExit(5000)) {
            Remove-State
            Write-Host ("stopped PID " + $pidText)
            return 0
        }
        Write-Host ("FAIL: PID " + $pidText + " did not exit within 5s of Kill; record kept")
        return 1
    } catch {
        Write-Host ("FAIL: " + $_.Exception.Message)
        return 1
    }
}

# ---------------------------------------------------------------------------
# Help + dispatch
# ---------------------------------------------------------------------------

function Show-Help {
    Write-Host @'
Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev/workbench.ps1 <command> [options]

Commands:
  start     Launch ResoniteWorkbench.App (minimized) and wait until its RPC pipe answers.
  connect   Connect the running Workbench App to the single discovered ResoniteLink session.
            Does nothing when already connected to that session; refuses to change
            an existing connection or to guess between multiple/no candidates.
  status    Show state file info, recorded process liveness, pipe reachability,
            and session.status (state / endpoint / displayName / disconnectReason).
  stop      Stop ONLY the App process started by this script
            (verified by PID + process name + start time). Never kills by name.
  help      Show this text. Launches nothing.

Options:
  -WorkbenchRepo <path>    resonite-workbench checkout (rwb cwd + App exe search root)
                           default: C:\Users\jojoh\Documents\resonite-workbench
  -AppExe <path>           explicit ResoniteWorkbench.App.exe path (skips bin search)
  -PipeName <name>         RPC pipe name (default: ResoniteWorkbench.Rpc.v1)
  -StartTimeoutSec <int>   seconds to wait for the pipe on start (default: 60)
  -ConnectTimeoutSec <int> seconds to poll for Connected after connect (default: 10)
  -StateFile <path>        override state file
                           (default: %LOCALAPPDATA%\resoloop-dev\workbench-app.json)

Exit codes: 0 = success, 1 = failure (reason printed with a FAIL: prefix).
'@
}

if ($Command -eq 'help') {
    Show-Help
    exit 0
}

try {
    $code = 1
    switch ($Command) {
        'start'   { $code = Invoke-Start }
        'status'  { $code = Invoke-Status }
        'connect' { $code = Invoke-Connect }
        'stop'    { $code = Invoke-Stop }
        default   { Show-Help; exit 0 }
    }
    if ($code -is [array]) { $code = $code[-1] }
    if ($null -eq $code -or $code -isnot [int]) { $code = 1 }
    exit $code
} catch {
    Write-Host ("FAIL: unexpected: " + $_.Exception.Message)
    exit 1
}
