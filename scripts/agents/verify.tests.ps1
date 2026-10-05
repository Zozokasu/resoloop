#requires -Version 7.2
$ErrorActionPreference='Stop'
. "$PSScriptRoot/verify.ps1"
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence=Join-Path $repo ('TestResults/AGENT-AUTOMATION-20261005/focused-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$results=[System.Collections.Generic.List[object]]::new()
function Assert-True { param($Value,[string]$Message); if (-not $Value) { throw $Message } }
function Assert-Throws { param([scriptblock]$Action); $threw=$false; try { & $Action | Out-Null } catch { $threw=$true }; Assert-True $threw 'Expected rejection.' }
function Test-Case {
    param([string]$Name,[scriptblock]$Action)
    try { & $Action; $results.Add(@{name=$Name;status='passed'}) }
    catch { $results.Add(@{name=$Name;status='failed';error=$_.Exception.Message}) }
}
function New-FixtureRecord {
    param([string]$Name,[string]$Text)
    $path=Join-Path $evidence "$Name.txt"; Set-Content -LiteralPath $path -Value $Text
    return [pscustomobject]@{stdout=$path;exitCode=0;error=$null}
}

Test-Case 'profiles are bounded; JSX has no dotnet or double compilation' {
    foreach ($profile in @('All','Dotnet','Jsx','JsxContract','Inspect')) {
        $plan=@(Get-VerifyPlan $profile $repo $evidence '')
        Assert-True (@($plan | Where-Object name -eq 'jsx-compile').Count -le 1) 'Duplicate TypeScript build.'
        foreach ($step in ($plan | Where-Object kind -eq 'trx')) {
            Assert-True ('Category!=Integration' -in $step.args) 'Integration exclusion missing.'
            Assert-True ('--no-build' -in $step.args -and '--no-restore' -in $step.args) 'Test rebuild/restore allowed.'
        }
        if ($profile -eq 'Jsx') { Assert-True (@($plan | Where-Object { $_.file -eq 'dotnet' -or $_.name -eq 'jsx-contract' }).Count -eq 0) 'JSX requires dotnet.' }
        if ($profile -eq 'Inspect') { Assert-True ($plan.Count -eq 0) 'Inspect selects build/test.' }
    }
    $all=@(Get-VerifyPlan 'All' $repo $evidence '')
    Assert-True (@($all | Where-Object name -eq 'test-RLoop.IntegrationTests').Count -eq 1) 'Offline isolation tests omitted.'
    Assert-True ($all[0].args -contains '--no-incremental') 'Fresh build missing.'
    $filtered=@(Get-VerifyPlan 'Dotnet' $repo $evidence 'FullyQualifiedName~Isolation')
    Assert-True ($filtered[1].args -contains '(Category!=Integration)&(FullyQualifiedName~Isolation)') 'Filter not ANDed.'
    Assert-Throws { Get-VerifyPlan 'Live' $repo $evidence '' }
    Assert-Throws { Get-VerifyPlan 'Dotnet' $repo $evidence 'Category=Integration' }
    Assert-Throws { Get-VerifyPlan 'Dotnet' $repo $evidence '--environment RESOLOOP_RUN_INTEGRATION=1' }
    Assert-Throws { Get-VerifyPlan 'Dotnet' $repo $evidence ')|Category!=Offline|(' }
    $selected=@(Get-VerifyPlan 'Dotnet' $repo $evidence 'FullyQualifiedName~Isolation' 'RLoop.IntegrationTests')
    Assert-True ($selected.Count -eq 2 -and $selected[1].name -eq 'test-RLoop.IntegrationTests') 'Project selector ran unrelated tests.'
    Assert-Throws { Get-VerifyPlan 'Jsx' $repo $evidence '' 'RLoop.Tests' }
}

Test-Case 'live opt-ins removed from child without mutating parent' {
    $old=$env:RESOLOOP_RUN_INTEGRATION
    $oldCapture=$env:RESOLOOP_RUN_CAPTURE_INTEGRATION
    try {
        $env:RESOLOOP_RUN_INTEGRATION='1'; $env:RESOLOOP_RUN_CAPTURE_INTEGRATION='1'
        $info=New-VerifyStartInfo 'node' @('--version') $repo
        Assert-True (-not $info.Environment.ContainsKey('RESOLOOP_RUN_INTEGRATION')) 'Live inherited.'
        Assert-True (-not $info.Environment.ContainsKey('RESOLOOP_RUN_CAPTURE_INTEGRATION')) 'Capture inherited.'
        Assert-True ($env:RESOLOOP_RUN_INTEGRATION -eq '1') 'Parent altered.'
    } finally { $env:RESOLOOP_RUN_INTEGRATION=$old; $env:RESOLOOP_RUN_CAPTURE_INTEGRATION=$oldCapture }
}

Test-Case 'TRX counts require actual passing execution and safe XML' {
    $dir=Join-Path $evidence 'trx'; [IO.Directory]::CreateDirectory($dir) | Out-Null
    $step=@{name='fixture';kind='trx';results=$dir}; $record=New-FixtureRecord 'trx-output' ''
    Assert-Throws { Read-VerifyCounts $step $record }
    $file=Join-Path $dir 'fixture.trx'
    $valid='<TestRun><ResultSummary><Counters total="3" executed="2" passed="2" failed="0" error="0" timeout="0" aborted="0" notExecuted="1" /></ResultSummary></TestRun>'
    Set-Content $file $valid
    $counts=Read-VerifyCounts $step $record
    Assert-True ($counts.executed -eq 2 -and $counts.skipped -eq 1) 'TRX skip counted as pass.'
    foreach ($invalid in @($valid.Replace('executed="2"','executed="0"'),$valid.Replace('passed="2"','passed="x"'),'<TestRun/>','<!DOCTYPE TestRun [<!ENTITY x SYSTEM "file:///nonexistent">]><TestRun>&x;</TestRun>')) {
        Set-Content $file $invalid; Assert-Throws { Read-VerifyCounts $step $record }
    }
}

Test-Case 'TAP missing, malformed, zero and all-skipped cannot pass' {
    $step=@{name='fixture';kind='tap'}
    $valid="# tests 3`n# pass 1`n# fail 0`n# cancelled 0`n# skipped 2`n# todo 0"
    $record=New-FixtureRecord 'tap-valid' $valid; $counts=Read-VerifyCounts $step $record
    Assert-True ($counts.pass -eq 1 -and $counts.skipped -eq 2) 'Skipped reported passed.'
    foreach ($text in @('', $valid.Replace('# pass 1','# pass x'),$valid.Replace('# tests 3','# tests 0'),$valid.Replace('# pass 1','# pass 0'),($valid+"`n# tests 3"),$valid.Replace('# fail 0','# fail 1'))) {
        $record=New-FixtureRecord ('tap-'+[Guid]::NewGuid().ToString('N')) $text
        Assert-Throws { Read-VerifyCounts $step $record }
    }
}

Test-Case 'contract needs exact nonempty checked/passed/failed summary' {
    $step=@{name='fixture';kind='contract'}
    $record=New-FixtureRecord 'contract-valid' 'contract: checked=2 passed=2 failed=0'
    Assert-True ((Read-VerifyCounts $step $record).checked -eq 2) 'Valid contract rejected.'
    foreach ($text in @('','contract: checked=0 passed=0 failed=0','contract: checked=2 passed=1 failed=1','contract: checked=x passed=2 failed=0')) {
        $record=New-FixtureRecord ('contract-'+[Guid]::NewGuid().ToString('N')) $text
        Assert-Throws { Read-VerifyCounts $step $record }
    }
    $record.exitCode=3; Assert-Throws { Read-VerifyCounts $step $record }
}

Test-Case 'failed command stops execution; later steps explicitly unrun' {
    $original=${function:Invoke-VerifyCommand}; $script:fixtureInvocations=0
    try {
        function Invoke-VerifyCommand {
            param($Step,[string]$Evidence)
            $script:fixtureInvocations++
            return [pscustomobject]@{name=$Step.name;exitCode=7;error=$null;status='failed'}
        }
        $records=@(Invoke-VerifySteps @(@{name='first';kind='command'},@{name='second';kind='command'}) $evidence)
        Assert-True ($script:fixtureInvocations -eq 1) 'Later command executed.'
        Assert-True ($records[0].status -eq 'failed' -and $records[1].status -eq 'unrun') 'Failure falsely passed/skipped.'
    } finally { ${function:Invoke-VerifyCommand}=$original }
}

Test-Case 'source and target drift reject otherwise passing results' {
    $before=@{head='a';tree='b';branch='main';sourceHash='c';statusHash='d'}
    Assert-True (Test-VerifyStable $before $before.Clone()) 'Stable rejected.'
    foreach ($field in @('head','tree','branch','sourceHash','statusHash')) {
        $after=$before.Clone(); $after[$field]='changed'
        Assert-True (-not (Test-VerifyStable $before $after)) "Drift accepted: $field"
    }
}

Test-Case 'filesystem lock rejects contention and releases without cleanup' {
    $fixtureRoot=Join-Path $evidence 'lock-fixture'
    $lock=Enter-VerifyLock $fixtureRoot
    try { Assert-Throws { $second=Enter-VerifyLock $fixtureRoot; $second.Dispose() } }
    finally { $lock.Dispose() }
    $released=Enter-VerifyLock $fixtureRoot; $released.Dispose()
}

Test-Case 'process execution streams both channels and records exit code' {
    $shell=(Get-Process -Id $PID).Path
    $step=@{name='process-fixture';file=$shell;args=@('-NoProfile','-NonInteractive','-Command','[Console]::Out.WriteLine("fixture stdout"); [Console]::Error.WriteLine("fixture stderr"); exit 9');cwd=$repo;kind='command'}
    $record=Invoke-VerifyCommand $step $evidence
    Assert-True ($record.exitCode -eq 9) 'Exit code lost.'
    Assert-True ((Get-Content $record.stdout -Raw).Contains('fixture stdout')) 'Stdout lost.'
    Assert-True ((Get-Content $record.stderr -Raw).Contains('fixture stderr')) 'Stderr lost.'
    Assert-Throws { Read-VerifyCounts $step $record }
    $step.name='process-success'; $step.args=@('-NoProfile','-NonInteractive','-Command','exit 0')
    $success=@(Invoke-VerifySteps @($step) $evidence)
    Assert-True ($success[0].status -eq 'passed') 'Successful command incorrectly failed.'
}

Test-Case 'post-start failure kills and joins child; preserves primary error' {
    $original=${function:New-VerifyStartInfo}
    try {
        $script:fixtureStartInfo=$original
        function New-VerifyStartInfo {
            param([string]$File,[string[]]$Arguments,[string]$WorkingDirectory)
            $info=& $script:fixtureStartInfo $File $Arguments $WorkingDirectory
            $info.RedirectStandardOutput=$false # Failure occurs only after successful Process.Start.
            return $info
        }
        $step=@{name='process-interruption';file=(Get-Process -Id $PID).Path;args=@('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30');cwd=$repo;kind='command'}
        $record=Invoke-VerifyCommand $step $evidence
        Assert-True ($null -ne $record.processId -and $record.error) 'Post-start failure was not captured.'
        Assert-True (-not (Get-Process -Id $record.processId -ErrorAction SilentlyContinue)) 'Child survived cleanup.'
        Assert-True ($record.error -match 'null-valued') 'Primary error was masked.'
    } finally { ${function:New-VerifyStartInfo}=$original }
}

$failed=@($results | Where-Object status -eq 'failed').Count
@{status=$(if ($failed) { 'failed' } else { 'passed' });cases=$results.ToArray();targetHead=(Invoke-VerifyGit $repo @('rev-parse','HEAD')).Trim();live='skipped/unverified';fullPipeline='unrun';files=@('scripts/agents/verify.ps1','scripts/agents/verify.tests.ps1','docs/dev/deterministic-verification.md')} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidence 'focused-summary.json')
Write-Host "FOCUSED tests passed=$($results.Count-$failed) failed=$failed; full pipeline=UNRUN; live=SKIPPED (unverified)"
Write-Host "Evidence $evidence"
foreach ($failure in ($results | Where-Object status -eq 'failed')) { Write-Host "$($failure.name): $($failure.error)" }
exit $(if ($failed) { 1 } else { 0 })
