$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Microsoft .NET Framework 4.x compiler is required.' }
Push-Location -LiteralPath $PSScriptRoot
try {
    & $compilerPath /nologo /target:exe /platform:x64 /optimize+ /warn:4 /warnaserror+ /main:MiuiCompatExperiment.PureSourceTests /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Core.dll /out:PureSourceTests.exe CompatController.cs PureSourceTests.cs
    if ($LASTEXITCODE -ne 0) { throw "Pure test compilation failed: $LASTEXITCODE" }
    & (Join-Path $PSScriptRoot 'PureSourceTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Pure source tests failed; see pure-source-tests.json.' }
} finally { Pop-Location }
