## 投稿内容

- Mod 名称：
- `mods.json` 中的 Mod ID：
- Steam 工坊页面（真实链接）：
- 源码仓库：
- 作者与许可证/分发授权：
- 本次新增或更新的内容：

## 测试结果

请说明已验证的项目（订阅、下载完成后的下一次启动、更新、取消订阅等），并粘贴本地索引验证结果：

```text
Index validation passed: ...
```

## 检查清单

- [ ] `workshopId` 是纯数字 ID，或两种允许的 `https://steamcommunity.com/.../filedetails/?id=...` 官方链接之一。
- [ ] 链接只使用官方域名、默认 HTTPS 端口，且恰好有一个小写 `id` 参数；没有 user-info 或 fragment。
- [ ] Mod ID 不与现有条目重复（忽略大小写）。
- [ ] Workshop ID 规范化后不与现有条目重复。
- [ ] 工坊包包含合法的 `workshop-plugin.json` 和固定的 `BepInEx/plugins` 目录。
- [ ] 已提供可审查源码及作者/许可证或分发授权信息。
- [ ] 已运行 `dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj -c Release -- --validate-index .\mods.json`。
- [ ] 本 PR 未提交 DLL、可执行文件、Steam 内容、`bin/`、`obj/` 或 `release_assets/`。

提交前请阅读 [CONTRIBUTING.md](../CONTRIBUTING.md)。任一非法或重复条目都会让运行时和 PR CI 拒绝整份索引。
