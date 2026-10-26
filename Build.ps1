param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.build'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.build\packages'
$outputPath = Join-Path $taskRoot 'artifacts\CPUX-win-x64'
$publishArgs = @('publish', (Join-Path $taskRoot 'CPUX.csproj'), '-c', 'Release', '-o', $outputPath, '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(), '-p:UseSharedCompilation=false')
if ($SelfContained) { $publishArgs += @('-r', 'win-x64', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "CPUX build failed: $LASTEXITCODE" }
Write-Host "Ready: $outputPath\CPUX.exe"
