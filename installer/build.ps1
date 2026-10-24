<#
  Builds publish\GhostyNav-Setup.exe: GhostyNav (framework-dependent, so the download stays small)
  zipped and appended to the installer, which runs on the .NET Framework built into Windows.

  powershell -ExecutionPolicy Bypass -File installer\build.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'publish'
$app = Join-Path $out 'app'
$version = ([xml](Get-Content (Join-Path $root 'GhostyNav.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root 'GhostyNav.csproj') -c Release -r win-x64 --self-contained false -p:DebugType=none -o $app --nologo
if ($LASTEXITCODE) { throw 'Publishing GhostyNav failed' }
dotnet build (Join-Path $PSScriptRoot 'Installer.csproj') -c Release "-p:Version=$version" -o (Join-Path $out 'setup') --nologo
if ($LASTEXITCODE) { throw 'Building the installer failed' }

# On its own, the installer exe is the uninstaller
$setup = Join-Path $out 'setup\GhostyNav-Setup.exe'
Copy-Item $setup (Join-Path $app 'uninstall.exe')

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $out 'app.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

# GhostyNav-Setup.exe = installer + zip + the zip's length (8 bytes) + "GHOSTYZ1"
$final = Join-Path $out 'GhostyNav-Setup.exe'
Copy-Item $setup $final
$bytes = [IO.File]::ReadAllBytes($zip)
$stream = [IO.File]::Open($final, [IO.FileMode]::Append)
try {
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Write([BitConverter]::GetBytes([long]$bytes.Length), 0, 8)
    $stream.Write([Text.Encoding]::ASCII.GetBytes('GHOSTYZ1'), 0, 8)
}
finally { $stream.Dispose() }

'Built {0} ({1:N1} MB), GhostyNav {2}' -f $final, ((Get-Item $final).Length / 1MB), $version
