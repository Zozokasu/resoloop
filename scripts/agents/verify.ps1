#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('All', 'Dotnet', 'Jsx', 'JsxContract', 'Inspect')][string]$Profile = 'All',
    [string]$TestFilter,
    [ValidateSet('All','RLoop.Tests','RLoop.Workbench.Tests','RLoop.IntegrationTests')][string]$TestProject = 'All',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_./-]*$')][string]$SourceRef
)

# Dot-sourcing exposes helpers for fixture tests; it never starts verification.
function Get-VerifyPlan {
    param([string]$Profile, [string]$Root, [string]$Evidence, [string]$Filter, [string]$TestProject='All')
    if ($Profile -notin @('All','Dotnet','Jsx','JsxContract','Inspect')) { throw 'Unknown offline profile.' }
    if ($TestProject -notin @('All','RLoop.Tests','RLoop.Workbench.Tests','RLoop.IntegrationTests')) { throw 'Unknown offline test project.' }
    if ($TestProject -ne 'All' -and $Profile -notin @('All','Dotnet')) { throw 'TestProject requires a .NET profile.' }
    if ($Filter -match '(?i)Integration|RESOLOOP_RUN|--|[\r\n;]') { throw 'Live/command options are forbidden in the test filter.' }
    $depth=0
    foreach ($character in $Filter.ToCharArray()) {
        if ($character -eq '(') { $depth++ }; if ($character -eq ')') { $depth-- }
        if ($depth -lt 0) { throw 'Unbalanced test filter would escape the offline exclusion.' }
    }
    if ($depth -ne 0) { throw 'Unbalanced test filter.' }
    $filterArg = if ($Filter) { "(Category!=Integration)&($Filter)" } else { 'Category!=Integration' }
    $plan = [System.Collections.Generic.List[object]]::new()
    if ($Profile -in @('All','Dotnet')) {
        $plan.Add(@{name='dotnet-build';file='dotnet';args=@('build','ResoLoop.slnx','--no-restore','--no-incremental');cwd=$Root;kind='command'})
        $projects=if ($TestProject -eq 'All') { @('RLoop.Tests','RLoop.Workbench.Tests','RLoop.IntegrationTests') } else { @($TestProject) }
        foreach ($project in $projects) {
            $plan.Add(@{name="test-$project";file='dotnet';args=@('test',"tests/$project/$project.csproj",'--no-build','--no-restore','--filter',$filterArg,'--logger','trx','--results-directory',(Join-Path $Evidence $project));cwd=$Root;kind='trx';results=(Join-Path $Evidence $project)})
        }
    }
    if ($Profile -in @('All','Jsx','JsxContract')) {
        $jsx = Join-Path $Root 'tools/resoloop-jsx'
        $plan.Add(@{name='jsx-compile';file='node';args=@('node_modules/typescript/bin/tsc','-p','tsconfig.json');cwd=$jsx;kind='command'})
        $plan.Add(@{name='jsx-test';file='node';args=@('--test','--test-reporter=tap','dist/test/**/*.test.js');cwd=$jsx;kind='tap'})
        if ($Profile -in @('All','JsxContract')) {
            $plan.Add(@{name='jsx-contract';file='node';args=@('dist/scripts/contract.js');cwd=$jsx;kind='contract';expectedCli=$(if ($Profile -eq 'All') { Join-Path $Root 'src/RLoop.Cli/bin/Debug/net10.0/resoloop.dll' } else { $null })})
        }
    }
    return $plan.ToArray()
}

function New-VerifyStartInfo {
    param([string]$File, [string[]]$Arguments, [string]$WorkingDirectory)
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName=$File; $info.WorkingDirectory=$WorkingDirectory
    $info.UseShellExecute=$false; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    # Change only the child's environment. Never log environment values.
    foreach ($key in @($info.Environment.Keys)) {
        if ($key -match '(?i)^RESOLOOP_RUN_|^RESOLOOP_.*(LIVE|INTEGRATION)|^RESONITE_LINK_URL$') { [void]$info.Environment.Remove($key) }
    }
    $info.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
    $info.Environment['DOTNET_NOLOGO']='1'
    return $info
}

