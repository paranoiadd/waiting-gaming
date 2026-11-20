[CmdletBinding()]
param([switch]$Test)

$ErrorActionPreference = 'Stop'

$frameworkDirectories = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319')
)
$frameworkDirectory = $frameworkDirectories | Where-Object {
    Test-Path -LiteralPath (Join-Path $_ 'csc.exe') -PathType Leaf
} | Select-Object -First 1
if (-not $frameworkDirectory) {
    throw 'The Windows .NET Framework 4.x C# compiler was not found.'
}
$compiler = Join-Path $frameworkDirectory 'csc.exe'

function Invoke-CSharpCompiler {
    param(
        [string]$OutputPath,
        [string]$Target,
        [string[]]$SourceFiles,
        [string[]]$Options
    )

    & $compiler /nologo /langversion:5 /codepage:65001 /optimize+ "/target:$Target" "/out:$OutputPath" @Options @SourceFiles
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed for $OutputPath (exit $LASTEXITCODE)."
    }
}

$sourceDirectory = Join-Path $PSScriptRoot 'src'
$packageDirectory = Join-Path $PSScriptRoot 'package'
$distDirectory = Join-Path $PSScriptRoot 'dist'
$compiledDirectory = Join-Path $distDirectory 'build'
$marketplaceDirectory = Join-Path $distDirectory 'marketplace'
$catalogDirectory = Join-Path $marketplaceDirectory '.agents\plugins'
$pluginDirectory = Join-Path $marketplaceDirectory 'plugins\waiting-gaming'
$binaryDirectory = Join-Path $pluginDirectory 'bin'

foreach ($relativePath in '.codex-plugin\plugin.json', 'hooks\hooks.json') {
    $packageFile = Join-Path $packageDirectory $relativePath
    if (-not (Test-Path -LiteralPath $packageFile -PathType Leaf)) {
        throw "Missing plugin package file: $packageFile"
    }
}

$turnEvents = Join-Path $sourceDirectory 'TurnEvents.cs'
$codexWindow = Join-Path $sourceDirectory 'CodexWindow.cs'
$hook = Join-Path $sourceDirectory 'Hook.cs'
$program = Join-Path $sourceDirectory 'Program.cs'
$windowManifest = Join-Path $sourceDirectory 'WaitingGaming.manifest'
$gameSources = Get-ChildItem -LiteralPath $sourceDirectory -Filter 'Game*.cs' -File | ForEach-Object FullName
$windowSources = @($program, $turnEvents, $codexWindow) + @($gameSources)
$windowReferences = @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll')
$jsonReference = '/reference:System.Web.Extensions.dll'

[void][System.IO.Directory]::CreateDirectory($compiledDirectory)
$gameExecutable = Join-Path $compiledDirectory 'WaitingGaming.exe'
$hookExecutable = Join-Path $compiledDirectory 'WaitingGaming.Hook.exe'

$gameOptions = $windowReferences + @($jsonReference, "/win32manifest:$windowManifest")
Invoke-CSharpCompiler -OutputPath $gameExecutable -Target winexe -SourceFiles $windowSources -Options $gameOptions
Invoke-CSharpCompiler -OutputPath $hookExecutable -Target exe -SourceFiles @($hook, $turnEvents, $codexWindow) -Options @($jsonReference)

if ($Test) {
    $testExecutable = Join-Path $compiledDirectory 'WaitingGaming.Tests.exe'
    $testSources = $windowSources + @($hook, (Join-Path $PSScriptRoot 'tests\SmokeTests.cs'))
    $testOptions = $windowReferences + @($jsonReference, '/main:WaitingGaming.SmokeTests')
    Invoke-CSharpCompiler -OutputPath $testExecutable -Target exe -SourceFiles $testSources -Options $testOptions
    & $testExecutable
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke tests failed (exit $LASTEXITCODE)."
    }
}

[void][System.IO.Directory]::CreateDirectory($binaryDirectory)
[void][System.IO.Directory]::CreateDirectory($catalogDirectory)
$packageRuntimeDirectory = Join-Path $packageDirectory 'bin'
[void][System.IO.Directory]::CreateDirectory($packageRuntimeDirectory)
# Repo marketplaces install the published bundle without running a source build.
Copy-Item -LiteralPath $gameExecutable, $hookExecutable -Destination $packageRuntimeDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE'), (Join-Path $PSScriptRoot 'SECURITY.md') -Destination $packageDirectory -Force
Get-ChildItem -LiteralPath $packageDirectory -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $pluginDirectory -Recurse -Force
}
# A portable manifest left by an earlier build would take precedence over the Codex manifest.
[System.IO.File]::Delete((Join-Path $pluginDirectory 'plugin.json'))
Copy-Item -LiteralPath $gameExecutable, $hookExecutable -Destination $binaryDirectory -Force

$catalog = [ordered]@{
    name = 'waiting-gaming-local'
    interface = @{ displayName = 'Waiting Gaming Local' }
    plugins = @(
        [ordered]@{
            name = 'waiting-gaming'
            source = [ordered]@{ source = 'local'; path = './plugins/waiting-gaming' }
            policy = [ordered]@{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }
            category = 'Productivity'
        }
    )
}
$catalogPath = Join-Path $catalogDirectory 'marketplace.json'
[System.IO.File]::WriteAllText($catalogPath, ($catalog | ConvertTo-Json -Depth 6), [System.Text.UTF8Encoding]::new($false))

Write-Output "Local marketplace: $marketplaceDirectory"
Write-Output "Game preview: $(Join-Path $binaryDirectory 'WaitingGaming.exe') --preview"
