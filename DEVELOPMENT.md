# 开发与维护说明

面向玩家的说明见 [README.md](README.md)。本文说明 Workshop Bridge 0.2.0、工坊包约定、每用户自动启用状态、构建测试与发布流程。

## 目标流程

```text
首次：ModManager.exe
        └─安装 BepInEx + BepInEx/patchers/StudentAge.WorkshopBridge.dll
             └─当前 Steam 用户第一次启动游戏
                  └─登记该用户当时的全部订阅为基线，不修改 _mod

日常：基线建立后订阅新的合法 DLL Mod
        └─Steam 下载完成
             └─下一次启动游戏
                  └─BepInEx Preloader 执行 Bridge
                       ├─自动写入当前用户 _mod
                       ├─本次立即建立目录联接
                       └─Chainloader 本次直接扫描并加载 DLL
```

自动启用只能发生在“Steam 下载完成后的下一次游戏启动”，不会发生在下载完成瞬间。Bridge 是 Preloader Patcher，Mod Manager 与 Bridge 在 Steam 后台下载完成时通常都没有运行。

Steam 负责工坊内容的下载、更新和卸载。Bridge 不实现下载器，不复制、覆盖或接管 Steam 工坊 DLL。

## 项目结构

```text
StudentAgeModManager.csproj       WinForms 管理器（net48）
Core/ModInstaller.cs              安装 BepInEx、提取并校验内嵌 Bridge
Core/WorkshopMetadata.cs          Steam 显示元数据、清理、缓存与在线验证
WorkshopBridge/                   BepInEx 5 Preloader Patcher（net48）
WorkshopBridge.Tests/             Bridge 状态、订阅过滤与目录联接测试
ModManager.Tests/                 管理器资源、UI 与提取集成测试
build_release.ps1                 生成 exe 和带 Bridge 的手动前置包
```

管理器仍是单个 `ModManager.exe`。构建时，Bridge 项目先生成，然后以资源名
`StudentAgeModManager.Resources.StudentAge.WorkshopBridge.dll` 嵌入 exe。安装时资源被写入：

```text
<GameRoot>/BepInEx/patchers/StudentAge.WorkshopBridge.dll
```

Bridge 的 `Mono.Cecil` 包只用于编译 BepInEx Patcher 的方法签名，设置了 `ExcludeAssets=runtime`。发布时不能额外携带一份 Mono.Cecil；运行时使用 `BepInEx/core/Mono.Cecil.dll`（BepInEx 5.4.23.4 自带 0.10.4.0）。

`ModInstaller` 会用 SHA-256 精确比较已安装 Bridge 与管理器内嵌副本。为避免只修改管理器 UI、文档或 `mods.json` 时也让 Bridge 被误判为新版本，Bridge 项目必须保持确定性构建，并设置：

```xml
<Deterministic>true</Deterministic>
<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
```

因此新增中央索引 `workshopId` 不需要用户更新 Bridge；用户只需刷新索引并由 Steam 下载新订阅 Mod 自己的内容。只有 Bridge 源码、项目配置、依赖、协议版本或构建工具链真正变化时，Bridge 哈希才应改变。不要把 Git 提交哈希重新写入 Bridge 的 `AssemblyInformationalVersion`。

## 为什么必须是 Preloader Patcher

BepInEx 5 Chainloader 只递归扫描 `Paths.PluginPath`，即 `BepInEx/plugins`。普通插件本身也在该扫描阶段才被发现；如果 Bridge 是普通插件，执行时已经太晚，无法让新工坊 DLL 在同一次启动中被扫描。

Bridge 放在 `BepInEx/patchers`，其公开静态入口符合 BepInEx 5 约定：

```csharp
IEnumerable<string> TargetDLLs
void Initialize()
void Patch(ref AssemblyDefinition assembly)
void Finish()
```

`TargetDLLs` 为空，因为 Bridge 不修改游戏程序集。真实 BepInEx 5.4.23.4 probe 已确认 `AssemblyPatcher.PatchAndLoad` 会先调用所有 `Initialize()`，再枚举 `TargetDLLs`，因此空目标仍会在 Chainloader 之前完成同步。

## Bridge 数据来源

### 当前 Steam 用户

Bridge 首选注册表：

