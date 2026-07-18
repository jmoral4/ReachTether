[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $DeployArguments
)

$project = Join-Path $PSScriptRoot "../dotNet/tools/ReachTether.Deploy/ReachTether.Deploy.csproj"
$buildOutput = dotnet build $project -c Release --nologo 2>&1
$buildExitCode = $LASTEXITCODE
if ($buildExitCode -ne 0) {
    $buildOutput | ForEach-Object { Write-Host $_ }
    exit $buildExitCode
}

$deployDll = Join-Path $PSScriptRoot "../dotNet/tools/ReachTether.Deploy/bin/Release/net9.0/ReachTether.Deploy.dll"
dotnet $deployDll @DeployArguments
exit $LASTEXITCODE
