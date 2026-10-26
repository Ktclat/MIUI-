param(
    [string]$MiuiDirectory,
    [string]$RuntimeDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/local'),
    [string]$PythonExecutable = 'python'
)
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$sourceDirectory = Join-Path $projectDirectory 'src/controller'
if ([bool]$MiuiDirectory -ne [bool]$RuntimeDirectory) {
    throw 'Provide both -MiuiDirectory and -RuntimeDirectory, or neither for source-only compilation.'
}
& (Join-Path $sourceDirectory 'build.ps1') -PythonExecutable $PythonExecutable
$compiledPath = Join-Path $sourceDirectory 'MIUI-Compat-Controller.exe'
if (-not (Test-Path -LiteralPath $compiledPath)) { throw 'Compilation did not produce the launcher.' }
if (-not $MiuiDirectory) {
    $sourceOutput = Join-Path $projectDirectory 'artifacts/source'
    New-Item -ItemType Directory -Path $sourceOutput -Force | Out-Null
    Copy-Item -LiteralPath $compiledPath -Destination (Join-Path $sourceOutput 'MIUI-.exe')
    Write-Output ('Compiled project-owned launcher: ' + (Join-Path $sourceOutput 'MIUI-.exe'))
    Write-Output 'No vendor code was built, downloaded or executed. A prepared app directory is required for native features.'
    return
}
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$resolvedMiui = (Resolve-Path -LiteralPath $MiuiDirectory).Path
$resolvedRuntime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
& $PythonExecutable (Join-Path $projectDirectory 'tools/prepare.py') --miui-dir $resolvedMiui --runtime-dir $resolvedRuntime --output $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw 'Local file preparation failed. No application was started.' }
$entrypoint = Join-Path $resolvedOutput 'MIUI-.exe'
Copy-Item -LiteralPath $compiledPath -Destination $entrypoint
Copy-Item -LiteralPath (Join-Path $projectDirectory 'LICENSE') -Destination (Join-Path $resolvedOutput 'LICENSE')
Copy-Item -LiteralPath (Join-Path $projectDirectory 'NOTICE') -Destination (Join-Path $resolvedOutput 'NOTICE')
# The file-only branch returns before Forms, LoadLibrary, jobs, pipes or native startup.
$offlineProcess = Start-Process -FilePath $entrypoint -ArgumentList @('--offline-self-test') -WorkingDirectory $resolvedOutput -WindowStyle Hidden -Wait -PassThru
if ($offlineProcess.ExitCode -ne 0) { throw 'Offline candidate validation failed; inspect self-test.json in the output.' }
Write-Output ('Prepared independent local package: ' + $resolvedOutput)
Write-Output 'No vendor code was executed. Phone behavior still requires manual validation.'
