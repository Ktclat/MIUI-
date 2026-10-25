$ErrorActionPreference = 'Stop'
$controllerDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '未找到 .NET Framework 4.x C# 编译器。' }
$allowlistPath = Join-Path $controllerDirectory 'candidate-allowlist.json'
if (-not (Test-Path -LiteralPath $allowlistPath)) { throw '缺少仓库中的固定白名单 candidate-allowlist.json。构建不会自动信任更改后的候选。' }
# Use the standard executable icon. Public builds require no Xiaomi icon resource.
Push-Location -LiteralPath $controllerDirectory
try {
    & $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Core.dll /resource:candidate-allowlist.json,CandidateAllowlist.json /win32manifest:app.manifest /out:MIUI-Compat-Controller.exe CompatController.cs
    if ($LASTEXITCODE -ne 0) { throw "编译失败：$LASTEXITCODE" }
} finally { Pop-Location }
Get-Item -LiteralPath (Join-Path $controllerDirectory 'MIUI-Compat-Controller.exe') | Select-Object FullName, Length, LastWriteTime