```text
HKCU/Software/Valve/Steam/ActiveProcess/ActiveUser
SteamID64 = 76561197960265728 + ActiveUser
```

注册表不可用时，只回退到游戏所在 Steam 库的 `appmanifest_1991040.acf` 中的 `LastOwner`。若两者都不能明确给出有效用户，Bridge fail-closed。**不会**再根据“唯一一个含 `_mod` 的存档目录”猜测用户。

用户存档目录必须与计算出的 SteamID64 精确匹配：

```text
%USERPROFILE%/AppData/LocalLow/PakyiGame/StudentAge/Saves/<SteamID64>/
```

### 游戏原生启用列表

游戏把已启用 Workshop ID 按行保存到：

```text
%USERPROFILE%/AppData/LocalLow/PakyiGame/StudentAge/Saves/<SteamID64>/_mod
```

Bridge 只接受规范十进制、非零 Workshop ID。文件超过 1 MiB、是重解析点、被目录占用或无法读取时 fail-closed。

### 当前用户订阅与下载状态

Bridge 从 Workshop 内容库旁读取：

```text
<SteamLibrary>/steamapps/workshop/appworkshop_1991040.acf
```

该文件由有界、严格、block-aware 的 VDF 解析器读取，最大 16 MiB。关键区块：

```text
AppWorkshop
├─ WorkshopItemsInstalled
└─ WorkshopItemDetails
```

判定规则：

- `SubscribedIds` 只包含 `WorkshopItemDetails/<ID>/subscribedby` **精确等于当前 Account ID** 的项目；
- 目录存在、`WorkshopItemsInstalled` 存在或 `_mod` 残留都不能代替当前用户订阅；
- `DownloadedIds` 还要求 `WorkshopItemsInstalled` 的 manifest 与项目详情 manifest 一致；
- 若项目详情存在 `latest_manifest`，它也必须与已安装 manifest 一致；
- 因此尚未下载、下载未完成或正在更新的项目不会自动开启，也不会建立 DLL 联接。

ACF 缺失、损坏、超限、是重解析点、appid 不匹配或结构不明确时，整个 DLL 同步 fail-closed。

### Workshop 目录

首选游戏所在库：

```text
<SteamLibrary>/steamapps/workshop/content/1991040
```

同时读取 Steam 注册表路径与 `steamapps/libraryfolders.vdf`，支持 Workshop 内容位于其他 Steam 库的情况。订阅元数据必须位于所选 Workshop 内容库对应的 `steamapps/workshop/appworkshop_1991040.acf`，不能由调用者指向无关文件。

## 每用户自动启用状态

每个 Steam 用户独立维护：

```text
%USERPROFILE%/AppData/LocalLow/PakyiGame/StudentAge/Saves/<SteamID64>/_workshop_bridge_state.json
```

schema v1：

```json
{
  "schemaVersion": 1,
  "steamAccountId": "234323669",
  "seenWorkshopIds": ["1234567890"],
  "pendingWorkshopIds": []
}
```

约束：

- `steamAccountId` 必须与当前 Steam 用户精确匹配；
- 所有 ID 必须是无前导零的规范十进制非零值；
- `seenWorkshopIds` 与 `pendingWorkshopIds` 不得重复或重叠；
- 文件最大 1 MiB，不能是重解析点或目录；
- 状态写入使用同目录临时文件、`Flush(true)` 和 `File.Replace`/`File.Move` 原子提交；
- `seenWorkshopIds` 单调增长，取消订阅时绝不删除。

### 首次基线

状态文件不存在时，Bridge 把当前用户的**全部当前订阅**写入 `seenWorkshopIds`，包括尚未下载完成的项目，然后结束自动启用阶段：

- 不修改 `_mod`；
- 不自动开启合法 DLL；
- 以后这些基线项目即使完成下载，也不会自动开启；
- 每个 Steam 用户分别建立自己的基线。

这可以避免升级到 Bridge 0.2.0 时意外执行历史订阅。基线中的 DLL 仍可由玩家在游戏“本地”页手动开启。

### 后续新订阅

状态存在且有效时，候选集合是：

```text
当前用户 DownloadedIds - seenWorkshopIds
```

对每个候选：

- 文件尚不可用或 DLL 枚举发生 I/O 错误：暂不记 seen，后续启动重试；
- 普通 JSON 项目、无 manifest、非法 manifest、缺少 DLL 或存在不安全重解析点：不写 `_mod`，安全记 seen；
- 合法 Bridge DLL 项目：进入两阶段自动启用事务。

