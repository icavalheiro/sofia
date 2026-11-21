# Spec 044 verification server: a throwaway Sofia.Server with its own SOFIA_HOME and an explicit UI folder.
# Never touches the user's server on :5199 or the real ~/.sofia data directory.
param(
    [string]$UiPath = "D:\repos\opensource\sofia\src\Sofia.App.Browser\bin\Release\net10.0-browser1.0\browser-wasm\AppBundle",
    [int]$Port = 5301,
    [string]$Bind = "127.0.0.1",
    [string]$HomeDir = "$env:TEMP\sofia-044\home"
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $HomeDir | Out-Null

$env:SOFIA_HOME = $HomeDir
$env:SOFIA_URLS = "http://${Bind}:$Port"
$env:Sofia__UiPath = $UiPath
$env:DOTNET_ENVIRONMENT = 'Production'

Write-Host "SOFIA_HOME=$env:SOFIA_HOME"
Write-Host "SOFIA_URLS=$env:SOFIA_URLS"
Write-Host "Sofia__UiPath=$env:Sofia__UiPath"

Set-Location 'D:\repos\opensource\sofia'
dotnet run --project src/Sofia.Server --no-build
