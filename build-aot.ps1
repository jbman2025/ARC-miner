# Build an AOT (single-arch) SYCL ARC-miner with the 2026.0 toolkit.
# Sets up a full link environment: MSVC (vcvars64) + oneAPI 2026.0 libs/bin,
# then calls build.ps1 with the ocloc device name (bmg-g21 / bmg-g31 / acm-g10).
param([string]$SyclArch = '', [Parameter(Mandatory)][string]$Out)

$ErrorActionPreference = 'Stop'
$oneapi = 'C:\Program Files (x86)\Intel\oneAPI'
$ver = '2026.0'

# Import the MSVC build environment (LIB/INCLUDE/PATH) into this session.
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath  = & $vswhere -latest -property installationPath
$vcvars  = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^(.*?)=(.*)$') { Set-Item "env:$($matches[1])" $matches[2] }
}

# Layer oneAPI 2026.0 on top so build.ps1 picks this icpx/ocloc and the linker
# finds libmmd.lib.
$env:PATH = "$oneapi\compiler\$ver\bin;$oneapi\ocloc\$ver\bin;$env:PATH"
$env:LIB  = "$oneapi\compiler\$ver\lib;$env:LIB"

Write-Host "icpx : $((Get-Command icpx).Source)"
Write-Host "ocloc: $((Get-Command ocloc).Source)"
if ($SyclArch) { & "$PSScriptRoot\build.ps1" -Backend sycl -SyclArch $SyclArch -Out $Out }
else           { & "$PSScriptRoot\build.ps1" -Backend sycl -Out $Out }
exit $LASTEXITCODE
