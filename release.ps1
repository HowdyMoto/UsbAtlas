<#
.SYNOPSIS
    Builds, checks, signs and packages a USB Atlas release.

.DESCRIPTION
    Publishes the self-contained win-x64 package (the app and the command line beside it, each a single .exe
    with the .NET runtime inside, and the license and notice files, the .NET runtime's included), signs the
    two .exes when a certificate is
    configured, verifies every signature, runs both self-tests and the off-screen UI checks from the
    package, zips it, and writes SHA256SUMS.txt under artifacts\releases\v<version>. -Linux adds the
    command line for linux-x64 and linux-arm64 as .tar.gz files.

    Signing uses signtool from the Windows SDK, with SHA-256 and an RFC 3161 timestamp, and one of:
      - a code-signing certificate in the certificate store, by thumbprint
        (-CertificateThumbprint, or USBATLAS_SIGN_THUMBPRINT)
      - Azure Artifact Signing (formerly Trusted Signing), through its signtool dlib and a metadata file
        naming the account and certificate profile (-ArtifactSigningDlib and -ArtifactSigningMetadata,
        or USBATLAS_SIGN_DLIB and USBATLAS_SIGN_METADATA)
    Microsoft's runtime files are already signed by Microsoft and are left as they are. Without signing
    configured the package is still made, with a warning; -RequireSigning makes that an error.

.EXAMPLE
    .\release.ps1 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567 -RequireSigning -Linux

.EXAMPLE
    .\release.ps1 -ArtifactSigningDlib C:\tools\Azure.CodeSigning.Dlib.dll -ArtifactSigningMetadata .\signing.json
#>
[CmdletBinding()]
param(
    [string]$CertificateThumbprint = $env:USBATLAS_SIGN_THUMBPRINT,
    [string]$ArtifactSigningDlib = $env:USBATLAS_SIGN_DLIB,
    [string]$ArtifactSigningMetadata = $env:USBATLAS_SIGN_METADATA,
    [string]$TimestampUrl,
    [switch]$RequireSigning,
    [switch]$Linux,
    [switch]$SkipUiChecks
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }
# Windows' tar can't set Unix permissions, so the archive is written here: the executable 0755, the rest 0644.
function Write-TarGz($dir, $path, $executable) {
    $file = [IO.File]::Create($path)
    try {
        $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
        $writer = [Formats.Tar.TarWriter]::new($gzip, [Formats.Tar.TarEntryFormat]::Pax, $false)
        try {
            foreach ($item in Get-ChildItem $dir -Recurse -File | Sort-Object FullName) {
                $name = [IO.Path]::GetRelativePath($dir, $item.FullName).Replace('\', '/')
                $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $name)
                $entry.Mode = if ($name -eq $executable) { [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute' } else { [IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead' }
                $entry.ModificationTime = $item.LastWriteTimeUtc
                $entry.DataStream = [IO.File]::OpenRead($item.FullName)
                try { $writer.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
            }
        } finally { $writer.Dispose(); $gzip.Dispose() }
    } finally { $file.Dispose() }
}
function Property($project, $name) { ([xml](Get-Content $project)).Project.PropertyGroup | ForEach-Object { $_.$name } | Where-Object { $_ } | Select-Object -First 1 }

$version = Property "$root\src\UsbAtlas\UsbAtlas.csproj" 'Version'
$cli = Property "$root\src\UsbAtlas.Cli\UsbAtlas.Cli.csproj" 'AssemblyName'
if ($cli -ieq 'UsbAtlas') { throw "The command line's assembly name, $cli, is the app's name in another case; Windows file names ignore case, so one would overwrite the other in the package." }
$out = Join-Path $root "artifacts\releases\v$version"
$stage = Join-Path $out 'release-win-x64'
$zip = Join-Path $out "UsbAtlas-$version-win-x64.zip"

# Signing, decided before anything is built so a release that must be signed fails early.
$signing = if ($CertificateThumbprint) { 'certificate' } elseif ($ArtifactSigningDlib -or $ArtifactSigningMetadata) { 'artifact' } else { $null }
if ($signing -eq 'artifact' -and -not ($ArtifactSigningDlib -and $ArtifactSigningMetadata)) { throw 'Azure Artifact Signing needs both -ArtifactSigningDlib and -ArtifactSigningMetadata.' }
if (-not $signing -and $RequireSigning) { throw 'No signing configured: pass -CertificateThumbprint, or -ArtifactSigningDlib and -ArtifactSigningMetadata.' }
if ($signing -eq 'certificate' -and -not (Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object Thumbprint -eq $CertificateThumbprint.Replace(' ', '').ToUpperInvariant())) {
    throw "No certificate with thumbprint $CertificateThumbprint is in the CurrentUser or LocalMachine personal store."
}
$signtool = $null
if ($signing) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'signtool.exe was not found. Install the Windows SDK (its Signing Tools for Desktop Apps).' }
    if (-not $TimestampUrl) { $TimestampUrl = if ($signing -eq 'artifact') { 'http://timestamp.acs.microsoft.com' } else { 'http://timestamp.digicert.com' } }
}

Step "Publishing USB Atlas $version for win-x64"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
# Each program is one .exe with the .NET runtime inside, so the folder shows the app rather than ~400 runtime files.
# WPF's few native DLLs stay beside UsbAtlas.exe rather than being unpacked to %TEMP% on first run, and symbols are
# embedded so stack traces keep their line numbers.
foreach ($project in 'UsbAtlas', 'UsbAtlas.Cli') {
    dotnet publish "$root\src\$project" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=embedded -o $stage --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

# The .NET runtime travels with the app, so its license and notices do too.
Step 'Adding the .NET runtime notices'
$notices = New-Item -ItemType Directory -Force (Join-Path $stage 'RuntimeNotices')
$dotnet = Split-Path (Get-Command dotnet).Source
Copy-Item "$dotnet\LICENSE.txt" "$notices\DotNet-LICENSE.txt"
Copy-Item "$dotnet\ThirdPartyNotices.txt" "$notices\DotNet-THIRD-PARTY-NOTICES.txt"
# The runtime pack's version is in the deps file, which a single-file publish builds into the .exe; the copy it
# was made from stays under obj.
$deps = Get-Content "$root\artifacts\obj\UsbAtlas\release_win-x64\UsbAtlas.deps.json" -Raw | ConvertFrom-Json
$desktop = $deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/*' } | Select-Object -First 1
if (-not $desktop) { throw 'The package has no Windows Desktop runtime pack to take a license from.' }
$desktopVersion = $desktop.Split('/')[1]
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
Copy-Item (Join-Path $nuget "microsoft.windowsdesktop.app.runtime.win-x64\$desktopVersion\LICENSE") "$notices\WindowsDesktop-LICENSE.txt"

# Our DLLs are bundled inside the two .exes, so signing those covers them.
$ours = @('UsbAtlas.exe', "$cli.exe") | ForEach-Object { Join-Path $stage $_ }
if ($signing) {
    Step "Signing $($ours.Count) files ($signing)"
    $how = if ($signing -eq 'certificate') { @('/sha1', $CertificateThumbprint.Replace(' ', '')) } else { @('/dlib', $ArtifactSigningDlib, '/dmdf', $ArtifactSigningMetadata) }
    & $signtool sign /fd SHA256 /tr $TimestampUrl /td SHA256 @how @ours
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
    Step 'Verifying signatures'
    foreach ($file in $ours) {
        & $signtool verify /pa /tw /q $file
        if ($LASTEXITCODE -ne 0) { & $signtool verify /pa /tw /v $file; throw "$(Split-Path $file -Leaf) doesn't verify." }
    }
} else {
    Write-Warning 'This build is UNSIGNED: SmartScreen will warn on first run and some organizations will block it. See .\release.ps1 -? for signing.'
}

Step 'Running the self-tests from the package'
$checks = New-Item -ItemType Directory -Force (Join-Path $out 'checks')
& "$stage\$cli.exe" self-test
if ($LASTEXITCODE -ne 0) { throw "$cli self-test failed." }
function App($arguments, $result) {
    Remove-Item "$checks\$result" -ErrorAction SilentlyContinue
    Start-Process "$stage\UsbAtlas.exe" $arguments -WorkingDirectory $checks -Wait
    $text = Get-Content "$checks\$result" -Raw -ErrorAction SilentlyContinue
    if (-not $text -or -not ($text.StartsWith('All checks passed') -or $text.StartsWith('UI checks passed'))) { throw "UsbAtlas $arguments failed: $text" }
    Write-Host "  UsbAtlas $arguments passed"
}
App '--self-test' 'self-test.txt'
if (-not $SkipUiChecks) {
    foreach ($mode in '', '--compact', '--vertical', '--wide', '--dark') { App "--demo --render --verify-ui $mode".Trim() 'ui-test.txt' }
}

Step "Zipping $(Split-Path $zip -Leaf)"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$packages = @($zip)

if ($Linux) {
    foreach ($rid in 'linux-x64', 'linux-arm64') {
        Step "Publishing the command line for $rid"
        $dir = Join-Path $out "release-$rid"
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        dotnet publish "$root\src\UsbAtlas.Cli" -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o $dir --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Publishing for $rid failed." }
        Copy-Item "$root\LICENSE", "$root\THIRD-PARTY-NOTICES.md" $dir
        Copy-Item "$dotnet\LICENSE.txt" "$dir\DotNet-LICENSE.txt"; Copy-Item "$dotnet\ThirdPartyNotices.txt" "$dir\DotNet-THIRD-PARTY-NOTICES.txt"
        $tar = Join-Path $out "$cli-$version-$rid.tar.gz"
        Write-TarGz $dir $tar $cli
        $packages += $tar
    }
}

Step 'Writing SHA256SUMS.txt'
$packages | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) } | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
Get-Content (Join-Path $out 'SHA256SUMS.txt')
Write-Host "Done: $out$(if (-not $signing) { ' (unsigned)' })" -ForegroundColor Green
