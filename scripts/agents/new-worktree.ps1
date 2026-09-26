<#
.SYNOPSIS
Prepares an isolated git worktree "slot" for one parallel SWE-2 task and copies the local Devin permission config into it.

.DESCRIPTION
Devin refuses to run non-interactively in a directory it has not trusted, and trusting requires one interactive run.
So parallel work uses a small fixed set of reusable slots (..\resoloop-worktrees\slot1..slot3) that the user trusts once
(`cd ..\resoloop-worktrees\slotN` then `devin`, answer the trust prompt, exit). Never edit Devin's trust files.

Each call points the slot at a new branch agent/<Name> created from the main tree's current HEAD. A slot that still
has uncommitted changes, or whose branch was not merged or deleted, is refused (integrate or discard first).
Prints the slot path on success.
#>
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,60}$')][string]$Name,
    [Parameter(Mandatory = $true)][ValidateRange(1, 3)][int]$Slot
)
$ErrorActionPreference = 'Stop'
$repo = (git rev-parse --show-toplevel).Trim()
$root = Join-Path (Split-Path $repo -Parent) 'resoloop-worktrees'
$path = Join-Path $root "slot$Slot"
$head = (git -C $repo rev-parse HEAD).Trim()
New-Item -ItemType Directory -Force $root | Out-Null

if (Test-Path (Join-Path $path '.git')) {
    $dirty = git -C $path status --porcelain -- . ':!.devin/config.local.json'
    if ($dirty) { throw "slot$Slot has uncommitted changes; integrate or discard them first." }
    $current = (git -C $path rev-parse --abbrev-ref HEAD).Trim()
    if ($current -like 'agent/*') {
        git -C $repo merge-base --is-ancestor $current HEAD 2>$null
        if ($LASTEXITCODE -ne 0) { throw "slot$Slot branch $current is not merged into the main tree yet." }
    }
    git -C $path checkout --quiet -b "agent/$Name" $head
    if ($LASTEXITCODE -ne 0) { throw "could not create branch agent/$Name in slot$Slot" }
    if ($current -like 'agent/*') { git -C $repo branch -d $current | Out-Null }
}
else {
    git -C $repo worktree add -b "agent/$Name" $path $head | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed for slot$Slot" }
    Write-Warning "slot$Slot was just created. Devin must trust it once interactively before non-interactive runs: cd `"$path`"; devin"
}

$localConfig = Join-Path $repo '.devin\config.local.json'
if (Test-Path $localConfig) {
    New-Item -ItemType Directory -Force (Join-Path $path '.devin') | Out-Null
    Copy-Item $localConfig (Join-Path $path '.devin\config.local.json') -Force
}
Write-Output $path
