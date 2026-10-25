param(
    [string]$BaselineSource,
    [Parameter(Mandatory = $true)][string]$CandidateDirectory,
    [ValidateRange(1, 20)][int]$Repeats = 3
)
$ErrorActionPreference = 'Stop'
if ($BaselineSource) { $BaselineSource = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BaselineSource) }
$CandidateDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CandidateDirectory)
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Microsoft .NET Framework 4.x compiler is required.' }
if ($BaselineSource -and -not (Test-Path -LiteralPath $BaselineSource -PathType Leaf)) { throw 'Baseline source does not exist.' }
if (-not (Test-Path -LiteralPath $CandidateDirectory -PathType Container)) { throw 'Candidate directory does not exist.' }
# This console benchmark only reads candidate files. It never constructs a form,
# process host, native runtime or job. Do not use its numbers as full startup times.
$benchmarkDirectory = Join-Path $PSScriptRoot 'benchmark-build'
New-Item -ItemType Directory -Path $benchmarkDirectory -Force | Out-Null
if ($BaselineSource) { Copy-Item -LiteralPath $BaselineSource -Destination (Join-Path $benchmarkDirectory 'Baseline.cs') -Force }
foreach ($name in @('CompatController.cs', 'Benchmark.cs', 'candidate-allowlist.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $benchmarkDirectory $name) -Force
}
Push-Location -LiteralPath $benchmarkDirectory
try {
    if ($BaselineSource) {
        & $compilerPath /nologo /target:exe /platform:x64 /optimize+ /warn:4 /warnaserror+ /main:MiuiCompatExperiment.Benchmark /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Core.dll /resource:candidate-allowlist.json,CandidateAllowlist.json /out:Benchmark-baseline.exe Baseline.cs Benchmark.cs
        if ($LASTEXITCODE -ne 0) { throw "Baseline benchmark compilation failed: $LASTEXITCODE" }
    }
    & $compilerPath /nologo /target:exe /platform:x64 /optimize+ /warn:4 /warnaserror+ /main:MiuiCompatExperiment.Benchmark /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Core.dll /resource:candidate-allowlist.json,CandidateAllowlist.json /out:Benchmark-optimized.exe CompatController.cs Benchmark.cs
    if ($LASTEXITCODE -ne 0) { throw "Optimized benchmark compilation failed: $LASTEXITCODE" }
    if ($BaselineSource) {
        & (Join-Path $benchmarkDirectory 'Benchmark-baseline.exe') $CandidateDirectory benchmark-baseline.json $Repeats
        if ($LASTEXITCODE -ne 0) { throw 'Baseline validation benchmark failed.' }
    }
    & (Join-Path $benchmarkDirectory 'Benchmark-optimized.exe') $CandidateDirectory benchmark-optimized.json $Repeats
    if ($LASTEXITCODE -ne 0) { throw 'Optimized validation benchmark failed.' }
} finally { Pop-Location }
