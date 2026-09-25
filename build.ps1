#Requires -Version 7.4
<#
.SYNOPSIS
    Builds, analyzes and tests PwshDocker.

.DESCRIPTION
    Tasks (run in the order given):

      Clean      Remove ./out, ./TestResults and compiled core artifacts.
      Bootstrap  Download pinned test/lint dependencies (Pester, PSScriptAnalyzer) into ./.build/modules.
                 Nothing is installed system-wide.
      Build      Compile PwshDocker.Core and assemble the module into ./out/PwshDocker.
      Analyze    Run PSScriptAnalyzer over the module source.
      Test       Run Pester in a clean pwsh process against ./out/PwshDocker, so the compiled
                 assembly is never loaded (and locked) in your session.

.EXAMPLE
    ./build.ps1

    Bootstraps, builds, analyzes and runs every test (integration tests need a running Docker engine).

.EXAMPLE
    ./build.ps1 -Task Build, Test -ExcludeTag Integration

    Builds and runs only the tests that do not need a Docker engine.
#>
[CmdletBinding()]
param(
    [ValidateSet('Clean', 'Bootstrap', 'Build', 'Analyze', 'Test')]
    [string[]] $Task = @('Bootstrap', 'Build', 'Analyze', 'Test'),

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    # Only run tests with these Pester tags.
    [string[]] $Tag,

    # Skip tests with these Pester tags (e.g. Integration).
    [string[]] $ExcludeTag,

    [ValidateSet('None', 'Normal', 'Detailed', 'Diagnostic')]
    [string] $Output = 'Normal',

    # Emit JUnit test results and CI-friendly output.
    [switch] $CI
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$RepoRoot = $PSScriptRoot
$SourceDir = Join-Path $RepoRoot 'src' 'PwshDocker'
$CoreProject = Join-Path $RepoRoot 'src' 'PwshDocker.Core' 'PwshDocker.Core.csproj'
$OutDir = Join-Path $RepoRoot 'out' 'PwshDocker'
$BuildDir = Join-Path $RepoRoot '.build'
$DepsDir = Join-Path $BuildDir 'modules'
$TestsDir = Join-Path $RepoRoot 'tests'
$ResultsDir = Join-Path $RepoRoot 'TestResults'

$Dependencies = @(
    @{ Name = 'Pester'; Version = '[5.7.0, 6.0.0)' }
    @{ Name = 'PSScriptAnalyzer'; Version = '[1.23.0, 2.0.0)' }
)

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Use-Dependencies {
    $separator = [System.IO.Path]::PathSeparator
    if (-not (Test-Path $DepsDir) -or -not (Get-ChildItem $DepsDir)) {
        Invoke-Bootstrap
    }
    if (($env:PSModulePath -split [regex]::Escape($separator)) -notcontains $DepsDir) {
        $env:PSModulePath = "$DepsDir$separator$env:PSModulePath"
    }
}

function Invoke-Clean {
    Write-Step 'Clean'
    foreach ($path in @($OutDir, $ResultsDir, (Join-Path $BuildDir 'core'))) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
        }
    }
    & dotnet clean $CoreProject --nologo --verbosity quiet | Out-Null
}

function Invoke-Bootstrap {
    Write-Step 'Bootstrap dependencies'
    $null = New-Item -ItemType Directory -Path $DepsDir -Force
    foreach ($dependency in $Dependencies) {
        $present = Get-ChildItem -Path (Join-Path $DepsDir $dependency.Name) -Filter "$($dependency.Name).psd1" -Recurse -ErrorAction Ignore
        if ($present) {
            Write-Host "    $($dependency.Name) present"
            continue
        }
        Write-Host "    Saving $($dependency.Name) $($dependency.Version)"
        Save-PSResource -Name $dependency.Name -Version $dependency.Version -Path $DepsDir -Repository PSGallery -TrustRepository -Quiet
    }
}

