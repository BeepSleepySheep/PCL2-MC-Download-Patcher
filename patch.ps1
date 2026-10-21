param(
    [Parameter(Mandatory = $true)][string]$InputExe,
    [string]$OutputExe
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'PCL2-MC-Download-Patcher.csproj'

dotnet build -c Release --nologo -v q $project
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($OutputExe) {
    dotnet run -c Release --no-build --project $project -- $InputExe $OutputExe
} else {
    dotnet run -c Release --no-build --project $project -- $InputExe
}
exit $LASTEXITCODE