已完成的普通/非法项目一旦记 seen，之后内容变成 DLL 也不会突然获得自动执行权限。

### `_mod` 两阶段提交与恢复

合法新 DLL 的顺序：

1. 先把 ID 写入 `pendingWorkshopIds`；
2. 原子追加到 `_mod`，已有 ID 不重复写入；
3. 把 ID 从 pending 移入 seen，并原子保存最终状态；
4. 同一次 `Synchronize` 继续读取 `_mod`、创建联接，让 Chainloader 本次直接加载。

如果 `_mod` 写入失败，Bridge 回滚 pending，不把项目错误提交为 seen，以便后续重试。如果 `_mod` 已提交但最终状态保存失败，磁盘上仍保留 pending；下次启动会把 pending 保守地收敛为 seen，**不会再次追加**。这样即使玩家在中断后手动关闭，也不会被恢复逻辑重新打开。

玩家手动从 `_mod` 删除 ID 后，seen 仍保留，所以后续启动不会反弹。取消订阅再重新订阅同一个 Workshop ID 也不会恢复开启。

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
- 文件或其路径不能是可疑重解析点；
- `schemaVersion` 必须是数字 `1`；
- `type` 必须精确等于 `bepinex-plugin`；
- `pluginRoot` 必须精确等于 `BepInEx/plugins`；
- `pluginRoot` 只作声明校验，代码始终使用固定目录，不采信作者提供的任意路径；
- 绝对路径、`..`、其他 schema 或无效 JSON 均拒绝桥接；
- 固定插件根中必须至少有一个 `.dll`。

工坊条目是否显示在游戏“本地”页由 Steam 已订阅项目列表决定；普通 `Cfgs` 内容不是 Bridge 的必需项。若同一工坊项目还提供原生 JSON Mod，可同时保留其 `Cfgs/zh-cn` 等标准目录。

## 完整同步算法

Bridge 在每次游戏启动时：

1. 找到当前 Steam 用户、该用户的 `_mod`/状态文件、Workshop 根、对应 ACF 和 `BepInEx/plugins`；
2. 严格读取当前用户的 `SubscribedIds` 与 `DownloadedIds`；
3. 建立首次基线，或按上述状态机一次性处理新的已下载订阅；
4. 读取并去重 `_mod` 中的规范 Workshop ID；
5. 清理 `.workshop` 下可确认由 Bridge 管理的旧目录联接；
6. 对每个 `_mod` ID 再次要求它同时属于当前用户的 `SubscribedIds` 和 `DownloadedIds`；
7. 校验项目根、manifest、固定插件目录和 DLL；
8. 创建目录联接：

```text
<GameRoot>/BepInEx/plugins/.workshop/<WorkshopId>
    -> <WorkshopRoot>/<WorkshopId>/BepInEx/plugins
```

Windows 的 BepInEx 递归扫描会穿过目录联接发现 DLL。每次重建联接可自动适配 Steam 库移动和更新。

普通 `_mod` 项目若已取消订阅、属于其他用户、尚未下载完成或正在更新，会被跳过并失去旧联接；Bridge 不会删除它在 `_mod` 中的原生记录，也不会删除 Steam 源文件。

## 失败与删除安全边界

- Bridge 的顶层异常会被捕获并写日志；Bridge 失败不能阻止游戏启动。
- 用户身份、ACF 订阅/下载状态、每用户状态或 `_mod` 不明确时，采用 fail-closed：清理能确认安全删除的规范旧联接，本次不加载或自动启用 DLL 工坊项目。
- `_mod` 存在但为空时，视为玩家明确禁用全部项目，会清理旧联接。
- `.workshop` 本身若是重解析点，立即停止同步。
- `.workshop/<ID>` 若是普通目录或文件，记录冲突并保留。
- 非规范 ID 名称（包括前导零）一律保留。
- 只对名称为规范 ID、确认为目录且带 `FileAttributes.ReparsePoint` 的入口调用非递归 `Directory.Delete(path, false)`。
- Workshop 项目根、`BepInEx/plugins` 源目录和 manifest 的不安全重解析点会被拒绝。
- 绝不对 Workshop 源目录执行递归删除。
- 传给 `cmd.exe /D /V:OFF /C mklink /J` 的路径会拒绝引号、`& | < > ^ % !` 和换行。

