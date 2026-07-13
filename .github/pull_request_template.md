## Mod 基本信息

- Mod 名称：
- Workshop ID：
- Steam 工坊页面：
- 源码仓库：
- 作者：
- 许可证或分发授权：
- 这是：新增收录 / 更新已有条目（删除不适用项）

## 管理器中的显示方式

请选择一种：

- [ ] 名称和简介直接使用 Steam 工坊内容；
- [ ] 在 `mods.json` 中自定义名称和简介。

如果选择自定义，请填写：

- 显示名称：
- 简短简介：

## 我已经测试

- [ ] 工坊项目已经公开，其他玩家可以打开；
- [ ] 项目属于《学生时代》（AppID `1991040`）；
- [ ] 工坊包中有 `workshop-plugin.json`；
- [ ] DLL 位于 `BepInEx/plugins`；
- [ ] 订阅并下载完成后，启动游戏可以正常接入；
- [ ] Mod 的主要功能已经测试；
- [ ] 关闭或取消订阅后，下次启动不会继续加载。

如果运行过本地索引检查，可以把结果粘贴在这里；没有本地开发环境也可以留空，GitHub 会自动检查：

```text
Index validation passed: ...
Steam Workshop verification passed: ...
```

## 提交前确认

- [ ] `mods.json` 中的 `id` 和 Workshop ID 没有与现有条目重复；
- [ ] Workshop ID 写在双引号中，例如 `"1234567890"`；
- [ ] PR 的目标分支是 `test`；
- [ ] 本 PR 没有提交 DLL、EXE、Steam 下载内容、`bin/`、`obj/` 或 `release_assets/`；
- [ ] 上面的 Steam 页面、源码和授权信息真实有效。

不确定如何填写时，请查看面向 Mod 作者的 [投稿教程](../CONTRIBUTING.md)。自动检查失败后可以直接继续修改当前 PR，不需要重新提交。