function Invoke-Build {
    $manifestSource = Join-Path $SourceDir 'PwshDocker.psd1'
    $version = (Import-PowerShellDataFile $manifestSource).ModuleVersion

    Write-Step "Compile PwshDocker.Core $version ($Configuration)"
    $coreOut = Join-Path $BuildDir 'core'
    & dotnet build $CoreProject --configuration $Configuration --nologo --verbosity quiet "-p:Version=$version" --output $coreOut
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    Write-Step 'Assemble module'
    if (Test-Path $OutDir) {
        Remove-Item $OutDir -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path (Join-Path $OutDir 'bin') -Force
    Copy-Item (Join-Path $coreOut 'PwshDocker.Core.dll') (Join-Path $OutDir 'bin')

    # Merge Private/*.ps1 then Public/*.ps1 into the psm1 (replacing the DevLoader region).
    $files = @(
        Get-ChildItem (Join-Path $SourceDir 'Private') -Filter '*.ps1' -Recurse -ErrorAction Ignore | Sort-Object FullName
        Get-ChildItem (Join-Path $SourceDir 'Public') -Filter '*.ps1' -Recurse -ErrorAction Ignore | Sort-Object FullName
    )
    $merged = [System.Text.StringBuilder]::new()
    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($SourceDir, $file.FullName).Replace('\', '/')
        $null = $merged.AppendLine("#region $relative")
        $null = $merged.AppendLine((Get-Content -LiteralPath $file.FullName -Raw).TrimEnd())
        $null = $merged.AppendLine("#endregion $relative")
        $null = $merged.AppendLine()
    }
    $psm1 = Get-Content (Join-Path $SourceDir 'PwshDocker.psm1') -Raw
    $startMarker = '#region DevLoader'
    $endMarker = '#endregion DevLoader'
    $start = $psm1.IndexOf($startMarker, [System.StringComparison]::Ordinal)
    $end = $psm1.IndexOf($endMarker, [System.StringComparison]::Ordinal)
    if ($start -lt 0 -or $end -lt $start) {
        throw 'The DevLoader region was not found in PwshDocker.psm1.'
    }
    $psm1 = $psm1.Substring(0, $start) + $merged.ToString().TrimEnd() + $psm1.Substring($end + $endMarker.Length)
    Set-Content -LiteralPath (Join-Path $OutDir 'PwshDocker.psm1') -Value $psm1 -Encoding utf8NoBOM -NoNewline

    # Manifest with an explicit export list (fast command discovery, no wildcard exports).
    $publicNames = @(
        Get-ChildItem (Join-Path $SourceDir 'Public') -Filter '*.ps1' -Recurse -ErrorAction Ignore |
            Sort-Object BaseName |
            ForEach-Object BaseName
    )
    $exportList = if ($publicNames.Count) {
        "@(`n" + (($publicNames | ForEach-Object { "        '$_'" }) -join "`n") + "`n    )"
    } else {
        '@()'
    }
    $manifest = Get-Content $manifestSource -Raw
    $manifest = [regex]::Replace($manifest, 'FunctionsToExport\s*=\s*@\(\s*\)', { param($match) "FunctionsToExport    = $exportList" }.GetNewClosure())
    Set-Content -LiteralPath (Join-Path $OutDir 'PwshDocker.psd1') -Value $manifest -Encoding utf8NoBOM -NoNewline

    Copy-Item (Join-Path $SourceDir '*.ps1xml') $OutDir
    Copy-Item (Join-Path $SourceDir 'en-US') $OutDir -Recurse
    Copy-Item (Join-Path $RepoRoot 'LICENSE') $OutDir

    Write-Host "    $($publicNames.Count) public command(s) -> $OutDir"
}

function Invoke-Analyze {
    Write-Step 'PSScriptAnalyzer'
    Use-Dependencies
    Import-Module PSScriptAnalyzer -ErrorAction Stop
    $settings = Join-Path $RepoRoot 'PSScriptAnalyzerSettings.psd1'
    $results = @(Invoke-ScriptAnalyzer -Path $SourceDir -Recurse -Settings $settings)
    if ($results.Count) {
        $results |
            Sort-Object ScriptName, Line |
            Format-Table -AutoSize -Wrap Severity, RuleName, ScriptName, Line, Message |
            Out-String -Width 240 |
            Write-Host
        throw "PSScriptAnalyzer reported $($results.Count) issue(s)."
    }
    Write-Host '    No issues found.'
}

function Invoke-Test {
    Write-Step 'Pester'
    if (-not (Test-Path (Join-Path $OutDir 'PwshDocker.psd1'))) {
        throw 'The module has not been built. Run ./build.ps1 -Task Build first.'
    }
    Use-Dependencies
    $suffix = if ($Tag) { '-' + ($Tag -join '-') } elseif ($ExcludeTag) { '-without-' + ($ExcludeTag -join '-') } else { '' }
    $resultsFile = Join-Path $ResultsDir "testResults$suffix.xml"
    $runner = {
        param($TestsDir, $Tag, $ExcludeTag, $Output, $CI, $ResultsFile)
        $ErrorActionPreference = 'Stop'
        Import-Module Pester -MinimumVersion 5.7.0
        $config = New-PesterConfiguration
        $config.Run.Path = $TestsDir
        $config.Run.Exit = $true
        $config.Output.Verbosity = $Output
        if ($Tag) { $config.Filter.Tag = $Tag }
        if ($ExcludeTag) { $config.Filter.ExcludeTag = $ExcludeTag }
        if ($CI) {
            $config.TestResult.Enabled = $true
            $config.TestResult.OutputFormat = 'JUnitXml'
            $config.TestResult.OutputPath = $ResultsFile
        }
        Invoke-Pester -Configuration $config
    }
    & ([System.Environment]::ProcessPath) -NoProfile -NonInteractive -Command $runner -args $TestsDir, $Tag, $ExcludeTag, $Output, $CI.IsPresent, $resultsFile
    if ($LASTEXITCODE -ne 0) {
        throw "Pester reported $LASTEXITCODE failure(s)."
    }
}

foreach ($step in $Task) {
    switch ($step) {
        'Clean' { Invoke-Clean }
        'Bootstrap' { Invoke-Bootstrap }
        'Build' { Invoke-Build }
        'Analyze' { Invoke-Analyze }
        'Test' { Invoke-Test }
    }
}
