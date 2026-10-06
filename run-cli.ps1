<#
.SYNOPSIS
    Compiles atlascli from source and runs it, without a release build or packaging.

.DESCRIPTION
    Builds the command line in the Debug configuration and runs it with any arguments
    given. Only build errors are shown, so the output is the CLI's own and is safe to pipe
    or to use as an MCP server. It runs in the current directory, so relative paths such
    as scan --out before.json land where you are. The app can stay open while this builds.
    Exits with the CLI's exit code. Requires the .NET 10 SDK.

.EXAMPLE
    .\run-cli.ps1 issues
    Lists every USB issue on this PC with what to do about it.

.EXAMPLE
    .\run-cli.ps1 show H01/02 --json
    Shows everything about one node as JSON.

.EXAMPLE
    .\run-cli.ps1 watch --for 30s
    Reports devices connecting and disconnecting for 30 seconds.

.EXAMPLE
    .\run-cli.ps1 help
    Lists every command and option.
#>

$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK is required: https://dotnet.microsoft.com/download' }
$project = Join-Path $PSScriptRoot 'src\UsbAtlas.Cli\UsbAtlas.Cli.csproj'
$exe = Join-Path $PSScriptRoot 'artifacts\bin\UsbAtlas.Cli\debug\atlascli.exe'

# Build output would mix with the CLI's, so it's shown only when the build fails.
$build = dotnet build $project -c Debug --nologo -v quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    $build | Write-Host
    Write-Error 'atlascli failed to build.' -ErrorAction Continue
    exit 1
}

& $exe @args
exit $LASTEXITCODE