function Invoke-VerifyCommand {
    param($Step, [string]$Evidence)
    $record = [ordered]@{name=$Step.name;command=$Step.file;arguments=$Step.args;workingDirectory=$Step.cwd;status='failed';startedUtc=[DateTime]::UtcNow.ToString('o');endedUtc=$null;exitCode=$null;processId=$null;stdout=(Join-Path $Evidence "$($Step.name).stdout.txt");stderr=(Join-Path $Evidence "$($Step.name).stderr.txt");error=$null}
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = New-VerifyStartInfo $Step.file $Step.args $Step.cwd
    $outStream=$null; $errStream=$null; $outTask=$null; $errTask=$null; $started=$false
    try {
        $outStream=[IO.File]::Open($record.stdout,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        $errStream=[IO.File]::Open($record.stderr,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        if (-not $process.Start()) { throw 'Process did not start.' }
        $started=$true
        $record.processId=$process.Id
        $outTask=$process.StandardOutput.BaseStream.CopyToAsync($outStream)
        $errTask=$process.StandardError.BaseStream.CopyToAsync($errStream)
        $process.WaitForExit()
        $outTask.GetAwaiter().GetResult() | Out-Null; $errTask.GetAwaiter().GetResult() | Out-Null
        $record.exitCode=$process.ExitCode
    } catch { $record.error=$_.Exception.Message }
    finally {
        # On interruption/errors, finish this process tree before releasing the worktree lock.
        try {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        } catch { if (-not $record.error) { $record.error=$_.Exception.Message } }
        foreach ($copyTask in @($outTask,$errTask)) {
            if ($null -ne $copyTask) {
                try { $copyTask.GetAwaiter().GetResult() | Out-Null }
                catch { if (-not $record.error) { $record.error=$_.Exception.Message } }
            }
        }
        if ($outStream) { $outStream.Dispose() }; if ($errStream) { $errStream.Dispose() }
        $process.Dispose(); $record.endedUtc=[DateTime]::UtcNow.ToString('o')
    }
    return [pscustomobject]$record
}

function Read-VerifyCounts {
    param($Step, $Record)
    if ($null -eq $Record.exitCode -or $Record.exitCode -ne 0 -or $Record.error) { throw "Command failed: $($Step.name)" }
    switch ($Step.kind) {
        'trx' {
            $files=@(Get-ChildItem -LiteralPath $Step.results -Filter '*.trx' -Recurse -File -ErrorAction Stop)
            if ($files.Count -eq 0) { throw 'TRX missing.' }
            $totals=@{executed=0L;passed=0L;failed=0L;skipped=0L}
            foreach ($file in $files) {
                $settings=[Xml.XmlReaderSettings]::new(); $settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit; $settings.XmlResolver=$null
                $reader=[Xml.XmlReader]::Create($file.FullName,$settings)
                try { $xml=[Xml.XmlDocument]::new(); $xml.XmlResolver=$null; $xml.Load($reader) } finally { $reader.Dispose() }
                $nodes=@($xml.SelectNodes('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]'))
                if ($nodes.Count -ne 1) { throw 'TRX counters missing or ambiguous.' }
                $values=@{}
                foreach ($name in @('total','executed','passed','failed','error','timeout','aborted','notExecuted')) {
                    $value=$nodes[0].GetAttribute($name)
                    if ($value -notmatch '^\d+$') { throw "Malformed TRX counter: $name" }
                    $values[$name]=[long]$value
                }
                if ($values.executed -le 0 -or $values.passed -le 0 -or $values.failed -ne 0 -or $values.error -ne 0 -or $values.timeout -ne 0 -or $values.aborted -ne 0 -or $values.executed -ne $values.passed -or $values.total -ne ($values.executed+$values.notExecuted)) { throw 'TRX did not prove a successful nonempty execution.' }
                $totals.executed+=$values.executed; $totals.passed+=$values.passed; $totals.failed+=$values.failed; $totals.skipped+=$values.notExecuted
            }
            return $totals
        }
        'tap' {
            $content=Get-Content -LiteralPath $Record.stdout -Raw
            $values=@{}
            foreach ($name in @('tests','pass','fail','cancelled','skipped','todo')) {
                $matches=[regex]::Matches($content,"(?m)^# $name (\d+)\r?$")
                if ($matches.Count -ne 1) { throw "TAP counter missing or ambiguous: $name" }
                $values[$name]=[long]$matches[0].Groups[1].Value
            }
            if ($values.tests -le 0 -or $values.pass -le 0 -or $values.fail -ne 0 -or $values.cancelled -ne 0 -or $values.tests -ne ($values.pass+$values.skipped+$values.todo)) { throw 'TAP did not prove a successful nonempty execution.' }
            return $values
        }
        'contract' {
            $content=Get-Content -LiteralPath $Record.stdout -Raw
            $matches=[regex]::Matches($content,'(?m)^contract: checked=(\d+) passed=(\d+) failed=(\d+)\r?$')
            if ($matches.Count -ne 1) { throw 'Contract counters missing or ambiguous.' }
            $checked=[long]$matches[0].Groups[1].Value; $passed=[long]$matches[0].Groups[2].Value; $failed=[long]$matches[0].Groups[3].Value
            if ($checked -le 0 -or $passed -ne $checked -or $failed -ne 0) { throw 'Contract did not prove a successful nonempty execution.' }
            return @{checked=$checked;passed=$passed;failed=$failed}
        }
    }
    return $null
}

function Invoke-VerifySteps {
    param([object[]]$Plan, [string]$Evidence)
    $records=[System.Collections.Generic.List[object]]::new(); $failed=$false
    foreach ($step in $Plan) {
        if ($failed) { $records.Add([pscustomobject]@{name=$step.name;status='unrun';reason='earlier step failed'}); continue }
        if ($step.expectedCli) {
            try {
                $cli=Get-VerifyCli (Split-Path (Split-Path $step.cwd -Parent) -Parent)
                $build=@($records | Where-Object name -eq 'dotnet-build')[0]
                if ($cli.path -ne [IO.Path]::GetFullPath($step.expectedCli) -or [DateTime]$cli.modifiedUtc -lt [DateTime]$build.startedUtc) { throw 'Contract would select a CLI other than the fresh Debug build.' }
            } catch { $records.Add([pscustomobject]@{name=$step.name;status='failed';error=$_.Exception.Message;exitCode=$null}); $failed=$true; continue }
        }
        $record=Invoke-VerifyCommand $step $Evidence
        try {
            $counts=Read-VerifyCounts $step $record
            $record | Add-Member counts $counts
            $record.status='passed'
        } catch { $record.status='failed'; $record.error=$_.Exception.Message; $failed=$true }
        $records.Add($record)
    }
    return $records.ToArray()
}

function Invoke-VerifyGit {
    param([string]$Root, [string[]]$Arguments)
    $process=[Diagnostics.Process]::new()
    $safeRoot=$Root.Replace('\','/')
    $process.StartInfo=New-VerifyStartInfo 'git' (@('-c',"safe.directory=$safeRoot",'-C',$Root)+$Arguments) $Root
    try {
        if (-not $process.Start()) { throw 'Git did not start.' }
        $outTask=$process.StandardOutput.ReadToEndAsync(); $errTask=$process.StandardError.ReadToEndAsync()
        $process.WaitForExit(); $output=$outTask.GetAwaiter().GetResult(); $errTask.GetAwaiter().GetResult() | Out-Null
        if ($process.ExitCode -ne 0) { throw "Read-only Git command failed: $($Arguments[0])" }
        return $output
    } finally { $process.Dispose() }
}

function Get-VerifyDigest {
    param([string]$Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function Get-VerifySnapshot {
    param([string]$Root)
    $entries=[System.Collections.Generic.List[object]]::new(); $excluded=[System.Collections.Generic.List[string]]::new()
    $paths=(Invoke-VerifyGit $Root @('ls-files','-z','--cached','--others','--exclude-standard')).Split([char]0,[StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -Unique
    foreach ($path in $paths) {
        # Private editor/trust/credential files are never opened, even if untracked.
        if ($path -match '^(\.vscode/|\.aws/|\.codex/|\.agents/|\.devin/|\.ccg/|\.claude/settings\.local\.json$|TestResults/)' -or $path -match '(^|/)(\.env($|\.)|credentials?($|\.)|secrets?($|\.))') { $excluded.Add($path); continue }
        $full=Join-Path $Root $path
        if (Test-Path -LiteralPath $full) {
            $item=Get-Item -LiteralPath $full
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source reparse point unsupported: $path" }
            $hash=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        } else { $hash='missing' }
        $entries.Add([ordered]@{path=$path;sha256=$hash})
    }
    $head=(Invoke-VerifyGit $Root @('rev-parse','HEAD')).Trim()
    $tree=(Invoke-VerifyGit $Root @('rev-parse','HEAD^{tree}')).Trim()
    $branch=(Invoke-VerifyGit $Root @('branch','--show-current')).Trim()
    $status=Invoke-VerifyGit $Root @('status','--porcelain=v1','-z','--untracked-files=all')
    return @{head=$head;tree=$tree;branch=$branch;sourceHash=(Get-VerifyDigest ($entries | ConvertTo-Json -Depth 5 -Compress));statusHash=(Get-VerifyDigest $status);gitStatus=$status;files=$entries.ToArray();excludedPaths=$excluded.ToArray();exclusions=@('Git-ignored files','TestResults/','.vscode/','.aws/','.codex/','.agents/','.devin/','.ccg/','.claude/settings.local.json','basename .env or .env.*; credential/credentials/secret/secrets or those names followed by a dot')}
}

function Test-VerifyStable {
    param($Before,$After)
    return ($Before.head -eq $After.head -and $Before.tree -eq $After.tree -and $Before.branch -eq $After.branch -and $Before.sourceHash -eq $After.sourceHash -and $Before.statusHash -eq $After.statusHash)
}

function Enter-VerifyLock {
    param([string]$Root)
    # Persistent empty lock inode avoids deleting another process's lock. OS releases on crash.
    $path=Join-Path $Root 'TestResults/agent-verification/verify.lock'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    return [IO.File]::Open($path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
}

function Get-VerifyCli {
    param([string]$Root)
    $bin=Join-Path $Root 'src/RLoop.Cli/bin'
    $dll=@(Get-ChildItem -LiteralPath $bin -Filter resoloop.dll -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'net10\.0' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
    if ($dll.Count -ne 1) { throw 'Prebuilt CLI missing; no restore/build fallback is allowed for JsxContract.' }
    return @{path=$dll[0].FullName;sha256=(Get-FileHash -LiteralPath $dll[0].FullName).Hash;modifiedUtc=$dll[0].LastWriteTimeUtc.ToString('o')}
}

function Invoke-OfflineVerification {
    param([string]$Profile,[string]$TestFilter,[string]$SourceRef,[string]$TestProject='All')
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    # Validate before creating evidence or launching anything.
    [void](Get-VerifyPlan $Profile $root $root $TestFilter $TestProject)
    if ($SourceRef -and $Profile -ne 'Inspect') { throw 'SourceRef is only supported by Inspect.' }
    if ($TestFilter -and $Profile -notin @('All','Dotnet')) { throw 'TestFilter requires a .NET profile.' }
    $lock=Enter-VerifyLock $root
    $evidence=Join-Path $root ('TestResults/agent-verification/run-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+[Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $evidence) { $lock.Dispose(); throw 'Evidence directory already exists; refusing overwrite.' }
    $summary=[ordered]@{schemaVersion=1;profile=$Profile;testProject=$TestProject;status='failed';root=$root;startedUtc=[DateTime]::UtcNow.ToString('o');endedUtc=$null;before=$null;after=$null;stable=$false;steps=@();unselected=@();tools=@();cli=$null;preflight=$null;live=@{status='skipped';evidence='unverified';filter='Category!=Integration';reason='Offline runner excludes Integration and clears live opt-ins in every child.'};error=$null}
    try {
        [IO.Directory]::CreateDirectory($evidence) | Out-Null
        $plan=@(Get-VerifyPlan $Profile $root $evidence $TestFilter $TestProject)
        $summary.steps=@($plan | ForEach-Object { @{name=$_.name;status='unrun';reason='not yet executed'} })
        $summary.unselected=@((Get-VerifyPlan 'All' $root $evidence '') | Where-Object { $_.name -notin $plan.name } | ForEach-Object { @{name=$_.name;status='skipped';evidence='unverified';reason='not selected by profile'} })
        $summary.before=Get-VerifySnapshot $root
        $summary.before | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'before.json')
        $versions=@(@{name='version-git';file='git';args=@('--version');cwd=$root;kind='command'})
        if ($Profile -in @('All','Dotnet','JsxContract')) { $versions+=@{name='version-dotnet';file='dotnet';args=@('--version');cwd=$root;kind='command'} }
        if ($Profile -in @('All','Jsx','JsxContract')) {
            $versions+=@{name='version-node';file='node';args=@('--version');cwd=$root;kind='command'}
            $versions+=@{name='version-typescript';file='node';args=@('node_modules/typescript/bin/tsc','--version');cwd=(Join-Path $root 'tools/resoloop-jsx');kind='command'}
        }
        $summary.tools=@(Invoke-VerifySteps $versions $evidence)
        $summary.tools+=@{name='powershell';version=$PSVersionTable.PSVersion.ToString();status='observed'}
        if (@($summary.tools | Where-Object status -eq 'failed').Count) { throw 'Tool prerequisite failed.' }
        if ($Profile -eq 'JsxContract') { $summary.cli=Get-VerifyCli $root }
        if ($SourceRef) {
            $source=(Invoke-VerifyGit $root @('rev-parse','--verify',"$SourceRef^{commit}")).Trim()
            $base=(Invoke-VerifyGit $root @('merge-base','HEAD',$source)).Trim()
            $ours=(Invoke-VerifyGit $root @('diff','--name-only',$base,'HEAD')).Split("`n",[StringSplitOptions]::RemoveEmptyEntries)
            $theirs=(Invoke-VerifyGit $root @('diff','--name-only',$base,$source)).Split("`n",[StringSplitOptions]::RemoveEmptyEntries)
            $summary.preflight=@{sourceRef=$SourceRef;sourceCommit=$source;mergeBase=$base;overlappingPaths=@($ours | Where-Object { $_ -in $theirs });status='observed';decision='No merge approval or conflict guarantee.'}
        }
        if ($plan.Count) { $summary.steps=@(Invoke-VerifySteps $plan $evidence) }
        if (@($summary.steps | Where-Object status -eq 'failed').Count) { throw 'Selected verification failed.' }
        if ($Profile -eq 'All') { $summary.cli=Get-VerifyCli $root }
        if ($Profile -in @('All','JsxContract')) {
            $contract=@($summary.steps | Where-Object name -eq 'jsx-contract')[0]
            $identity='contract: using '+$summary.cli.path
            if (-not (Get-Content -LiteralPath $contract.stdout | Where-Object { $_ -eq $identity })) { throw 'Contract CLI identity differs from observed prebuilt CLI.' }
        }
        $summary.status=if ($Profile -eq 'Inspect') { 'inspected' } else { 'passed' }
    } catch { $summary.status='failed'; $summary.error=$_.Exception.Message }
    finally {
        try {
            $summary.after=Get-VerifySnapshot $root
            $summary.after | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'after.json')
            $summary.stable=($null -ne $summary.before -and (Test-VerifyStable $summary.before $summary.after))
            if (-not $summary.stable) { $summary.status='failed'; $summary.error='Source snapshot changed or could not be verified.' }
        } catch { $summary.status='failed'; $summary.error=$_.Exception.Message }
        $summary.endedUtc=[DateTime]::UtcNow.ToString('o')
        try { $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json') }
        finally { $lock.Dispose() }
    }
    Write-Host "OFFLINE $($summary.status.ToUpperInvariant()) profile=$Profile"
    Write-Host "HEAD $($summary.before.head) source=$($summary.before.sourceHash) stable=$($summary.stable)"
    Write-Host "Steps passed=$(@($summary.steps | Where-Object status -eq 'passed').Count) failed=$(@($summary.steps | Where-Object status -eq 'failed').Count) unrun=$(@($summary.steps | Where-Object status -eq 'unrun').Count); live=SKIPPED (unverified)"
    Write-Host "Evidence $evidence"
    if ($summary.error) { Write-Host "Error $($summary.error)" }
    return $(if ($summary.status -eq 'failed') { 1 } else { 0 })
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference='Stop'
    try { exit (Invoke-OfflineVerification $Profile $TestFilter $SourceRef $TestProject) }
    catch { Write-Host "OFFLINE FAILED: $($_.Exception.Message)"; exit 1 }
}
