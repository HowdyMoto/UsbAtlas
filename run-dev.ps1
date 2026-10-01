<#
.SYNOPSIS
    Compiles USB Atlas from source and runs it, without a release build or packaging.

.DESCRIPTION
    Builds the current source in the Debug configuration and launches it. Any
    arguments are passed to the app, for example --demo for the sample topology
    or --dark for dark mode. The app runs from artifacts\diagnostics, so files it
    writes (previews, scans, test results) stay out of the source tree.
    Requires the .NET 10 SDK.

.EXAMPLE
    .\run-dev.ps1
    Runs against your USB hardware.

.EXAMPLE
    .\run-dev.ps1 --demo --dark
    Shows the sample topology in dark mode.
#>

$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK is required: https://dotnet.microsoft.com/download' }
$project = Join-Path $PSScriptRoot 'src\UsbAtlas\UsbAtlas.csproj'

Push-Location (New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'artifacts\diagnostics'))
try {
    dotnet run --project $project -c Debug -- @args
    $code = $LASTEXITCODE
}
finally { Pop-Location }
exit $code
