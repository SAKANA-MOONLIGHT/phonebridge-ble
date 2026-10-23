$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $root '.build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$archivePath = Join-Path $buildDir 'sdk-contracts.zip'
$expected = 'DBEBA07806DACDB38A8F6E4677E2B06C52D79AEAB58E0F5B38191BD41314E416'
if (-not (Test-Path -LiteralPath $archivePath)) {
    Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.contracts/10.0.22621.2428/microsoft.windows.sdk.contracts.10.0.22621.2428.nupkg' -OutFile $archivePath
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expected) { throw 'SDK package checksum mismatch.' }
Expand-Archive -LiteralPath $archivePath -DestinationPath (Join-Path $buildDir 'sdk-contracts') -Force
Write-Output 'Official Microsoft Windows SDK contracts are ready in .build/sdk-contracts.'
