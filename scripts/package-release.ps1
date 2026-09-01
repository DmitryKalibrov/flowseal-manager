param(
    [string]$OutputDirectory = 'release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
$expectedRoot = $projectRoot + [System.IO.Path]::DirectorySeparatorChar

if (-not $outputRoot.StartsWith($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must stay inside the project directory.'
}

[xml]$versionProps = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props')
$releaseVersion = [string]$versionProps.Project.PropertyGroup.ReleaseVersion
$buildVersion = [string]$versionProps.Project.PropertyGroup.BuildVersion
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'ReleaseVersion must use X.Y.Z.' }
if ($buildVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'BuildVersion must use X.Y.Z.W.' }

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot | Out-Null

dotnet run --project (Join-Path $projectRoot 'tests\FlowsealManager.Core.Tests\FlowsealManager.Core.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }

$packages = @()
$checksums = @()
foreach ($runtimeIdentifier in @('win-x64', 'win-arm64')) {
    $publishRoot = Join-Path $outputRoot "publish-$runtimeIdentifier"
    dotnet publish (Join-Path $projectRoot 'src\FlowsealManager.App\FlowsealManager.App.csproj') `
        -c Release `
        -r $runtimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $runtimeIdentifier." }

    $executable = Join-Path $publishRoot 'FlowsealManager.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw "FlowsealManager.exe is missing for $runtimeIdentifier." }
    $fileVersion = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
    if ($fileVersion -ne $buildVersion) {
        throw "Built file version $fileVersion does not match BuildVersion $buildVersion."
    }
    $assetName = "FlowsealManager-$runtimeIdentifier.zip"
    $assetPath = Join-Path $outputRoot $assetName
    Compress-Archive -LiteralPath $executable -DestinationPath $assetPath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item -LiteralPath $assetPath).Length
    $packages += [ordered]@{
        runtimeIdentifier = $runtimeIdentifier
        assetName = $assetName
        sha256 = $hash
        size = $size
        executable = 'FlowsealManager.exe'
    }
    $checksums += "$hash  $assetName"
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

$manifest = [ordered]@{
    schemaVersion = 1
    releaseVersion = $releaseVersion
    buildVersion = $buildVersion
    packages = $packages
}
$manifestPath = Join-Path $outputRoot 'update-manifest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksums += "$manifestHash  update-manifest.json"
$checksums | Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding ascii

Write-Host "Prepared Flowseal Manager v$releaseVersion (build $buildVersion) in $outputRoot" -ForegroundColor Green
