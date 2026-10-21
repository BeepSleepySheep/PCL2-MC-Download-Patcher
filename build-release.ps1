param(
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot

try {
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -o dist
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $exe = Join-Path $PSScriptRoot 'dist\PCL2-MC-Download-Patcher.exe'
    if (-not (Test-Path $exe)) {
        throw "发布失败，找不到 $exe"
    }

    $name = if ($Version) { "PCL2-MC-Download-Patcher-$Version-win-x64" } else { 'PCL2-MC-Download-Patcher-win-x64' }
    $zip = Join-Path $PSScriptRoot "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Compress-Archive -Path $exe -DestinationPath $zip
    Write-Host ''
    Write-Host "已生成: $exe"
    Write-Host "已打包: $zip"
}
finally {
    Pop-Location
}
