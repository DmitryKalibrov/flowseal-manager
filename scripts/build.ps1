param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "dist\$RuntimeIdentifier"))
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'dist')) + [System.IO.Path]::DirectorySeparatorChar

if (-not $publishRoot.StartsWith($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe publish path.'
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

dotnet run --project (Join-Path $projectRoot 'tests\FlowsealManager.Core.Tests\FlowsealManager.Core.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }

dotnet publish (Join-Path $projectRoot 'src\FlowsealManager.App\FlowsealManager.App.csproj') `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -o $publishRoot

if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Published to $publishRoot" -ForegroundColor Green