日志：

```text
<GameRoot>/BepInEx/WorkshopBridge.log
```

日志包括基线数、自动启用数、原生启用 ID 数、建立/清理联接数、跳过项、错误和逐项原因。

## 中央索引中的工坊条目

`mods.json` 的 `workshopId` 是字符串，允许以下三种输入：

```text
<WORKSHOP_ID>
https://steamcommunity.com/sharedfiles/filedetails/?id=<WORKSHOP_ID>
https://steamcommunity.com/workshop/filedetails/?id=<WORKSHOP_ID>
```

`<WORKSHOP_ID>` 是文档占位符，实际值必须是 ASCII 十进制、非零且不超过 `UInt64.MaxValue`。解析器允许数字前导零和字段外围空白，但会立即规范化为无前导零的十进制字符串；贡献者应直接提交规范形式。

官方链接采用严格白名单：

- scheme 必须为 HTTPS，host 必须精确为 `steamcommunity.com`；
- 只允许默认 HTTPS 端口，不允许 user-info 或 fragment；
- 原始路径必须精确匹配上述两个 `filedetails` 路径，可带末尾 `/`；
- 必须恰好有一个小写 `id` 查询参数，解码后是合法数字 ID；
- 可以带其他合法查询参数；
- HTTP、子域名/伪后缀、任意第三方 URL、短链、`steam://`、点路径、反斜杠路径、非法转义、缺失/重复 `id`、Unicode 数字、零和溢出值一律拒绝。

`IndexClient.ParseAndValidate(...)` 在下载后验证并规范化**整份**索引：

- `schemaVersion` 必须为 `1`，`mods` 必须存在，条目不能为 `null`；
- Mod `id` 必须是 JSON 字符串，并且非空、无首尾空白、不超过 128 字符且不含控制字符；
- Mod `id` 按 `StringComparer.OrdinalIgnoreCase` 拒绝重复；
- `name`、`description` 存在时只能是 JSON 字符串或 `null`，同样拒绝仅大小写不同的重复字段；
- `workshopId` 只能是 JSON 字符串或 `null`，不接受 JSON 数字的隐式转换；
- Workshop ID 在规范化后拒绝重复，因此数字 ID 与指向同一 ID 的链接会冲突；
- 任一非法或重复条目都会抛出 `InvalidDataException` 并拒绝整份索引，不会静默跳过。

验证完成后，下游只会看到规范数字 ID。管理器自行调用 `WorkshopItem.PageUrl(...)` 构造固定的 Steam 页面地址，绝不直接打开索引提供的原始 URL。非空值（包括纯空白或非法文本）始终被视为已声明工坊，绝不能回退为直接 DLL 下载。

新工坊条目只强制要求稳定、唯一的内部 `id` 和合法的 `workshopId`：

```json
{
  "id": "<UNIQUE_MOD_ID>",
  "workshopId": "<WORKSHOP_ID>"
}
```

`name`、`description` 以及旧下载字段都可以省略。若索引提供非空名称或说明，它们始终优先并保持原样；字段省略、为 `null`、空字符串或纯空白时，才进入 Steam 显示元数据补全。以上尖括号内容是占位符，不能原样写入索引，也不要虚构 Workshop ID。

如果需要帮助旧用户清理历史直装版本，应暂时保留原 `installDir`。管理器检测到该目录后会显示「清理旧安装」。`update_index.ps1` 会跳过带 `workshopId` 的条目，版本更新交给 Steam。

没有 `workshopId` 的旧索引条目仍保留原 GitHub 下载流程，作为迁移兼容，不是新 DLL Mod 的推荐发布方式。

### Steam 显示元数据

元数据只调用无需 API Key 的 Steam 官方接口，不抓取 HTML，也不经过 GitHub 镜像：

```text
POST https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/

itemcount=<COUNT>
publishedfileids[0]=<WORKSHOP_ID>
```

`SteamWorkshopMetadataProvider` 在请求前只接受规范数字 ID、去重并按最多 50 项分批。传输层固定使用 HTTPS POST、禁止自动重定向，设置 10 秒端到端超时、4 MiB 响应上限和最多两次请求，并严格按 UTF-8 解析。表单中只有 `itemcount` 与 `publishedfileids[n]`。

