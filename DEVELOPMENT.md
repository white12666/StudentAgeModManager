# 开发与维护说明

面向玩家的说明见 [README.md](README.md)。本文说明 Workshop Bridge、工坊包约定、构建测试与发布流程。

## 目标流程

```text
首次：ModManager.exe
        └─安装 BepInEx + BepInEx/patchers/StudentAge.WorkshopBridge.dll

日常：Steam 订阅 DLL Mod
        └─游戏原生 Mod 页面启用并重启
             └─BepInEx Preloader 执行 Bridge
                  └─BepInEx 扫描已启用工坊项目中的插件
```

Steam 负责工坊内容的下载、更新和卸载。Bridge 不复制 DLL、不实现下载器，也不会扫描所有订阅项目。

## 项目结构

```text
StudentAgeModManager.csproj       WinForms 管理器（net48）
Core/ModInstaller.cs              安装 BepInEx、提取并校验内嵌 Bridge
WorkshopBridge/                   BepInEx 5 Preloader Patcher（net48）
WorkshopBridge.Tests/             Bridge 同步与目录联接测试
ModManager.Tests/                 管理器资源嵌入/提取集成测试
build_release.ps1                 生成 exe 和带 Bridge 的手动前置包
```

管理器仍是单个 `ModManager.exe`。构建时，Bridge 项目先生成，然后以资源名
`StudentAgeModManager.Resources.StudentAge.WorkshopBridge.dll` 嵌入 exe。安装时资源被写入：

```text
<GameRoot>/BepInEx/patchers/StudentAge.WorkshopBridge.dll
```

Bridge 的 `Mono.Cecil` 包只用于编译 BepInEx Patcher 的方法签名，设置了 `ExcludeAssets=runtime`。发布时不能额外携带一份 Mono.Cecil；运行时使用 `BepInEx/core/Mono.Cecil.dll`（5.4.23.4 自带 0.10.4.0）。

## 为什么必须是 Preloader Patcher

BepInEx 5 Chainloader 只递归扫描 `Paths.PluginPath`，即 `BepInEx/plugins`。普通插件本身也在该扫描阶段才被发现，执行时已经太晚。

Bridge 放在 `BepInEx/patchers`，其公开静态入口符合 BepInEx 5 约定：

```csharp
IEnumerable<string> TargetDLLs
void Initialize()
void Patch(ref AssemblyDefinition assembly)
void Finish()
```

`TargetDLLs` 为空，因为 Bridge 不修改游戏程序集。BepInEx 5.4.23.4 的 `AssemblyPatcher.PatchAndLoad` 会先调用所有 `Initialize()`，再枚举 `TargetDLLs`，因此空目标仍会在 Chainloader 之前完成同步。

## Bridge 数据来源

### 游戏原生启用列表

游戏把已启用 Workshop ID 按行保存到：

```text
%USERPROFILE%/AppData/LocalLow/PakyiGame/StudentAge/Saves/<SteamID64>/_mod
```

Bridge 优先读取注册表：

```text
HKCU/Software/Valve/Steam/ActiveProcess/ActiveUser
SteamID64 = 76561197960265728 + ActiveUser
```

若注册表不可用，则读取游戏所在 Steam 库的 `appmanifest_1991040.acf` 中的 `LastOwner`。仍无法确定时，只有本机恰好存在一个含 `_mod` 的存档用户才会采用；多用户场景不会猜测或加载其他用户的启用列表。

### Workshop 目录

首选游戏所在库：

```text
<SteamLibrary>/steamapps/workshop/content/1991040
```

同时读取 Steam 注册表路径与 `steamapps/libraryfolders.vdf`，支持 Workshop 内容位于其他 Steam 库的情况。

## 工坊 DLL 包格式

工坊项目根目录必须是：

```text
<WorkshopItem>/
├─ workshop-plugin.json
└─ BepInEx/
   └─ plugins/
      └─ <PluginName>/
         ├─ <PluginName>.dll
         └─ 其他运行时依赖（如确有需要）
```

`workshop-plugin.json` 的 v1 格式：

```json
{
  "schemaVersion": 1,
  "type": "bepinex-plugin",
  "pluginRoot": "BepInEx/plugins"
}
```

当前校验规则是有意严格的：

- 文件最大 64 KiB；
- `schemaVersion` 必须是数字 `1`；
- `type` 必须精确等于 `bepinex-plugin`；
- `pluginRoot` 必须精确等于 `BepInEx/plugins`；
- `pluginRoot` 只作声明校验，代码始终使用固定目录，不采信任意路径；
- 绝对路径、`..`、其他 schema 或无效 JSON 均拒绝桥接。

工坊条目是否显示在游戏“本地 Mod”页由 Steam 已订阅项目列表决定；普通 `Cfgs` 内容不是 Bridge 的必需项。若同一工坊项目还提供原生 JSON Mod，可同时保留其 `Cfgs/zh-cn` 等标准目录。

## 同步算法

Bridge 在每次游戏启动时：

1. 找到 `_mod`、Workshop 根目录和 `BepInEx/plugins`；
2. 读取、去重并验证纯数字且非零的 Workshop ID；
3. 检查 `.workshop` 是 Bridge 可安全使用的普通目录；
4. 只删除 `.workshop` 下“名称为数字且确认为目录重解析点”的旧入口；
5. 对每个已启用项目校验 manifest、固定插件目录和 DLL；
6. 创建目录联接：

