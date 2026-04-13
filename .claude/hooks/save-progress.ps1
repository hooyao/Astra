# SessionEnd hook: snapshot project progress to progress.md
# Receives JSON on stdin with session_id, transcript_path, cwd

$ErrorActionPreference = 'SilentlyContinue'

# Read hook input
$raw = [Console]::In.ReadToEnd()
$hookData = $raw | ConvertFrom-Json
$projectDir = $hookData.cwd
if (-not $projectDir) { $projectDir = Get-Location }

$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$dateLine = Get-Date -Format "yyyy-MM-dd"

# --- Git state ---
Push-Location $projectDir

$recentCommits = git log --oneline -15 2>$null
$uncommitted   = git status --short 2>$null
$branch        = git branch --show-current 2>$null

# Source file listing (src/ only)
$srcFiles = git ls-files "src/**" 2>$null

Pop-Location

# --- Build progress.md ---
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# Project Progress")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Last updated: $timestamp")
[void]$sb.AppendLine("Branch: $branch")
[void]$sb.AppendLine("")

# Recent commits
[void]$sb.AppendLine("## Recent Commits")
[void]$sb.AppendLine("")
if ($recentCommits) {
    foreach ($line in $recentCommits) {
        [void]$sb.AppendLine("- ``$line``")
    }
} else {
    [void]$sb.AppendLine("(no commits yet)")
}
[void]$sb.AppendLine("")

# Uncommitted changes
[void]$sb.AppendLine("## Uncommitted Changes")
[void]$sb.AppendLine("")
if ($uncommitted) {
    [void]$sb.AppendLine('```')
    foreach ($line in $uncommitted) {
        [void]$sb.AppendLine($line)
    }
    [void]$sb.AppendLine('```')
} else {
    [void]$sb.AppendLine("(clean working tree)")
}
[void]$sb.AppendLine("")

# Source structure
[void]$sb.AppendLine("## Source Files")
[void]$sb.AppendLine("")
if ($srcFiles) {
    [void]$sb.AppendLine('```')
    foreach ($line in $srcFiles) {
        [void]$sb.AppendLine($line)
    }
    [void]$sb.AppendLine('```')
}

# Write file
$outPath = Join-Path $projectDir "progress.md"
[System.IO.File]::WriteAllText($outPath, $sb.ToString(), [System.Text.Encoding]::UTF8)

# Hook output (silent)
Write-Output '{"continue":true,"suppressOutput":true}'
