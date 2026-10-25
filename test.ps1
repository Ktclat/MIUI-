param([string]$PythonExecutable = 'python', [string]$CandidateDirectory)
$ErrorActionPreference = 'Stop'
if ($CandidateDirectory) {
    $CandidateDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CandidateDirectory)
}
Push-Location -LiteralPath $PSScriptRoot
try {
    & $PythonExecutable -m unittest discover -s tests -v
    if ($LASTEXITCODE -ne 0) { throw 'Python source tests failed.' }
    & (Join-Path $PSScriptRoot 'src/controller/test-pure.ps1')
    if ($CandidateDirectory) {
        & (Join-Path $PSScriptRoot 'src/controller/test-offline.ps1') -CandidateDirectory $CandidateDirectory
    }
    Write-Output 'All requested offline tests passed; no vendor or GUI execution.'
} finally { Pop-Location }
