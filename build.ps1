param([switch]$SkipTests, [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$OutputName = 'GuitarNTE-win-x64')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build GuitarNTE.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if (-not $SkipTests) {
        dotnet run --project tests/GuitarNTE.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    $publishDirectory = Join-Path $PSScriptRoot ('artifacts\' + $OutputName)
    if ((Test-Path -LiteralPath $publishDirectory) -and (Get-ChildItem -LiteralPath $publishDirectory -Force | Select-Object -First 1)) {
        throw 'Output directory is not empty. Move the previous output or choose a new -OutputName to avoid packaging stale files.'
    }
    dotnet publish src/GuitarNTE.App/GuitarNTE.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $oldMacLibrary = Join-Path $publishDirectory 'Melanchall_DryWetMidi_Native64.dylib'
    if (Test-Path -LiteralPath $oldMacLibrary) { Remove-Item -LiteralPath $oldMacLibrary }
    Copy-Item -LiteralPath README.md,LICENSE,THIRD-PARTY-NOTICES.md,VALIDATION.md,CHANGELOG.md -Destination $publishDirectory -Force
    Copy-Item -LiteralPath docs -Destination $publishDirectory -Recurse -Force
    Copy-Item -LiteralPath examples -Destination $publishDirectory -Recurse -Force
    $runtimeRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.netcore.app.runtime.win-x64'
    $runtimePackage = Get-ChildItem -LiteralPath $runtimeRoot -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    Copy-Item -LiteralPath (Join-Path $runtimePackage.FullName 'LICENSE.TXT') -Destination (Join-Path $publishDirectory 'DOTNET-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $runtimePackage.FullName 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $publishDirectory 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force
    $desktopRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsdesktop.app.runtime.win-x64'
    $desktopPackage = Get-ChildItem -LiteralPath $desktopRoot -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    Copy-Item -LiteralPath (Join-Path $desktopPackage.FullName 'LICENSE') -Destination (Join-Path $publishDirectory 'WPF-LICENSE.txt') -Force
    $archivePath = Join-Path $PSScriptRoot ('artifacts\' + $OutputName + '.zip')
    Compress-Archive -LiteralPath $publishDirectory -DestinationPath $archivePath -Force
    $hashes = @(
        '{0}  {1}.zip' -f (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant(), $OutputName
        '{0}  {1}/GuitarNTE.exe' -f (Get-FileHash -LiteralPath (Join-Path $publishDirectory 'GuitarNTE.exe') -Algorithm SHA256).Hash.ToLowerInvariant(), $OutputName
    )
    $hashes | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts/SHA256SUMS.txt') -Encoding ascii
    $hashes | Write-Output
    Write-Output "Published: $publishDirectory"
    Write-Output "Archive: $archivePath"
} finally { Pop-Location }