```text
<GameRoot>/BepInEx/plugins/.workshop/<WorkshopId>
    -> <WorkshopRoot>/<WorkshopId>/BepInEx/plugins
```

Windows 的 BepInEx 递归扫描会穿过目录联接发现 DLL。每次重建联接可自动适配 Steam 库移动和更新。

## 失败与删除安全边界

- Bridge 的顶层异常会被捕获；Bridge 失败不能阻止游戏启动。
- `_mod` 不存在、读取失败、用户身份有歧义或 Workshop 根目录暂不可用时，采用 fail-closed：清理能确认安全删除的规范旧联接，本次不加载 DLL 工坊项目，避免沿用其他 Steam 用户的启用集合。
- `_mod` 存在但为空时，视为用户明确禁用全部项目，会清理旧联接。
- `.workshop` 本身若是重解析点，立即停止同步。
- `.workshop/<ID>` 若是普通目录/文件，记录冲突并保留。
- 非规范 ID 名称（包括前导零）的项目一律保留。
- 只对确认带 `FileAttributes.ReparsePoint` 的数字目录调用非递归 `Directory.Delete(path, false)`。
- 绝不对 Workshop 源目录执行删除。
- 传给 `cmd.exe /D /V:OFF /C mklink /J` 的路径会拒绝命令元字符、引号、百分号和换行。

日志：

```text
<GameRoot>/BepInEx/WorkshopBridge.log
```

## 中央索引中的工坊条目

给 `mods.json` 条目填写纯数字 `workshopId` 后，管理器会把主按钮切换为「订阅 / 查看工坊」，不会下载 `downloadUrl`。无效但非空的 `workshopId` 也不会回退为直接 DLL 安装。

新工坊条目可以把旧下载字段留空：

```json
{
  "id": "ExamplePlugin",
  "name": "示例插件",
  "description": "示例",
  "repo": "owner/repo",
  "version": "workshop",
  "downloadUrl": "",
  "assetType": "",
  "installDir": "",
  "workshopId": "1234567890"
}
```

如果需要帮助旧用户清理历史直装版本，应暂时保留原 `installDir`。管理器检测到该目录后会显示「清理旧安装」。`update_index.ps1` 会跳过带 `workshopId` 的条目，版本更新交给 Steam。

没有 `workshopId` 的旧索引条目仍保留原 GitHub 下载流程，作为迁移兼容，不是新 DLL Mod 的推荐发布方式。

## 构建与测试

要求：Windows、.NET SDK、.NET Framework 4.8 targeting pack。

```powershell
dotnet build -c Release
dotnet run --project .\WorkshopBridge.Tests\StudentAge.WorkshopBridge.Tests.csproj -c Release
dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj -c Release
```

测试覆盖：

- 有效启用项目建立目录联接；
- 无 manifest 和无效 manifest 被跳过；
- 重复、非法及零 ID 被过滤；
- BepInEx 风格递归扫描可穿过联接发现 DLL；
- `_mod` 或 Workshop 根目录缺失时 fail-closed，空列表时清理；
- 普通目录冲突不覆盖；
- `.workshop` 根为重解析点时停止；
- `mklink` 命令元字符路径被拒绝；
- `libraryfolders.vdf` 跨库定位；
- 工坊声明（包括仅空白值）不会回退直接 DLL 下载；
- ModManager.exe 内嵌 Bridge、提取、SHA-256 校验、损坏后修复。

Bridge 测试会调用 Windows `mklink /J`，因此不能在非 Windows 环境运行。

## 发布资产

### 管理器安装（推荐）

只发布 `ModManager.exe` 即可。它会在安装官方 BepInEx 包后覆盖部署当前内嵌 Bridge，所以基础 ZIP 中即使没有 Bridge，玩家通过管理器安装仍能得到完整前置。

### 手动解压前置

手动解压用户需要 ZIP 自带：

```text
BepInEx/patchers/StudentAge.WorkshopBridge.dll
```

生成发布资产：

```powershell
.\build_release.ps1 `
  -BaseBepInExPackage .\release_assets\BepInEx-5.4.23-package.zip
```

输出（`release_assets/` 默认被 gitignore）：

```text
release_assets/ModManager.exe
release_assets/BepInEx-5.4.23-workshop-bridge.zip
```

脚本会构建项目、嵌入/校验 Bridge、向基础 BepInEx ZIP 写入 patcher，并核对 ZIP 内 DLL 哈希。发布 `bepinex` Release 时应上传带 `workshop-bridge` 的包，并同步更新 `mods.json` 中 `bepinex.downloadUrl`。

## 管理器版本发布

1. 修改 `StudentAgeModManager.csproj` 的 `Version` / `AssemblyVersion` / `FileVersion`；
2. 运行全部构建和测试；
3. 运行 `build_release.ps1`；
4. 创建 `v*.*.*` Release 并上传 `ModManager.exe`；
5. 更新 `mods.json` 的 `manager.version` 和下载链接；
6. 确认 `git diff --check`、分支和发布资产哈希。

## 编码注意事项

- `WebClient.Encoding` 必须显式使用 UTF-8，否则中文索引可能按系统 ANSI 解码。
- 含非 ASCII 内容的 PowerShell 5.1 脚本应保存为 UTF-8 BOM；`build_release.ps1` 特意只使用 ASCII 文本。
- 不要提交 `bin/`、`obj/` 或 `release_assets/`。
