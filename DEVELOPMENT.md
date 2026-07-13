# 开发与维护说明

本文只面向 Mod Manager / Workshop Bridge 维护者。

- 玩家使用说明：[README.md](README.md)
- Mod 收录投稿：[CONTRIBUTING.md](CONTRIBUTING.md)

## 项目概览

- 游戏 Steam AppID：`1991040`
- Mod Manager：WinForms，`.NET Framework 4.8`
- Workshop Bridge：BepInEx 5 Preloader Patcher
- Steam 负责工坊内容的下载、更新和卸载；本项目只负责识别、启用和接入 DLL Mod。

主要文件：

```text
Core/ModInstaller.cs           安装 BepInEx 和内嵌 Bridge
Core/LocalPluginScanner.cs     扫描本地与工坊插件
Core/LocalPluginManager.cs     启用/禁用本地插件
Core/WorkshopMetadata.cs       Steam 显示资料与在线验证
WorkshopBridge/                Preloader Patcher
WorkshopBridge.Tests/          Bridge 测试
ModManager.Tests/              管理器与 UI 集成测试
build_release.ps1              生成发布资产
```

Bridge 构建后以资源嵌入 `ModManager.exe`，安装位置为：

```text
<GameRoot>/BepInEx/patchers/StudentAge.WorkshopBridge.dll
```

`ModInstaller` 使用 SHA-256 比较内嵌版本与已安装版本。Bridge 应保持确定性构建；不要把 Git 提交哈希写入其程序集版本。

## 本地插件扫描与启停

扫描范围：

```text
BepInEx/plugins/*.dll
BepInEx/plugins/<直接子目录>/**/*.dll
BepInEx/ModManager/disabled/
BepInEx/plugins/.workshop/<WorkshopId>
```

规则：

- 一个直接子目录是一个启停单元；根级 DLL 单独成组。
- 通过临时 AppDomain 和 `ReflectionOnlyLoad` 读取 `BepInPlugin`，不执行插件代码。
- 普通重解析点不跟随；只有 `.workshop/<规范数字 ID>` 的目录联接视为工坊来源。
- 禁用时移到 `BepInEx/ModManager/disabled`，启用时原样移回。
- 不覆盖同名目标、不移动工坊联接、不删除文件，游戏运行时拒绝移动。
- 启用区和禁用区存在同名项时标记路径冲突。

`MainForm.RenderList()` 按 Workshop ID 合并索引条目和已接入联接，避免重复卡片。未匹配的联接显示为“Steam 工坊 · 未收录 · 已接入”，普通插件显示为本地未收录。

## Workshop Bridge

### 为什么使用 Preloader Patcher

BepInEx Chainloader 只扫描 `BepInEx/plugins`。普通插件被加载时扫描已经结束，因此 Bridge 必须在 `BepInEx/patchers` 中提前建立目录联接，才能让工坊 DLL 在同一次启动被发现。

Bridge 不修改游戏程序集，`TargetDLLs` 为空；同步工作在 `Initialize()` 中完成。

### 启动流程

每次游戏启动时，Bridge 会：

1. 确定当前 Steam 用户；
2. 读取当前用户的订阅和下载状态；
3. 处理首次基线或新的已下载订阅；
4. 读取游戏原生 `_mod` 启用列表；
5. 校验工坊包；
6. 在 `.workshop` 下重建目录联接。

联接格式：

```text
<GameRoot>/BepInEx/plugins/.workshop/<WorkshopId>
    -> <WorkshopRoot>/<WorkshopId>/BepInEx/plugins
```

用户文件：

```text
%USERPROFILE%/AppData/LocalLow/PakyiGame/StudentAge/Saves/<SteamID64>/_mod
%USERPROFILE%/AppData/LocalLow/PakyiGame/StudentAge/Saves/<SteamID64>/_workshop_bridge_state.json
```

Steam 数据：

```text
<SteamLibrary>/steamapps/workshop/appworkshop_1991040.acf
<SteamLibrary>/steamapps/workshop/content/1991040/
```

### 自动启用规则

