param([string]$CandidateDirectory = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'app'))
$ErrorActionPreference = 'Stop'
$CandidateDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CandidateDirectory)
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testPath = Join-Path $PSScriptRoot 'OfflineTests.exe'
Push-Location -LiteralPath $PSScriptRoot
try {
    & $compilerPath /nologo /target:exe /platform:x64 /optimize+ /warn:4 /warnaserror+ /main:MiuiCompatExperiment.OfflineTests /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Core.dll /resource:candidate-allowlist.json,CandidateAllowlist.json /out:OfflineTests.exe CompatController.cs OfflineTests.cs
    if ($LASTEXITCODE -ne 0) { throw "离线测试编译失败：$LASTEXITCODE" }
    & $testPath $CandidateDirectory
    if ($LASTEXITCODE -ne 0) { throw '离线回归检查失败，见 offline-regressions.json。' }
} finally { Pop-Location }
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'offline-regressions.json') -Encoding UTF8 -Raw
