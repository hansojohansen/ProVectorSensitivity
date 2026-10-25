<#
.SYNOPSIS
  OpenVector RESEARCH build of Raw Accel 1.7.1 + angle x speed gain table.
  RESEARCH ONLY: the result is unofficial and at most TEST-SIGNED. Never use it with
  anticheat-protected games. See README.md and GUIDE.md next to this script.

.DESCRIPTION
  1. Restores the WDK + Windows SDK NuGet packages and Newtonsoft.Json into rawaccel\packages.
  2. Builds rawaccel.sln (driver, installer, uninstaller, wrapper, writer), Release|x64.
  3. Collects the outputs into -OutDir in the layout installer.exe expects (driver\rawaccel.sys).
  4. With -TestSign: creates a throw-away self-signed code-signing certificate (private key
     non-exportable, deleted at the end), test-signs driver\rawaccel.sys and exports only the
     public .cer next to it.

  Requirements: Windows, Visual Studio 2022 (17.x) with "Desktop development with C++",
  "C++/CLI support (v143)", ".NET Framework 4.7.2 targeting pack" and, unless -NoSpectre,
  "MSVC v143 Spectre-mitigated libs". Network access to nuget.org. Used by
  .github/workflows/research-driver.yml; also runnable on your own PC.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File research\rawaccel-angular\build.ps1 -TestSign
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'out\RESEARCH-ONLY-test-signed'),
    [switch]$TestSign,
    [switch]$NoSpectre,
    [string]$CertSubject = 'CN=OpenVector RESEARCH test signing - NOT TRUSTED - NOT FOR GAMES'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$root = Join-Path $PSScriptRoot 'rawaccel'
$packages = Join-Path $root 'packages'

function Invoke-Checked {
    param([string]$Exe, [string[]]$Arguments)
    Write-Host ">> $Exe $($Arguments -join ' ')"
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed with exit code $LASTEXITCODE" }
}

# --- tools -------------------------------------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found: install Visual Studio 2022' }
$msbuild = & $vswhere -latest -products * -version '[17.0,18.0)' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild from Visual Studio 2022 (v143 toolset) not found' }
Write-Host "MSBuild: $msbuild"

$nuget = (Get-Command nuget.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $nuget) {
    $nuget = Join-Path $PSScriptRoot 'out\tools\nuget.exe'
    if (-not (Test-Path $nuget)) {
        New-Item -ItemType Directory -Force (Split-Path $nuget) | Out-Null
        Write-Host 'Downloading nuget.exe'
        Invoke-WebRequest 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nuget -UseBasicParsing
    }
}
Write-Host "NuGet: $nuget"

# --- restore -----------------------------------------------------------------------------
Invoke-Checked $nuget @('restore', (Join-Path $root 'build\packages.config'), '-PackagesDirectory', $packages, '-NonInteractive')
Invoke-Checked $nuget @('restore', (Join-Path $root 'writer\packages.config'), '-PackagesDirectory', $packages, '-NonInteractive')

$wdkProps = Get-ChildItem $packages -Filter 'Microsoft.Windows.WDK.x64.props' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $wdkProps) { throw "WDK NuGet props not found under $packages (version mismatch with Directory.Build.props?)" }
Write-Host "WDK: $($wdkProps.FullName)"

# --- build -------------------------------------------------------------------------------
$msbuildArgs = @(
    (Join-Path $root 'rawaccel.sln'),
    '/m', '/nologo', '/v:minimal',
    '/p:Configuration=Release', '/p:Platform=x64',
    '/p:SignMode=Off'            # we test-sign explicitly below, never with the WDK auto test cert
)
if ($NoSpectre) { $msbuildArgs += @('/p:SpectreMitigation=false', '/p:Driver_SpectreMitigation=false') }
New-Item -ItemType Directory -Force (Join-Path $root 'signed') | Out-Null   # upstream post-build steps copy into it
Invoke-Checked $msbuild $msbuildArgs

