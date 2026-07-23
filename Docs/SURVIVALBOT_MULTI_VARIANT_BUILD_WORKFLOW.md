# SurvivalBot 多版本自动构建工作流

最后更新：2026-07-23

## 1. 目标

一次执行同时生成两个相互隔离、可直接部署的版本，并在打包前检查编译结果是否符合功能边界。

入口：

~~~powershell
& 'D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\Forks\SurvivalBot\Build\Build-SurvivalBotVariants.ps1'
~~~

默认行为：

1. 使用 Debug + SurvivalEdition=Private 构建自用版。
2. 使用 Release + SurvivalEdition=ReleaseA 构建发布版 A。
3. 单独构建仅供自用版使用的 CompactNavConverter.exe。
4. 使用 Mono.Cecil 审计两个 DLL 的类型、方法和版本标识。
5. 生成两个完整部署目录和 SHA-256 清单。
6. 不创建备份，直接把自用版覆盖部署到游戏并逐项核验哈希。

只生成产物、不部署：

~~~powershell
& 'D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\Forks\SurvivalBot\Build\Build-SurvivalBotVariants.ps1' -SkipDeploy
~~~

## 2. 功能矩阵

| 功能 | 自用版 Private | 发布版 A |
|---|---:|---:|
| 正式生存循环 | 保留 | 保留 |
| .aswnav 0.10m 运行时寻路 | 保留 | 保留 |
| 不可配置的自动职业导演状态机 | 保留 | 保留 |
| 攻击期间安全持续移动与强锁 | 保留 | 保留 |
| `.aswnav` 边界索引与分帧精确悬崖搜索 | 保留 | 保留 |
| 躲避路径敌方视线审计及致死悬崖验证 | 保留 | 保留 |
| 敌人 ESP 与正式运行参数 | 保留 | 保留 |
| RAIN 建图入口及直接加载地图建图 | 保留 | 编译期移除 |
| 战斗测试 | 保留 | 编译期移除 |
| 开房测试 | 保留 | 编译期移除 |
| 一键进入 level33 纯寻路巡回 | 保留 | 编译期移除 |
| CompactNavConverter.exe | 随包提供 | 不提供 |
| 可配置“战术” | 移除 | 移除 |
| 可配置“职业策略”及对应下拉框 | 移除 | 移除 |
| 可配置“保命技能”及对应下拉框 | 移除 | 移除 |

发布版 A 只消费预先生成的 level33.aswnav 或游戏原生导航，不会启动运行时 MapBake。

## 3. 编译期边界

ASWDEBUG.csproj 使用 SurvivalEdition 属性：

~~~text
Private  -> SURVIVAL_INTERNAL_TOOLS
ReleaseA -> SURVIVAL_RELEASE_A
~~~

发布版 A 不编译以下源文件：

~~~text
Cheats\AutoBattle\AutoBattleTakeoverManager.cs
Cheats\SurvivalBot\LocalNavigationCombatTest.cs
Cheats\SurvivalBot\MapBakeSceneLoader.cs
~~~

相关管理方法、Harmony 测试钩子和 UI 入口也使用同一编译符号移除。这里不是运行时隐藏按钮，发布 DLL 中不存在对应类型和管理方法。

## 4. 产物布局

每次执行创建新的时间戳目录，不清空或覆盖以前的构建：

~~~text
artifacts\SurvivalBot\yyyyMMdd_HHmmss_fff\
+-- Private\
|   +-- Game\
|       +-- ASWII_Data\Managed\ASWDEBUG.dll
|       +-- ASWII_Data\Managed\0Harmony.dll
|       +-- ASWII_Data\Managed\Mono.Cecil.dll
|       +-- ASWII_Data\Managed\BouncyCastle.Crypto.dll
|       +-- ASWDEBUG.Tools\CompactNavConverter.exe
|       +-- winhttp.dll
|       +-- doorstop_config.ini
+-- ReleaseA\
|   +-- Game\
|       +-- ASWII_Data\Managed\ASWDEBUG.dll
|       +-- ASWII_Data\Managed\0Harmony.dll
|       +-- winhttp.dll
|       +-- doorstop_config.ini
+-- manifest.json
~~~

manifest.json 记录：

- Git commit 和工作区状态；
- 两个版本的功能矩阵；
- Mono.Cecil 审计结果；
- 每个部署文件的长度和 SHA-256。

## 5. 自动审计

构建脚本发现以下任一情况会立即失败，不会部署：

- 自用版缺少内部测试类型；
- 发布版 A 仍包含内部测试、直接地图加载或 level33 本地巡回类型；
- 发布版 A 仍包含测试/建图管理方法；
- 任一版本仍包含已删除的战术、职业策略或保命技能设置 API；
- 任一版本缺少职业识别、技能执行、护卫/突击导演、躲避路线视线审计或悬崖验证方法；
- DLL 内的版本标识与目标版本不一致；
- 构建、打包或部署后的任一文件哈希不一致。

## 6. 单独构建

需要排查某个版本时可以直接调用 MSBuild。

自用版：

~~~powershell
& 'D:\Program Files\Visual Studio 2026\MSBuild\Current\Bin\MSBuild.exe' 'D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\Forks\SurvivalBot\ASWDEBUG.csproj' /t:Build /p:Configuration=Debug /p:SurvivalEdition=Private /m /v:minimal
~~~

发布版 A（不部署）：

~~~powershell
& 'D:\Program Files\Visual Studio 2026\MSBuild\Current\Bin\MSBuild.exe' 'D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\Forks\SurvivalBot\ASWDEBUG.csproj' /t:Build /p:Configuration=Release /p:SurvivalEdition=ReleaseA /p:DeployToGame=false /m /v:minimal
~~~

普通构建未指定 SurvivalEdition 时默认为 Private，因此现有自用部署习惯不受影响。