- 状态文件首次创建时，当前订阅只登记为基线，不修改 `_mod`。
- 基线建立后，新订阅且已完整下载的合法 DLL Mod 会自动加入 `_mod`，并在同次启动建立联接。
- 玩家手动关闭后不会反弹；取消后重新订阅同一 ID 也不会再次自动开启。
- 下载未完成、manifest 不一致或文件暂不可用时等待后续启动重试。
- 用户、ACF、状态文件或 `_mod` 不明确时采用 fail-closed，不加载可疑内容。

状态写入和 `_mod` 更新采用原子提交；`pendingWorkshopIds` 用于处理中断恢复。

日志位置：

```text
<GameRoot>/BepInEx/WorkshopBridge.log
```

## 工坊 DLL 包格式

项目根目录必须包含：

```text
<WorkshopItem>/
├─ workshop-plugin.json
└─ BepInEx/
   └─ plugins/
      └─ <PluginName>/
         ├─ <PluginName>.dll
         └─ 运行时依赖（如需要）
```

`workshop-plugin.json` 固定为：

```json
{
  "schemaVersion": 1,
  "type": "bepinex-plugin",
  "pluginRoot": "BepInEx/plugins"
}
```

Bridge 只接受该版本和固定插件目录，并要求其中至少有一个 DLL。路径遍历、可疑重解析点、非法 JSON 或其他 schema 均拒绝接入。

## 中央索引

最小条目：

```json
{
  "id": "unique-mod-id",
  "workshopId": "1234567890"
}
```

- `id` 和规范化后的 `workshopId` 必须分别唯一。
- `workshopId` 可输入纯数字或 Steam 官方 HTTPS 详情页，但验证后统一为无前导零数字。
- `name`、`description` 可选；缺失时由 Steam 官方 API 和本地缓存补全。
- 任一非法条目都会拒绝整份索引。
- `downloadUrl`、`assetType`、`installDir` 等旧直装字段已废弃，不能回退为 DLL/ZIP 直装。

运行时缓存：

```text
<GameRoot>/BepInEx/ModManager/workshop-metadata.json
```

PR 工作流会构建项目、运行测试，并通过 Steam 官方 API 验证 Workshop ID。投稿格式见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 安全边界

- 不执行扫描到的插件代码。
- 不递归删除 Steam 工坊源目录。
- Bridge 只清理可确认由其管理的规范 ID 目录联接；普通目录和异常项保留并记录日志。
- 工坊来源、manifest、`.workshop` 根或关键状态文件出现可疑重解析点时拒绝处理。
- Steam 显示资料只能影响名称和简介，不能影响路径、Workshop ID 或加载行为。

## 构建与测试

要求：Windows、.NET SDK、.NET Framework 4.8 targeting pack。

```powershell
dotnet clean -c Release
dotnet build -c Release
dotnet run --project .\WorkshopBridge.Tests\StudentAge.WorkshopBridge.Tests.csproj -c Release
dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj -c Release
dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj -c Release -- --validate-index .\mods.json
dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj -c Release -- --validate-index .\mods.json --verify-workshop
```

`--verify-workshop` 依赖 Steam 在线接口；Bridge 测试依赖 Windows 的 `mklink /J`。

真实 Steam 数据只能复制到临时目录后测试，不要直接修改真实 `_mod` 或 `_workshop_bridge_state.json`。

## 发布

普通用户只需要 `ModManager.exe`。生成完整发布资产：

```powershell
.\build_release.ps1 `
  -BaseBepInExPackage .\release_assets\BepInEx-5.4.23-package.zip
```

输出：

```text
release_assets/ModManager.exe
release_assets/BepInEx-5.4.23-workshop-bridge.zip
```

发布前确认：

1. Release 构建和两组测试通过；
2. 索引离线验证和 Steam 在线验证通过；
3. EXE 内嵌 Bridge，ZIP 只新增 `BepInEx/patchers/StudentAge.WorkshopBridge.dll`；
4. 发布包不包含额外 `Mono.Cecil.dll`；
5. `git diff --check` 通过，版本号、目标分支和资产哈希正确。

不要提交 `bin/`、`obj/` 或 `release_assets/`。
