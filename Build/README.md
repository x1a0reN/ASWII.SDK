# Protected release

正式发布只允许使用以下入口：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build\Build-ProtectedRelease.ps1
```

该入口依次执行：

1. 使用 `Protected|AnyCPU` 编译 `.NET Framework 3.5` 中间程序集，启用优化并关闭 PDB。
2. 从仓库的 dotnet tool manifest 恢复固定版本 `Obfuscar 2.2.50`。
3. 对私有实现和字符串执行保守混淆；保留 Unity 生命周期、Harmony 约定方法和 `Doorstop.Entrypoint.StartInjected`。
4. 对原始和混淆后程序集运行 IL 门禁，确认不存在运行时授权旁路、调试属性或入口破坏。
5. 将唯一可上传的明文产物写入 `bin\Protected\ASWDEBUG.dll`，不发布 PDB。

`bin\Protected\ASWDEBUG.dll` 仍是明文托管程序集。必须把它上传到 VeriGate Admin，由服务端生成新的 `VGCH` 密文，再导出并上传蓝奏云。不得将旧的 `bin\Debug\ASWDEBUG-Lanzou.dll` 继续作为最新版本。

混淆映射保存在 `artifacts\ProtectedRelease\<run-id>\obfuscation-map.xml`。映射只用于崩溃诊断，不得上传、分发或与 DLL 一起打包。

GitHub Actions 工作流使用带 `aswdebug-build` 标签的 Windows self-hosted runner，因为编译引用来自本机游戏安装目录。runner 必须安装 Visual Studio 2026、.NET SDK，并具备项目文件中声明的游戏程序集。
