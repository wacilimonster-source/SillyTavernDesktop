[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Hash,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$ExePath,
    [double]$SizeMb = 0,
    [string]$Repository = 'wacilimonster-source/SillyTavernDesktop'
)

$ErrorActionPreference = 'Stop'

if ($Tag -ne "v$Version") {
    throw "Tag '$Tag' does not match version '$Version'."
}

$githubDir = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $githubDir
$changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
$headerPath = Join-Path $githubDir 'release-notes-header.md'
$footerPath = Join-Path $githubDir 'release-notes-footer.md'

foreach ($path in @($changelogPath, $headerPath, $footerPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required file not found: $path"
    }
}

$changelog = Get-Content -LiteralPath $changelogPath -Raw -Encoding UTF8
$escapedVersion = [regex]::Escape($Version)
$pattern = "(?ms)^## \[$escapedVersion\][^\r\n]*(?:\r?\n)(.*?)(?=^## \[|\z)"
$match = [regex]::Match($changelog, $pattern)
if (-not $match.Success) {
    throw "CHANGELOG.md has no section for version $Version (expected a '## [$Version]' heading)."
}
$body = $match.Groups[1].Value.Trim()
if (-not $body) {
    throw "CHANGELOG.md section for version $Version is empty."
}

$previousTag = $null
$tags = @(& git -C $repoRoot tag --list 'v*' --sort=-v:refname)
$index = [array]::IndexOf($tags, $Tag)
if ($index -ge 0 -and ($index + 1) -lt $tags.Count) {
    $previousTag = $tags[$index + 1]
}

$compareUrl = if ($previousTag) {
    "https://github.com/$Repository/compare/$previousTag...$Tag"
} else {
    "https://github.com/$Repository/releases"
}

$size = '-'
if ($SizeMb -gt 0) {
    $size = "~$SizeMb MB"
} elseif ($ExePath -and (Test-Path -LiteralPath $ExePath)) {
    $computedMb = [math]::Round((Get-Item -LiteralPath $ExePath).Length / 1MB, 1)
    $size = "~$computedMb MB"
}

$hasScreenshots = $false
try {
    & git -C $repoRoot cat-file -e "${Tag}:assets/screenshots/main-dark.png" 2>$null
    $hasScreenshots = ($LASTEXITCODE -eq 0)
} catch {
    $hasScreenshots = $false
}

$hero = ''
$gallery = ''
if ($hasScreenshots) {
    $rawBase = "https://raw.githubusercontent.com/$Repository/$Tag/assets/screenshots"
    $hero = @(
        '<p align="center">',
        "  <img src=`"$rawBase/main-dark.png`" width=`"720`" alt=`"SillyTavern Tray Manager`">",
        '</p>'
    ) -join "`n"
    $gallery = @(
        '<p align="center">',
        "  <img src=`"$rawBase/main-light.png`" width=`"60%`" alt=`"Light theme`">",
        "  <img src=`"$rawBase/settings.png`" width=`"38%`" alt=`"Settings`">",
        '</p>'
    ) -join "`n"
}

$header = (Get-Content -LiteralPath $headerPath -Raw -Encoding UTF8).Trim()
$footer = (Get-Content -LiteralPath $footerPath -Raw -Encoding UTF8).Trim()

$notes = @($header, '', $body, '', $footer) -join "`n"
$notes = $notes.Replace('{{HERO}}', $hero)
$notes = $notes.Replace('{{GALLERY}}', $gallery)
$notes = $notes.Replace('{{TAG}}', $Tag)
$notes = $notes.Replace('{{VERSION}}', $Version)
$notes = $notes.Replace('{{SIZE}}', $size)
$notes = $notes.Replace('{{HASH}}', $Hash.ToLowerInvariant())
$notes = $notes.Replace('{{COMPARE_URL}}', $compareUrl)
$notes = [regex]::Replace($notes, "(\r?\n){3,}", "`n`n")
$notes = $notes.Trim() + "`n"

$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$outputDir = Split-Path -Parent $resolvedOutput
if ($outputDir -and -not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

[System.IO.File]::WriteAllText($resolvedOutput, $notes, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Release notes for $Tag written to $resolvedOutput"
