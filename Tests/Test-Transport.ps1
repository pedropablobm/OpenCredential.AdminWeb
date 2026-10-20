param([string]$RuntimeImage = 'mcr.microsoft.com/dotnet/aspnet:10.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $env:LOCALAPPDATA ('Temp\opencode\oc-transport-' + [Guid]::NewGuid().ToString('N'))
$name = 'opencredential-transport-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
& dotnet publish (Join-Path $PSScriptRoot 'RegressionTests.csproj') -c Release --no-self-contained -p:UseAppHost=false -o $output --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Transport regression build failed.' }
& docker run --name $name --network none --read-only --tmpfs '/tmp:rw,size=64m' --memory 256m --cpus 1 --mount ('type=bind,source=' + $output + ',target=/tests,readonly') --entrypoint dotnet $RuntimeImage /tests/RegressionTests.dll --transport-only
if ($LASTEXITCODE -ne 0) { throw "Transport regression failed in disposable container $name." }
Write-Output "PASS Transport container: $name (stopped; no existing service or host certificate store changed)."
# No automatic container/image/volume removal. Test CA and private keys exist only
# in the disposable container's tmpfs, never in the repository or host trust store.
