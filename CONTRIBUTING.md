# 向中央 Mod 索引投稿

感谢为《学生时代》Mod 生态做贡献。中央索引位于 [`mods.json`](mods.json)。DLL Mod 作者可以通过 Pull Request 添加或更新自己的条目；管理器仍会在运行时重新验证整份索引，因此 CI 不是唯一安全边界。

## 投稿前准备

请先确认：

- 工坊项目确实属于 Steam AppID `1991040`；
- 工坊包符合 [`DEVELOPMENT.md`](DEVELOPMENT.md#工坊-dll-包格式) 中的 DLL Bridge 包格式；
- 工坊页面已公开或至少能由维护者访问；
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
- `workshopId` 不是 JSON 字符串/`null`，或字符串内容非法；
- 两个条目规范化后指向同一 Workshop ID。

因此，纯数字 `123` 与任何指向 ID `123` 的允许链接会被判定为重复。没有 `workshopId`，或值为 JSON `null`/空字符串的历史条目仍属于旧版直接下载兼容流程；新 DLL Mod 不应使用该流程。

## 修改 `mods.json`

工坊条目可以把旧版下载字段留空。下面只是模板；必须替换占位符，不能原样提交：

```json
{
  "id": "<UNIQUE_MOD_ID>",
  "name": "<显示名称>",
  "description": "<简短说明>",
  "repo": "<owner/repository>",
  "version": "workshop",
  "downloadUrl": "",
  "assetType": "",
  "installDir": "",
  "workshopId": "<WORKSHOP_ID>"
}
```

若需要帮助旧用户清理历史直装版本，可以暂时保留原来的 `installDir`；管理器会显示“清理旧安装”。不要为工坊条目填写新的 DLL 下载地址。

## 本地验证

要求 Windows、.NET SDK 和 .NET Framework 4.8 targeting pack。在仓库根目录执行：

```powershell
dotnet build -c Release
dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj -c Release
dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj -c Release -- --validate-index .\mods.json
```

最后一条命令调用与运行时 `IndexClient.ParseAndValidate(...)` 相同的生产验证逻辑。成功时会输出 `Index validation passed`；失败时返回非零退出码。

## 提交 Pull Request

1. 只修改必要的 `mods.json` 条目及相关文档；
2. 不要提交 DLL、可执行文件、Steam 下载内容、`bin/`、`obj/` 或 `release_assets/`；
3. 在 PR 中提供真实 Steam 工坊页面、源码仓库、作者/授权信息和测试结果；
4. 确认 Mod ID 与规范化后的 Workshop ID 均未重复；
5. 等待 `Validate central mod index` 检查通过并接受维护者审查。

来自 fork 的工作流仍应由维护者审查后运行。即使 CI 通过，维护者仍会核对页面归属、源码、授权和工坊包内容。
