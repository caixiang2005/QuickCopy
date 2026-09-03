$ErrorActionPreference = 'Stop'

$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$obj = Join-Path $project 'obj'
$bin = Join-Path $project 'bin'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$msbuild = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'

New-Item -ItemType Directory -Force -Path $obj, $bin | Out-Null

& $msbuild (Join-Path $project 'QuickCopy.csproj') /t:Rebuild /p:Configuration=Release /p:Platform=x64 /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$release = Join-Path $bin 'Release'
$extraFiles = @(
    'mscorlib.dll', 'normidna.nlp', 'normnfc.nlp', 'normnfd.nlp',
    'normnfkc.nlp', 'normnfkd.nlp'
)
foreach ($name in $extraFiles) {
    $path = Join-Path $release $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
$cultureFolder = Join-Path $release 'zh-Hans'
if (Test-Path -LiteralPath $cultureFolder) {
    $resolvedRelease = (Resolve-Path -LiteralPath $release).Path
    $resolvedCulture = (Resolve-Path -LiteralPath $cultureFolder).Path
    if (-not $resolvedCulture.StartsWith($resolvedRelease + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected build output path: $resolvedCulture"
    }
    Remove-Item -LiteralPath $resolvedCulture -Recurse -Force
}

Write-Host "Built: $bin\Release\QuickCopy.exe"
