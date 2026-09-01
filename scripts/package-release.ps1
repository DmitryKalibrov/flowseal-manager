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
}

$isccCandidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 is required. Install JRSoftware.InnoSetup and run packaging again.'
}

& $iscc `
    "/DReleaseVersion=$releaseVersion" `
    "/DBuildVersion=$buildVersion" `
    "/DSourceRoot=$outputRoot" `
    "/DOutputRoot=$outputRoot" `
    (Join-Path $projectRoot 'installer\FlowsealManager.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

foreach ($runtimeIdentifier in @('win-x64', 'win-arm64')) {
    Remove-Item -LiteralPath (Join-Path $outputRoot "publish-$runtimeIdentifier") -Recurse -Force
}

$installer = Join-Path $outputRoot 'FlowsealManager-Setup.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw 'FlowsealManager-Setup.exe is missing.' }
$installerVersion = (Get-Item -LiteralPath $installer).VersionInfo.FileVersion.Trim()
if ($installerVersion -ne $buildVersion) {
    throw "Installer file version $installerVersion does not match BuildVersion $buildVersion."
}
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $installer).Length

Write-Host "Prepared Flowseal Manager v$releaseVersion (build $buildVersion) in $outputRoot" -ForegroundColor Green
Write-Host "FlowsealManager-Setup.exe: $size bytes, SHA-256 $hash" -ForegroundColor Green