响应解析要求 `response.publishedfiledetails` 结构正确；数值字段必须是真正的 JSON 整数，不接受数字字符串、浮点数或布尔值。每个结果必须返回所请求的规范 `publishedfileid`，并读取 `result`、`consumer_app_id`、`title` 和 `description`。缺失、重复或意外 ID 会被拒绝。Steam 返回的 URL、文件名、路径、下载地址和安装信息没有对应数据通道，绝不能影响安装。

标题和说明会 HTML decode、移除 BBCode、控制字符、Unicode 格式字符和不成对 surrogate，并折叠换行/空白；有效 emoji 保留。标题最多 128 个 UTF-16 code unit，摘要最多 240，截断不会拆开 surrogate pair。原始单字段只处理前 64 KiB。

运行时顺序是：

1. `IndexClient.ParseAndValidate(...)` 先完成整份索引的确定性验证和 Workshop ID 规范化；
2. 仅选择缺少 `name` 或 `description` 的工坊条目；
3. 24 小时内的新鲜缓存可直接使用，缺失或过期时请求 Steam；一旦获得有效实时结果，它优先于旧缓存；
4. 最终显示回退顺序为有效实时 Steam 数据、本地缓存、内部 `id`，说明最后使用“Steam 创意工坊项目”；
5. 补全完成后才应用索引中的 GitHub 镜像列表。

运行时 Steam 网络、解析或项目错误只影响可选显示资料，不会推翻已经通过确定性验证的索引；取消操作仍会传播。索引中显式非空文案不清理、不覆盖。补全过程只能写 `ModEntry.name`/`description`，不能修改 Workshop ID、URL、安装路径、下载方式、Bridge manifest 或工坊/直装分流。

缓存位置和约束：

```text
<GameRoot>/BepInEx/ModManager/workshop-metadata.json
```

缓存 schema 为 v1，最大 1 MiB，写入使用同目录临时文件、`Flush(true)` 和 `File.Replace`/`File.Move`。读取时重新严格检查字段类型、规范 ID、`consumerAppId == 1991040`、非空清理后标题和时间戳；损坏、超限或不可信条目按未命中处理。过期缓存仍可在实时请求失败时回退，缓存重写只保留当前已验证索引中的工坊 ID。

### PR 在线真实性验证

`WorkshopMetadataService.VerifyIndexAsync(...)` 不读取或写入运行时缓存。它实时要求每个声明的项目公开可读、Steam 返回 ID 精确匹配、清理后标题非空，并且 `consumer_app_id == 1991040`。项目明确无效、私有/删除、返回内容非法或网络失败都会抛出错误并让 CLI 返回非零。

创作者投稿流程、必填证据和本地命令见 [CONTRIBUTING.md](CONTRIBUTING.md)。PR 工作流 `.github/workflows/validate-index.yml` 会在 Windows 上构建项目、运行回归，先调用确定性生产验证器，再执行实时 Steam 验证；在线真实性检查是补充边界，不能替代运行时的确定性安全验证。

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

Bridge 测试覆盖：

- 首次运行只建立每用户基线，不修改 `_mod`；
- 基线包含尚未下载的当前订阅，之后下载完成也不自动开启；
- 新合法 DLL 自动写入 `_mod` 并在同次启动建立联接；
- 玩家手动关闭不反弹，取消后重新订阅同一 ID 不恢复；
- 普通 JSON、无/非法 manifest、无 DLL 与不安全重解析点不自动开启；
- 未下载、更新中或文件暂不可用的项目保持可重试；
- `subscribedby` 精确过滤当前用户，排除其他用户和取消订阅残留；
- 当前用户已订阅但安装 manifest 缺失或版本不匹配时不联接；
- ACF 缺失、损坏、超限时 fail-closed；
- 状态文件损坏、超限、身份不匹配或重解析点时 fail-closed；
- `_mod` 缺失、超限、读取失败或重解析点时 fail-closed；
- `_mod` 写入失败不提交 seen，已有 ID 不重复写入；
- pending 事务中断后保守恢复；
- 多 Steam 用户的基线、状态和 `_mod` 互不影响；
- Bridge 只删除规范 ID 的目录重解析点，不删除普通目录、非规范入口或 Steam 源文件；
- BepInEx 风格递归扫描可穿过联接发现 DLL；
- `mklink` 命令元字符路径被拒绝；
- `libraryfolders.vdf` 跨库定位。

