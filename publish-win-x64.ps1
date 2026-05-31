$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'PedalNudge.Windows.csproj'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'

if (-not (Test-Path $dotnet)) {
  $dotnet = 'dotnet'
}

Write-Host "Using .NET CLI: $dotnet"
Write-Host 'Restoring packages from nuget.org...'
& $dotnet restore $project --source https://api.nuget.org/v3/index.json

Write-Host 'Publishing self-contained Windows x64 app...'
& $dotnet publish $project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:PublishTrimmed=false `
  --source https://api.nuget.org/v3/index.json

$exe = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish\PedalNudge.Windows.exe'
Write-Host ''
Write-Host "Done. EXE should be here: $exe"
