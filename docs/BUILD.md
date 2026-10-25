# 构建说明

## 仅编译自有源码

需要 Windows 10/11 x64、.NET Framework 4.x 的 64 位 C# 编译器及 PowerShell。
编译脚本默认使用 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`。
不需要 Visual Studio、不需要小米账号，也不会自动启动厂商程序。

```powershell
powershell -File .\build.ps1
powershell -File .\test.ps1
```

`artifacts/source/MIUI+.exe` 是自有宿主，需要另行准备完整 `app` 目录才能运行原生功能。
它不是把小米闭源 DLL 重新编译成了开源程序。仓库不携带运行所需的厂商组件。

## 在本地组合完整副本

准备两组合法获得且与 `profiles/miui-2.5.5.643.json` 哈希相符的输入：

| 输入 | 版本和内容 |
| --- | --- |
| MIUI+ | Windows x64，2.5.5.643；从自己有权使用的安装目录读取 |
| 连接运行库 | IDM 3.1.494 的 `idmsdk.dll`、`idmruntime.dll`，以及 MiSSO 3.0.0.6 的 `MiSSO.dll` |

参考运行库来自小米电脑管家 5.8.1.121 的官方包。版本名称本身不够，脚本会检查每个输入文件的 SHA-256。
可以查看[小米跨端智联官方网站](https://hyperos.mi.com/continuity)的支持条件和官方获取入口。
项目没有获得替第三方分发这些组件的授权，因此不会将它们打包进仓库或 CI 产物。

还需要 Python 3.11 或更新版本来执行构建工具；UI 压缩输出的已验证参考环境是 Python 3.14.6 / zlib 1.3.2。
其他 zlib 版本只有产生完全相同的固定哈希结果才会被接受。

```powershell
powershell -File .\build.ps1 `
  -MiuiDirectory 'C:\Program Files\MI\AIoT\MiShare\2.5.5.643' `
  -RuntimeDirectory 'D:\LocalVendorFiles\XiaomiPCManager-5.8.1.121' `
  -OutputDirectory '.\artifacts\local'
```

Python 命令不在 PATH 时，增加 `-PythonExecutable 'C:\Path\To\python.exe'`。
输出目录必须是新目录。脚本先检查全部输入，再创建独立副本，完成本地补丁和哈希回读；不会修改原安装。
构建失败会保留有唯一名称的 `.building-*` 临时目录供检查，不会用递归删除去清理其他文件。

正常使用时，先退出其他 MIUI+ 实例，再从完整副本目录打开 `MIUI+.exe`。
不要只复制这个 EXE；其旁边的 `app` 目录也必须保留。

## 验证范围

- Python 合成测试检查补丁边界、原字节不符、路径越界、重复路径、输入不符和输出原子发布等情况。
- C# 纯源码测试不加载厂商 DLL、不创建界面、不访问剪贴板、不接触手机。
- 提供完整本地输入后，可额外执行 61 个可执行组件的固定哈希及原生通信端点文件检查。
- CI 不包含厂商组件，因此 CI 通过不表示 MIUI+ 已成功连接手机。

构建脚本不会自动安装、启动、注册服务、修改防火墙、创建登录任务或读取设备剪贴板。
真实连接、双向剪贴板、投屏停止和重新打开需要设备所有者另行实测。

## 重编并验证自有原生助手

普通本地组合构建会使用已经由源码重编核对的自有助手字节，不需要安装 C/C++ 工具链。
希望修改助手时，需要能生成 AMD64 Windows COFF 的 MinGW-w64 GCC/GNU assembler。

```powershell
powershell -File .\verify-native.ps1 -GccExecutable 'gcc'
```

该命令重编自有汇编，比对代码、相对调用和 unwind 元数据，再运行完全由自有内存 stub 组成的测试。
不会加载厂商程序。提供原始 `MiScreenShare.exe` 可额外执行只读 PE 校验及精确回退验证：

```powershell
powershell -File .\verify-native.ps1 `
  -OriginalMediaFile 'C:\Program Files\MI\AIoT\MiShare\2.5.5.643\MiScreenShare.exe'
```

`-GccExecutable`、`-PythonExecutable` 都可传绝对路径。所有产物放在 `artifacts/native-tests`，不会提交。
更换工具链若生成不同机器码，应先人工审阅指令与 ABI，不能直接更新固定哈希使校验失去意义。
CI 的 `windows-2022` 环境提供 GCC，详情见 [GitHub 托管镜像说明](https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md)。