Mod Manager 集成测试覆盖：

- 纯数字 ID、两种官方链接、尾部斜杠、额外参数和 URL 编码的规范化；
- HTTP、伪域名、user-info、非默认端口、错误路径、fragment、重复/缺失 `id`、零、溢出及 Unicode 数字的拒绝；
- 工坊声明（包括纯空白和非法链接）不会回退直接 DLL 下载；
- 重复 Mod ID、重复规范化 Workshop ID、非法/null 条目及显示字段错误类型会拒绝整份索引；
- Steam 分批/去重、固定 endpoint/表单、有限重试、严格 JSON/UTF-8/字段类型和响应大小限制；
- HTML 实体、BBCode、控制/格式字符、空白、emoji 与显示长度清理；
- 显式索引文案优先、实时数据、24 小时缓存、过期缓存与离线安全回退；
- 错误 AppID、空标题、ID 不匹配、缺失/不可用项目和在线网络失败；
- `IndexClient` 的验证/补全/镜像顺序，以及 CLI 离线和在线退出码；
- `ModManager.exe` 内嵌 Bridge、提取、SHA-256 校验和损坏后修复；
- MainForm 的基线/下一次启动/手动关闭文案与 guide、banner、列表、状态栏布局；
- ModCard 忙碌状态、缺失显示资料回退与工坊/旧直装按钮回归。

`--validate-index <path>` 成功时返回 `0` 并输出条目数；解析、格式、重复或 I/O 失败时输出错误并返回非零。追加 `--verify-workshop` 会使用官方 API 实时验证所有工坊项目；成功输出验证数量，项目错误或网络错误返回非零。CI 必须使用这两个入口，避免维护与运行时分叉的校验逻辑。

Bridge 测试会调用 Windows `mklink /J`，因此不能在非 Windows 环境运行。

## 使用真实 Steam 结构验证

真实验证只能对副本执行写入测试。至少复制：

```text
<SteamLibrary>/steamapps/workshop/appworkshop_1991040.acf
<SteamLibrary>/steamapps/workshop/content/1991040
%USERPROFILE%/AppData/LocalLow/PakyiGame/StudentAge/Saves/<SteamID64>/_mod
```

在临时目录中构造 `BridgeOptions`，验证当前账号 `subscribedby`、残留目录、首次基线、新项目自动启用、同次联接、手动关闭、取消后重订阅及下载未完成/版本不匹配。不得对真实 `_mod` 或真实 `_workshop_bridge_state.json` 做破坏性测试。

## 发布资产

### 管理器安装（推荐）

只发布 `ModManager.exe` 即可。它会在安装官方 BepInEx 包后覆盖部署当前内嵌 Bridge，所以基础 ZIP 中即使没有 Bridge，玩家通过管理器安装仍能得到完整前置。

### 手动解压前置

手动解压用户需要 ZIP 自带且只新增：

```text
BepInEx/patchers/StudentAge.WorkshopBridge.dll
```

不能发布第二份 `Mono.Cecil.dll`。

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

1. 修改 `StudentAgeModManager.csproj` 的 `Version` / `AssemblyVersion` / `FileVersion`（如本次需要）；
2. 运行全部构建和测试；
3. 运行 `build_release.ps1`；
4. 检查 ZIP 仅在基础包上新增 `BepInEx/patchers/StudentAge.WorkshopBridge.dll`，且不含额外 `Mono.Cecil.dll`；
5. 确认 `ModManager.exe` 仍含 `StudentAgeModManager.Resources.StudentAge.WorkshopBridge.dll`；
6. 确认 `git diff --check`、目标分支、未跟踪文件和发布资产哈希；
7. 创建 Release 并上传对应资产。

## 编码注意事项

- `WebClient.Encoding` 必须显式使用 UTF-8，否则中文索引可能按系统 ANSI 解码。
- 含非 ASCII 内容的 PowerShell 5.1 脚本应保存为 UTF-8 BOM；`build_release.ps1` 特意只使用 ASCII 文本。
- 不要提交 `bin/`、`obj/` 或 `release_assets/`。
