param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.x compiler not found.' }
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.dll','System.Runtime.InteropServices.WindowsRuntime.dll','System.Runtime.WindowsRuntime.dll')
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:' + (Join-Path $out 'PhoneBridge.exe')),('/win32manifest:' + (Join-Path $root 'windows\app.manifest')))
foreach ($reference in $references) { $arguments += '/reference:' + (Join-Path $framework $reference) }
$contracts = Join-Path $root '.build\sdk-contracts\ref\netstandard2.0'
if (-not (Test-Path -LiteralPath (Join-Path $contracts 'Windows.WinMD'))) { throw 'Missing SDK contracts. See scripts\fetch-contracts.ps1.' }
$arguments += Get-ChildItem -LiteralPath $contracts -Filter '*.winmd' | ForEach-Object { '/reference:' + $_.FullName }
$arguments += (Get-ChildItem -LiteralPath (Join-Path $root 'windows') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'windows\PhoneBridge.exe.config') -Destination $out
if ($Test) {
    $process = Start-Process -FilePath (Join-Path $out 'PhoneBridge.exe') -ArgumentList @('--self-test',('"' + (Join-Path $out 'self-test.txt') + '"')) -WindowStyle Hidden -PassThru -Wait
    Get-Content -LiteralPath (Join-Path $out 'self-test.txt')
    if ($process.ExitCode -ne 0) { throw 'Self-test failed.' }
}
Write-Output ('Built: ' + (Join-Path $out 'PhoneBridge.exe'))
