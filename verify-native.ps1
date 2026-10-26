param(
    [string]$GccExecutable = 'gcc',
    [string]$PythonExecutable = 'python',
    [string]$OriginalMediaFile
)
$ErrorActionPreference = 'Stop'
if ($OriginalMediaFile) {
    $OriginalMediaFile = (Resolve-Path -LiteralPath $OriginalMediaFile).Path
}
$nativeOutput = Join-Path $PSScriptRoot 'artifacts/native-tests'
New-Item -ItemType Directory -Path $nativeOutput -Force | Out-Null
$assemblerSource = Join-Path $PSScriptRoot 'tools/background_video.S'
$objectFile = Join-Path $nativeOutput 'background_video.obj'
& $GccExecutable -c -x assembler-with-cpp $assemblerSource -o $objectFile
if ($LASTEXITCODE -ne 0) { throw 'AMD64 Windows assembler compilation failed.' }
& $PythonExecutable (Join-Path $PSScriptRoot 'tools/verify_assembly.py') --object $objectFile
if ($LASTEXITCODE -ne 0) { throw 'Assembler bytes, relocations or unwind metadata differ.' }
$testExecutable = Join-Path $nativeOutput 'helper-test.exe'
& $GccExecutable -O2 -Wall -Wextra -Werror (Join-Path $PSScriptRoot 'native-tests/test_background_helper.c') $assemblerSource -o $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Our stub-only helper test failed to compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Our stub-only helper test failed.' }
& $PythonExecutable (Join-Path $PSScriptRoot 'native-tests/test_telemetry_profile.py')
if ($LASTEXITCODE -ne 0) { throw 'Telemetry profile verification failed.' }
$telemetryTest = Join-Path $nativeOutput 'telemetry-test.exe'
& $GccExecutable -O2 -Wall -Wextra -Werror (Join-Path $PSScriptRoot 'native-tests/test_telemetry_helper.c') (Join-Path $PSScriptRoot 'native-tests/telemetry_probe.S') -o $telemetryTest
if ($LASTEXITCODE -ne 0) { throw 'Telemetry stub compilation failed.' }
& $telemetryTest
if ($LASTEXITCODE -ne 0) { throw 'Telemetry stub checks failed.' }
if ($OriginalMediaFile) {
    & $PythonExecutable (Join-Path $PSScriptRoot 'native-tests/test_background_transform.py') --original-media $OriginalMediaFile --object $objectFile
    if ($LASTEXITCODE -ne 0) { throw 'Read-only native PE checks failed.' }
    & $PythonExecutable -O (Join-Path $PSScriptRoot 'native-tests/test_background_transform.py') --original-media $OriginalMediaFile --object $objectFile
    if ($LASTEXITCODE -ne 0) { throw 'Optimized-Python native PE checks failed.' }
}
Write-Output 'Native helper checks passed. Only our code and in-memory stubs ran; no vendor program, device or UI execution.'
