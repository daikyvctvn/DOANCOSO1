$ErrorActionPreference = "Stop"

$root = "C:\Doancoso"
$outLog = Join-Path $root "run-5099.out.log"
$errLog = Join-Path $root "run-5099.err.log"

Remove-Item $outLog, $errLog -Force -ErrorAction SilentlyContinue

$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:APPDATA = Join-Path $root ".appdata"
$env:HOME = $root

New-Item -ItemType Directory -Force -Path (Join-Path $root ".appdata\NuGet") | Out-Null

$process = Start-Process `
    -FilePath "C:\Program Files\dotnet\dotnet.exe" `
    -ArgumentList @("C:\Doancoso\bin\Debug\net10.0\TableOrderWeb.dll", "--urls", "http://127.0.0.1:5099") `
    -WorkingDirectory $root `
    -RedirectStandardOutput $outLog `
    -RedirectStandardError $errLog `
    -PassThru

Write-Output ("STARTED:{0}" -f $process.Id)







