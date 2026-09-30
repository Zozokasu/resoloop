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
               session. Retries discovery while no candidate is found (see
               -DiscoverTimeoutSec), refuses to guess between multiple
               candidates, and refuses to change an existing connection.
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
      - Do not run start/stop concurrently (no locking is performed).

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

.PARAMETER DiscoverTimeoutSec
    Seconds connect retries session.discover while zero candidates are
    found (default: 45). 0 disables the retry: connect fails immediately
    as before.

.PARAMETER RequireFreshBuild
    When set, start fails instead of launching if the App exe or
    ResoniteWorkbench.Core.dll is older than the WorkbenchRepo HEAD
    commit. Without it a stale build only prints a WARN line.

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
    [int]$DiscoverTimeoutSec = 45,
    [switch]$RequireFreshBuild,
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
    if ($Obj -is [System.Collections.IDictionary]) {
        foreach ($n in $Names) {
            if ($Obj.Contains($n)) { return $Obj[$n] }
        }
        return $null
    }
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
    #   Found=$false               -> record is complete but the PID is not a live process
    #   Found=$true, Match=$false  -> record incomplete/invalid, or the live process
    #                                 is a different one (PID reuse); Reason explains why
    #   Found=$true, Match=$true   -> verified same process
    # All three record fields (pid, processName, startTimeUtc) are REQUIRED.
    # A missing/blank/invalid field is never treated as a match; it yields
    # Found=$true, Match=$false so callers refuse instead of cleaning up.
    param([Parameter(Mandatory = $true)]$State)
    $pidValue = Get-Prop $State @('pid')
    if ($null -eq $pidValue -or [string]::IsNullOrWhiteSpace([string]$pidValue)) {
        return @{ Found = $true; Match = $false; Process = $null; Reason = 'incomplete record: missing pid' }
    }
    $pidInt = 0
    if (-not [int]::TryParse(([string]$pidValue).Trim(), [ref]$pidInt)) {
        return @{ Found = $true; Match = $false; Process = $null; Reason = 'incomplete record: invalid pid' }
    }
    $recordedName = Get-Prop $State @('processName')
    if ([string]::IsNullOrWhiteSpace([string]$recordedName)) {
        return @{ Found = $true; Match = $false; Process = $null; Reason = 'incomplete record: missing processName' }
    }
    $recordedStart = Get-Prop $State @('startTimeUtc')
    if ([string]::IsNullOrWhiteSpace([string]$recordedStart)) {
        return @{ Found = $true; Match = $false; Process = $null; Reason = 'incomplete record: missing startTimeUtc' }
    }
    $parsed = $null
    try {
        $parsed = [DateTime]::Parse(([string]$recordedStart), [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    } catch {
        return @{ Found = $true; Match = $false; Process = $null; Reason = 'unparseable recorded startTimeUtc' }
    }
    $proc = $null
    try {
        $proc = Get-Process -Id $pidInt -ErrorAction Stop
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
    if ($actualName -ne $recordedName) {
        return @{ Found = $true; Match = $false; Process = $proc; Reason = ("process name mismatch (recorded=" + $recordedName + " actual=" + $actualName + ")") }
    }
    $diff = [Math]::Abs(($actualStart - $parsed.ToUniversalTime()).TotalSeconds)
    if ($diff -gt 2) {
        return @{ Found = $true; Match = $false; Process = $proc; Reason = ("start time mismatch (" + $diff + "s > 2s)") }
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
        try {
            $null = $proc.Start()
            $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
            $stderrTask = $proc.StandardError.ReadToEndAsync()
            $timedOut = $false
            $exited = $proc.WaitForExit(30000)
            if (-not $exited) {
                $timedOut = $true
                try {
                    $proc.Kill()
                } catch {
                    throw ("rwb " + $Method + " exceeded the 30s timeout and Kill() failed: " + $_.Exception.Message)
                }
                if (-not $proc.WaitForExit(5000)) {
                    throw ("rwb " + $Method + " did not exit within 5s after Kill")
                }
            }
            $stdout = ''
            $stderr = ''
            try { $stdout = $stdoutTask.Result } catch { $stdout = '' }
            try { $stderr = $stderrTask.Result } catch { $stderr = '' }
            if ($null -eq $stdout) { $stdout = '' }
            if ($null -eq $stderr) { $stderr = '' }
            $exitCode = -1
            if ($exited) { $exitCode = $proc.ExitCode }
            return [ordered]@{
                ExitCode = $exitCode
                TimedOut = $timedOut
                StdOut   = $stdout
                StdErr   = $stderr
            }
        } finally {
            $proc.Dispose()
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

function Wait-SessionConnected {
    # Polls session.status every 500ms until state Connected or TimeoutSec
    # elapses. Returns @{ TimedOut; State; SessionId; Endpoint; Match }.
    # Match=$true only when the Connected status carries ExpectedSessionId;
    # a missing sessionId counts as not matching.
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedSessionId,
        [int]$TimeoutSec = 10
    )
    $st = ''
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $poll = Invoke-RwbChecked -Method 'session.status' -Params @{}
        $st = [string](Get-Prop $poll @('state'))
        if ($st -eq 'Connected') {
            $t = Get-Prop $poll @('targetSession')
            $sid = [string](Get-Prop $t @('sessionId'))
            return @{
                TimedOut  = $false
                State     = $st
                SessionId = $sid
                Endpoint  = Get-Prop $t @('endpoint')
                Match     = (-not [string]::IsNullOrWhiteSpace($sid) -and $sid -eq $ExpectedSessionId)
            }
        }
    }
    return @{ TimedOut = $true; State = $st; SessionId = ''; Endpoint = $null; Match = $false }
}

function Get-RepoHeadInfo {
    # Returns @{ CommitTimeUtc; ShortHash } for the HEAD of $RepoPath, or
    # $null when git is unavailable, the path is not a git repo, or the
    # commit time cannot be parsed. Never throws for those cases.
    param([Parameter(Mandatory = $true)][string]$RepoPath)
    $line = $null
    try {
        $line = & git -C $RepoPath log -1 --format=%cI HEAD 2>$null
        if ($LASTEXITCODE -ne 0) { $line = $null }
    } catch {
        $line = $null
    }
    if ([string]::IsNullOrWhiteSpace([string]$line)) { return $null }
    $parsed = $null
    try {
        $parsed = [DateTime]::Parse(([string]$line).Trim(), [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    } catch {
        $parsed = $null
    }
    if ($null -eq $parsed) { return $null }
    $shortHash = ''
    try {
        $h = & git -C $RepoPath rev-parse --short HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$h)) {
            $shortHash = ([string]$h).Trim()
        }
    } catch {
        $shortHash = ''
    }
    return @{ CommitTimeUtc = $parsed.ToUniversalTime(); ShortHash = $shortHash }
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
                $recordedPipe = [string](Get-Prop $state @('pipeName'))
                if ([string]::IsNullOrWhiteSpace($recordedPipe) -or
                    -not [string]::Equals($recordedPipe, $PipeName, [System.StringComparison]::OrdinalIgnoreCase)) {
                    Write-Host ("FAIL: recorded App (PID " + (Get-Prop $state @('pid')) + ") has pipeName '" + $(if ([string]::IsNullOrWhiteSpace($recordedPipe)) { '(none)' } else { $recordedPipe }) + "', expected '" + $PipeName + "'; not starting a second App")
                    return 1
                }
                $pipeReady = $false
                $pipeDeadline = [DateTime]::UtcNow.AddSeconds(5)
                while ([DateTime]::UtcNow -lt $pipeDeadline -and -not $pipeReady) {
                    if (Test-PipeReachable -Name $PipeName -TimeoutMs 500) { $pipeReady = $true }
                    else { Start-Sleep -Milliseconds 250 }
                }
                if (-not $pipeReady) {
                    Write-Host ("FAIL: recorded App (PID " + (Get-Prop $state @('pid')) + ") is alive but pipe '" + $PipeName + "' does not answer; not starting a second App")
                    return 1
                }
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

        # 4. warn when the App build predates the workbench repo HEAD
        $headInfo = Get-RepoHeadInfo -RepoPath $WorkbenchRepo
        if ($null -eq $headInfo) {
            Write-Host 'WARN: could not determine the workbench repo HEAD commit time; skipping the build freshness check'
        } else {
            $headStamp = $headInfo.CommitTimeUtc.ToString('o')
            $headLabel = $headInfo.ShortHash
            if ([string]::IsNullOrWhiteSpace($headLabel)) { $headLabel = '(unknown hash)' }
            $buildItems = @((Get-Item -LiteralPath $exe))
            $coreDll = Join-Path (Split-Path -Parent $exe) 'ResoniteWorkbench.Core.dll'
            if (Test-Path -LiteralPath $coreDll -PathType Leaf) {
                $buildItems += Get-Item -LiteralPath $coreDll
            }
            $stale = @()
            foreach ($f in $buildItems) {
                if ($f.LastWriteTimeUtc -lt $headInfo.CommitTimeUtc) {
                    $stale += $f
                    Write-Host ("WARN: " + $f.FullName + " (LastWriteTimeUtc " + $f.LastWriteTimeUtc.ToString('o') + ") is older than the workbench repo HEAD commit " + $headLabel + " (" + $headStamp + "); rebuild with dotnet build ResoniteWorkbench.slnx in the workbench repo before start (the running App may lack recent RPC changes)")
                }
            }
            if ($stale.Count -gt 0 -and $RequireFreshBuild) {
                Write-Host ("FAIL: App build is older than the workbench repo HEAD (commit " + $headLabel + " at " + $headStamp + "); not launching")
                return 1
            }
        }

        # 5. launch minimized and record BEFORE waiting on the pipe
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

            # 6. wait for the pipe
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
        $currentSessionId = [string](Get-Prop $target @('sessionId'))

        if ($state -eq 'Connected' -or $state -eq 'Connecting' -or $state -eq 'Reconnecting') {
            $sameTarget = $false
            $candSessionId = $null
            if ($cands.Count -eq 1) {
                $candSessionId = [string](Get-Prop $cands[0] @('sessionId'))
                $candEndpoint = Get-Prop $cands[0] @('endpoint')
                $sameTarget = (-not [string]::IsNullOrWhiteSpace($candSessionId)) -and
                              (-not [string]::IsNullOrWhiteSpace($currentSessionId)) -and
                              ($currentSessionId -eq $candSessionId) -and
                              (Test-EndpointMatch $currentEndpoint $candEndpoint)
            }
            if ($state -eq 'Connected' -and $sameTarget) {
                Write-Host ("already connected to " + $currentEndpoint)
                return 0
            }
            if (($state -eq 'Connecting' -or $state -eq 'Reconnecting') -and $sameTarget) {
                # a connect to the chosen target is already in progress; wait for it,
                # never issue a second session.connect
                Write-Host ("connect already in progress to " + $currentEndpoint + "; polling for Connected")
                $w = Wait-SessionConnected -ExpectedSessionId $candSessionId -TimeoutSec $ConnectTimeoutSec
                if ($w.TimedOut) {
                    Write-Host ("FAIL: session did not reach Connected within " + $ConnectTimeoutSec + "s (state=" + $w.State + ")")
                    return 1
                }
                if (-not $w.Match) {
                    Write-Host ("FAIL: session reached Connected but sessionId is " + $(if ([string]::IsNullOrWhiteSpace($w.SessionId)) { '(none)' } else { $w.SessionId }) + ", expected " + $candSessionId)
                    return 1
                }
                Write-Host ("connected to " + $(if ($null -eq $w.Endpoint) { '(endpoint unknown)' } else { $w.Endpoint }))
                return 0
            }
            Write-Host ("FAIL: already connected to " + $(if ([string]::IsNullOrWhiteSpace($currentEndpoint)) { '(unknown endpoint)' } else { $currentEndpoint }) + "; refusing to change the connection (candidates: " + (Get-CandidateList $cands) + ")")
            return 1
        }

        if ($cands.Count -eq 0 -and $DiscoverTimeoutSec -gt 0) {
            # UDP discovery can lag App start by tens of seconds; keep asking.
            # Exits on the first non-empty result, so >=2 candidates still
            # fails below without waiting for the full timeout.
            Write-Host ("no ResoniteLink session discovered yet; retrying for up to " + $DiscoverTimeoutSec + "s")
            $discoverDeadline = [DateTime]::UtcNow.AddSeconds($DiscoverTimeoutSec)
            while ([DateTime]::UtcNow -lt $discoverDeadline -and $cands.Count -eq 0) {
                Start-Sleep -Seconds 3
                $discoverResult = Invoke-RwbChecked -Method 'session.discover' -Params @{}
                $cands = @()
                if ($null -ne $discoverResult) {
                    $cands = @($discoverResult | Where-Object { $null -ne $_ })
                }
            }
            if ($cands.Count -eq 0) {
                Write-Host ("FAIL: no ResoniteLink session discovered after retrying for " + $DiscoverTimeoutSec + "s")
                return 1
            }
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
            $connTarget = Get-Prop $connectResult @('targetSession')
            $connSid = [string](Get-Prop $connTarget @('sessionId'))
            if (-not [string]::IsNullOrWhiteSpace($connSid) -and $connSid -eq $sessionId) {
                $ep = Get-Prop $connTarget @('endpoint')
                Write-Host ("connected to " + $(if ($null -eq $ep) { '(endpoint unknown)' } else { $ep }))
                return 0
            }
            Write-Host ("FAIL: session.connect reported Connected but sessionId is " + $(if ([string]::IsNullOrWhiteSpace($connSid)) { '(none)' } else { $connSid }) + ", expected " + $sessionId)
            return 1
        }
        $w = Wait-SessionConnected -ExpectedSessionId $sessionId -TimeoutSec $ConnectTimeoutSec
        if ($w.TimedOut) {
            Write-Host ("FAIL: session did not reach Connected within " + $ConnectTimeoutSec + "s (state=" + $w.State + ")")
            return 1
        }
        if (-not $w.Match) {
            Write-Host ("FAIL: session reached Connected but sessionId is " + $(if ([string]::IsNullOrWhiteSpace($w.SessionId)) { '(none)' } else { $w.SessionId }) + ", expected " + $sessionId)
            return 1
        }
        Write-Host ("connected to " + $(if ($null -eq $w.Endpoint) { '(endpoint unknown)' } else { $w.Endpoint }))
        return 0
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
            Write-Host ("FAIL: PID " + $(if ($null -eq $pidText) { '(none)' } else { $pidText }) + " is not verified as the recorded process (" + $check.Reason + "); not killing; state file kept")
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
            While no candidate is found it retries session.discover for up to
            -DiscoverTimeoutSec seconds.
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
  -DiscoverTimeoutSec <int> seconds connect retries session.discover while no
                           candidate is found (default: 45; 0 = fail immediately)
  -RequireFreshBuild       start: fail instead of launching when the App build is
                           older than the workbench repo HEAD (default: warn only)
  -StateFile <path>        override state file
                           (default: %LOCALAPPDATA%\resoloop-dev\workbench-app.json)

Exit codes: 0 = success, 1 = failure (reason printed with a FAIL: prefix).
Note: do not run start/stop concurrently (no locking is performed).
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
