# 向中央 Mod 索引投稿

感谢为《学生时代》Mod 生态做贡献。中央索引位于 [`mods.json`](mods.json)。DLL Mod 作者可以通过 Pull Request 添加或更新自己的条目；管理器仍会在运行时重新验证整份索引，因此 CI 不是唯一安全边界。

## 投稿前准备

请先确认：

- 工坊项目确实属于 Steam AppID `1991040`；
- 工坊项目已公开，并可由 Steam 官方公开 API 读取标题和项目详情；
- 工坊包符合 [`DEVELOPMENT.md`](DEVELOPMENT.md#工坊-dll-包格式) 中的 DLL Bridge 包格式；
- 有可供审查的源码仓库，并明确作者身份与许可证/分发授权；
- 已在干净的游戏环境中测试订阅、Steam 下载、下一次启动加载、更新和取消订阅。

## `workshopId` 允许的格式

`workshopId` 是 JSON **字符串**。请只提交以下三种形式之一：

```text
<WORKSHOP_ID>
https://steamcommunity.com/sharedfiles/filedetails/?id=<WORKSHOP_ID>
https://steamcommunity.com/workshop/filedetails/?id=<WORKSHOP_ID>
```

`<WORKSHOP_ID>` 只是文档占位符，提交时必须替换为真实的非零 ASCII 十进制数字；不要把尖括号一起提交。建议直接填写无前导零的纯数字 ID。

官方链接必须满足全部条件：

- scheme 为 `https`；
- host 精确为 `steamcommunity.com`，不能是子域名、伪后缀或其他域名；
- 只能使用默认 HTTPS 端口；
- 不含用户名/密码信息或 `#fragment`；
- 路径只能是上面两种 `filedetails` 路径，可带或不带末尾 `/`；
- 查询字符串中必须恰好有一个小写 `id` 参数；
- `id` 解码后必须是非零 ASCII 十进制数字，并且不能超出无符号 64 位整数范围；
- 可以附带其他合法查询参数。

不接受 HTTP、`steam://`、短链接、任意第三方 URL、错误路径、重复/缺失 `id`、Unicode 数字、零或溢出值。

管理器只从链接中提取并规范化数字 ID；它永远不会直接打开索引提供的原始 URL，而是自行构造受信任的 Steam 页面地址。

## 索引级拒绝规则

验证器会在使用索引前检查**全部条目**。出现任一问题时，整份索引都会被拒绝，不会跳过坏条目：

- `schemaVersion` 不是 `1`，或缺少 `mods` 数组；
- `mods` 中存在 `null` 条目；
- Mod `id` 不是 JSON 字符串，或为空、带首尾空白、超过 128 字符、含控制字符；
- Mod `id` 重复（忽略大小写）；
- `name` 或 `description` 存在但不是 JSON 字符串/`null`；
- `workshopId` 不是 JSON 字符串/`null`，或字符串内容非法；
- 两个条目规范化后指向同一 Workshop ID。

因此，纯数字 `123` 与任何指向 ID `123` 的允许链接会被判定为重复。没有 `workshopId`，或值为 JSON `null`/空字符串的历史条目仍属于旧版直接下载兼容流程；新 DLL Mod 不应使用该流程。

## 修改 `mods.json`

每个新工坊条目必须提供两个稳定字段：唯一的内部 `id`，以及合法的 `workshopId`。`name`、`description` 和旧版下载字段都不是工坊条目的必填项。最小模板如下；必须替换占位符，不能原样提交：

```json
{
  "id": "<UNIQUE_MOD_ID>",
  "workshopId": "<WORKSHOP_ID>"
}
```

如果希望由索引固定显示文案，可以另外提供：

```json
"name": "<显示名称>",
"description": "<简短说明>"
```

非空的索引 `name`/`description` 始终优先，管理器不会用 Steam 内容覆盖或清理它们。字段省略、为 JSON `null`、空字符串或纯空白时，管理器才会从 Steam 补全；Steam 文本会先移除 HTML 实体、BBCode、控制/格式字符和多余空白，并限制显示长度。若 Steam 暂时不可用，会依次回退到本地缓存、内部 `id` 和说明文字“Steam 创意工坊项目”，不会因此拒绝已经通过本地规则的索引。

若需要帮助旧用户清理历史直装版本，可以暂时保留原来的 `installDir`；管理器会显示“清理旧安装”。不要为工坊条目填写新的 DLL 下载地址。

Steam 返回的 URL、路径、文件名、下载地址或安装信息一律不会被采用；元数据只用于名称和说明。页面链接仍由管理器根据规范化后的数字 ID 自行构造，工坊条目也绝不会回退到 DLL 直装。

## 本地验证

要求 Windows、.NET SDK 和 .NET Framework 4.8 targeting pack。在仓库根目录执行：

```powershell
dotnet build -c Release
dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj -c Release
dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj -c Release -- --validate-index .\mods.json
dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj -c Release -- --validate-index .\mods.json --verify-workshop
```

第三条命令调用与运行时 `IndexClient.ParseAndValidate(...)` 相同的确定性生产验证逻辑。最后一条还会通过无需 API Key 的 Steam 官方接口实时确认每个项目存在且公开可读、返回 ID 一致、标题非空，并且 `consumer_app_id == 1991040`。成功时会同时输出 `Index validation passed` 和 `Steam Workshop verification passed`；网络错误、私有/已删除项目、错误 AppID 或其他验证失败都会返回非零退出码。

## 提交 Pull Request

1. 只修改必要的 `mods.json` 条目及相关文档；
2. 不要提交 DLL、可执行文件、Steam 下载内容、`bin/`、`obj/` 或 `release_assets/`；
3. 在 PR 中提供真实 Steam 工坊页面、源码仓库、作者/授权信息，以及离线和在线两种验证结果；
4. 确认 Mod ID 与规范化后的 Workshop ID 均未重复；
5. 等待 `Validate central mod index` 检查通过并接受维护者审查。

来自 fork 的工作流仍应由维护者审查后运行。即使 CI 通过，维护者仍会核对页面归属、源码、授权和工坊包内容。