# --- collect -----------------------------------------------------------------------------
$bin = Join-Path $root 'x64\Release'
function Find-One([string]$name) {
    $hit = Get-ChildItem $bin -Filter $name -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } | Select-Object -First 1
    if (-not $hit) { throw "build output $name not found under $bin" }
    return $hit.FullName
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $OutDir 'driver') | Out-Null
Copy-Item (Find-One 'rawaccel.sys') (Join-Path $OutDir 'driver\rawaccel.sys')
foreach ($f in 'installer.exe', 'uninstaller.exe', 'writer.exe', 'writer.exe.config', 'wrapper.dll', 'Newtonsoft.Json.dll') {
    Copy-Item (Find-One $f) (Join-Path $OutDir $f)
}
New-Item -ItemType Directory -Force (Join-Path $OutDir 'tools') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'tools\make_angular_table.py') (Join-Path $OutDir 'tools')
Copy-Item (Join-Path $PSScriptRoot 'GUIDE.md') $OutDir
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $OutDir 'LICENSE-RawAccel-MIT.txt')

# --- test-sign ---------------------------------------------------------------------------
$signed = $false
if ($TestSign) {
    $signtool = @(
        Get-ChildItem (Join-Path $packages 'Microsoft.Windows.SDK.CPP.*') -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue
        Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue
    ) | Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe (x64) not found in the SDK NuGet package or Windows Kits' }
    Write-Host "signtool: $($signtool.FullName)"

    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $CertSubject `
        -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(1)
    try {
        Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir 'OpenVector-RESEARCH-test-cert.cer') | Out-Null
        # No timestamp: test-mode code integrity does not need one and it avoids a network dependency.
        Invoke-Checked $signtool.FullName @('sign', '/v', '/fd', 'SHA256', '/s', 'My', '/sha1', $cert.Thumbprint,
            (Join-Path $OutDir 'driver\rawaccel.sys'))
        $sig = Get-AuthenticodeSignature (Join-Path $OutDir 'driver\rawaccel.sys')
        if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
            throw 'rawaccel.sys is not signed by the test certificate'
        }
        # Status is expected to be UnknownError / NotTrusted: the root is deliberately not trusted anywhere.
        Write-Host "Signature on rawaccel.sys: signer $($sig.SignerCertificate.Subject), status $($sig.Status) (untrusted root is expected)"
        $signed = $true
    }
    finally {
        # private key never leaves this machine and is deleted now
        Remove-Item (Join-Path 'Cert:\CurrentUser\My' $cert.Thumbprint) -DeleteKey -ErrorAction SilentlyContinue
    }
}

$state = if ($signed) { 'TEST-SIGNED with a throw-away self-signed certificate (OpenVector-RESEARCH-test-cert.cer)' } else { 'UNSIGNED (rawaccel.sys will not load until you test-sign it)' }
@"
==========================================================================
 RESEARCH ONLY - OpenVector fork of Raw Accel 1.7.1 (angle x speed gain table)
==========================================================================
 This is NOT official Raw Accel and NOT an OpenVector release.
 driver\rawaccel.sys: $state
 It loads only with Windows test-signing mode ON (bcdedit /set testsigning on),
 which usually means Secure Boot OFF.

 NEVER play anticheat-protected games (Valorant/Vanguard, FACEIT, ESEA, EAC,
 BattlEye, Ricochet, EA Javelin, ...) while this driver is installed or
 test-signing is on. Many refuse to start; some may flag your account.

 Read GUIDE.md before doing anything. Rollback = official Raw Accel installer,
 reboot, testsigning off, Secure Boot on, BitLocker resume (GUIDE.md section F).

 Built from: research/rawaccel-angular (upstream 53a721345617a1e29f3a16750cbdf807040cf44e
 + patches/0001-openvector-angular-gain.patch). Raw Accel is MIT, see LICENSE-RawAccel-MIT.txt.
 Driver version reported to user mode: 1.7.900 (research ABI).
"@ | Set-Content -Encoding UTF8 (Join-Path $OutDir 'README-RESEARCH-ONLY.txt')

$sums = Get-ChildItem $OutDir -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
    $rel = $_.FullName.Substring($OutDir.Length).TrimStart('\')
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $rel
}
$sums | Set-Content -Encoding ASCII (Join-Path $OutDir 'SHA256SUMS.txt')

Write-Host ''
Write-Host "Done: $OutDir"
Write-Host $state
