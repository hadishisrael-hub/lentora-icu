<#
.SYNOPSIS
  Builds the Lentora ICU installer: self-contained publish + the official unmodified ArgyllCMS + Inno Setup.
.EXAMPLE
  pwsh scripts/package.ps1 -ArgyllZip C:\path\Argyll_V3.5.0_win64_exe.zip
#>
param(
    [Parameter(Mandatory)] [string] $ArgyllZip,
    [string] $Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'PrinterColorProfiler\PrinterColorProfiler.csproj'
[xml]$csproj = Get-Content $project
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$commit = (git -C $root rev-parse --short HEAD).Trim()
$dirty = if (git -C $root status --porcelain) { '+local-changes' } else { '' }
$stage = Join-Path $root "build\publish-$version"
$out = Join-Path $root 'dist-local'

if (-not (Test-Path $ArgyllZip)) { throw "ArgyllCMS archive not found: $ArgyllZip" }
if (-not (Test-Path $Iscc)) { throw "Inno Setup compiler not found: $Iscc" }
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "Publishing Lentora ICU $version ($commit$dirty)"
dotnet publish $project -c Release -r win-x64 --self-contained true -o $stage --configfile (Join-Path $root 'NuGet.Config') -p:DebugType=None
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# ArgyllCMS is shipped exactly as released upstream, next to the exe (AGPL-3.0, see THIRD-PARTY-NOTICES.md).
Expand-Archive -Path $ArgyllZip -DestinationPath $stage
$argyllHash = (Get-FileHash $ArgyllZip -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not (Test-Path (Join-Path $stage 'Argyll_V3.5.0\bin\colprof.exe'))) { throw 'colprof.exe missing after extracting ArgyllCMS' }

# The packaged app must find its own Argyll and calibration chart.
$exe = Join-Path $stage 'PrinterColorProfiler.exe'
$env:PRINTER_PROFILER_ARGYLL = $null
foreach ($check in '--check-argyll', '--check-chart') {
    $p = Start-Process $exe -ArgumentList $check -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Packaged app failed $check (exit $($p.ExitCode))" }
}

@"
Lentora ICU $version
source commit: $commit$dirty
ArgyllCMS archive: $(Split-Path $ArgyllZip -Leaf) sha256 $argyllHash
ArgyllCMS source: https://www.argyllcms.com/Argyll_V3.5.0_src.zip
"@ | Set-Content (Join-Path $stage 'BUILD-INFO.txt') -Encoding utf8

& $Iscc /Q "/DAppVersion=$version" "/DSourceDir=$stage" "/DOutputDir=$out" (Join-Path $root 'installer\LentoraICU.iss')
if ($LASTEXITCODE) { throw 'Inno Setup failed' }
$setup = Join-Path $out "LentoraICU-Setup-$version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $setup -Leaf)" | Set-Content (Join-Path $out "LentoraICU-Setup-$version.sha256") -Encoding ascii
Write-Host "Installer: $setup"
Write-Host "SHA-256:   $hash"
Write-Host "Size:      $([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB"
