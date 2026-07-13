# 把你的 DLL Mod 加入管理器推荐目录

欢迎为《学生时代》制作 Mod！本指南会带你把 DLL Mod 加入 Mod Manager 的推荐目录。

先说明一件重要的事：**中央索引不是加载白名单。**只要你的工坊包符合 Workshop Bridge 格式，玩家即使直接从 Steam 订阅、没有在 Git 中收录，也可以正常接入和运行。

“收录”主要带来这些效果：

- Mod 会出现在管理器顶部的“Steam 创意工坊目录”；
- 玩家可以从管理器打开你的工坊页面；
- 你可以在索引中自定义管理器显示的名称和简介；
- 已收录并已接入时，目录信息和本地安装状态会合并成一张卡片。

## 最短投稿流程

**最推荐的投稿方式是 PR**，因为修改内容可以直接接受自动检查和合并。默认流程只有 5 步：

1. 把符合格式的 DLL Mod 发布到《学生时代》Steam 创意工坊；
2. 取得真实的 Workshop ID；
3. Fork（复制）本仓库，并在 `mods.json` 中添加一条记录；
4. 向本仓库的 **`main` 分支**提交 Pull Request（简称 PR，也就是合并申请）；
5. 等待自动检查和维护者审核。

如果确实不会或不方便提交 PR，也可以使用后文的 Issue 备用方式。Issue 需要维护者代为核对、编辑和测试，会增加维护工作量，因此处理速度可能较慢。

下面会逐步说明。

---

## 第 1 步：准备工坊 DLL 包

工坊项目中至少需要以下结构：

```text
<WorkshopItem>/
├─ workshop-plugin.json
└─ BepInEx/
   └─ plugins/
      └─ <你的插件目录>/
         ├─ <你的插件>.dll
         └─ 其他依赖.dll（如果需要）
```

`workshop-plugin.json` 内容固定为：

```json
{
  "schemaVersion": 1,
  "type": "bepinex-plugin",
  "pluginRoot": "BepInEx/plugins"
}
```

请确认：

- 至少有一个带 `BepInPlugin` 声明的 DLL；
- 工坊项目属于《学生时代》，Steam AppID 为 `1991040`；
- 工坊项目已经公开，其他玩家可以打开和订阅；
- 订阅、下载并启动游戏后，Workshop Bridge 可以正常接入；
- 有可供审核的源码仓库，并说明作者和许可证/分发授权。

更详细的包格式和 Bridge 原理见 [DEVELOPMENT.md](DEVELOPMENT.md#工坊-dll-包格式)。

---

## 第 2 步：找到 Workshop ID

打开你的 Steam 工坊页面，例如：

```text
https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890
```

其中：

```text
1234567890
```

就是 Workshop ID。

在 `mods.json` 中推荐直接填写纯数字字符串：

```json
"workshopId": "1234567890"
```

注意外面的双引号不能省略。以下写法是错误的：

```json
"workshopId": 1234567890
```

---

## 第 3 步：修改 `mods.json`

中央推荐目录文件位于仓库根目录：

```text
mods.json
```

### 最小模板

只填写内部 ID 和 Workshop ID 即可：

```json
{
  "id": "author-my-mod",
  "workshopId": "1234567890"
}
```

如果没有填写名称和简介，管理器会从 Steam 工坊读取。

### 推荐模板

如果希望自己控制管理器中的显示文字，可以添加 `name` 和 `description`：

```json
{
  "id": "author-my-mod",
  "name": "我的 Mod",
  "description": "一句话说明这个 Mod 的主要功能",
  "workshopId": "1234567890"
}
```

字段说明：

| 字段 | 是否必填 | 用途 |
|---|---:|---|
| `id` | 是 | 管理器内部使用的稳定唯一 ID，建议使用“作者-项目名” |
| `workshopId` | 是 | Steam Workshop ID，必须写成字符串 |
| `name` | 否 | 管理器中的显示名称；不填则读取 Steam 标题 |
| `description` | 否 | 管理器中的简短介绍；不填则读取 Steam 说明 |

`id` 发布后尽量不要修改。它不是下载地址，也不要求和 DLL 文件名完全相同。

### 放入 `mods` 数组

假设原来是：

```json
"mods": []
```

可以改成：

```json
"mods": [
  {
    "id": "author-my-mod",
    "name": "我的 Mod",
    "description": "一句话说明这个 Mod 的主要功能",
    "workshopId": "1234567890"
  }
]
```

如果数组中已经有其他 Mod，记得在相邻对象之间添加英文逗号：

```json
"mods": [
  {
    "id": "existing-mod",
    "workshopId": "1111111111"
  },
  {
    "id": "author-my-mod",
    "workshopId": "1234567890"
  }
]
```

同时把根对象中的 `updatedAt` 更新为当天日期：

```json
"updatedAt": "2026-07-13"
```

日期格式为：

```text
YYYY-MM-DD
```

### 不要再填写旧版直装字段

管理器已经不支持 DLL/ZIP 直装索引，请不要添加：

```json
"downloadUrl"
"assetType"
"installDir"
```

DLL 文件由 Steam Workshop 分发和更新。

---

## 第 4 步：通过 GitHub 提交

不熟悉 Git 命令也没关系，可以直接使用 GitHub 网页完成：

1. 打开本仓库并点击右上角 **Fork**；
2. 在自己的 Fork 中创建一个新分支；
3. 打开 `mods.json`，点击铅笔图标编辑；
4. 添加你的条目并提交修改；
5. 点击 **Contribute → Open pull request**；
6. 创建 PR 时，把目标分支（base branch）选择为 **`main`**。

当前正式版管理器读取的是：

```text
StudentAgeModManager/main/mods.json
```

因此 PR 请直接提交到 `main`；提交到其他分支不会进入正式推荐目录。

### PR 描述建议包含

可以直接复制下面的模板：

```markdown
## Mod 信息

- Mod 名称：
- Workshop ID：
- Steam 页面：
- 源码仓库：
- 作者：
- 许可证/分发授权：

## 测试情况

- [ ] 工坊项目已公开
- [ ] 工坊包包含 workshop-plugin.json
- [ ] DLL 位于 BepInEx/plugins
- [ ] 订阅并下载后可以正常接入
- [ ] 游戏内功能测试正常
- [ ] 取消订阅或关闭后不会继续加载
```

维护者主要会确认：项目确实属于你、源码和授权清楚、工坊包格式正确，并且不会误导玩家。

### 备用方式：提交 Issue

**首选仍然是 PR。** PR 可以直接运行检查并合并；Issue 则需要维护者代你修改 `mods.json`、处理冲突和运行测试，会明显增加维护难度。因此 Issue 只作为不会使用 PR 时的备用入口，处理优先级和速度可能低于信息完整的 PR。

如果确实无法提交 PR，可以打开：

[新建 Mod 收录 Issue](https://github.com/white12666/StudentAgeModManager/issues/new)

Issue 标题建议写成：

```text
[Mod 收录申请] 你的 Mod 名称
```

正文可以复制下面的模板：

```markdown
## Mod 信息

- Mod 名称：
- Workshop ID：
- Steam 页面：
- 源码仓库：
- 作者：
- 许可证/分发授权：

## 希望在管理器中显示

- 建议内部 ID：
- 显示名称（可选）：
- 一句话简介（可选）：

## 测试情况

- [ ] 工坊项目已公开
- [ ] 工坊包包含 workshop-plugin.json
- [ ] DLL 位于 BepInEx/plugins
- [ ] 订阅并下载后可以正常接入
- [ ] 游戏内功能测试正常
- [ ] 取消订阅或关闭后不会继续加载
```

请不要只留下 Mod 名称或上传一个 DLL。Workshop ID、公开 Steam 页面、源码和授权信息仍然需要完整提供。提交 Issue 也不代表一定收录；条目仍需通过与 PR 相同的格式、安全和真实性检查。

---

## 第 5 步：等待自动检查

提交 PR 后，GitHub 会自动运行检查。

检查通过时会看到绿色标记。常见检查内容包括：

- `mods.json` 是否是有效 JSON；
- `id` 和 Workshop ID 是否重复；
- Workshop ID 是否填写正确；
- 工坊项目是否公开可访问；
- 工坊项目是否属于 AppID `1991040`；
- Steam 是否返回了有效标题。

如果检查失败，不必重新创建 PR。直接在原分支继续修改并提交，PR 会自动重新检查。

如果使用 Issue 投稿，Issue 本身不会直接修改索引。维护者需要先核对资料并代为创建代码改动，之后才会运行相同的自动检查；资料不完整时会请投稿者补充。

---

<details>
<summary><strong>可选：在本地提前检查</strong></summary>


如果电脑安装了 .NET SDK 和 .NET Framework 4.8 targeting pack，可以在仓库根目录运行：

```powershell
dotnet build -c Release

dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj `
  -c Release -- --validate-index .\mods.json

dotnet run --project .\ModManager.Tests\StudentAgeModManager.Tests.csproj `
  -c Release -- --validate-index .\mods.json --verify-workshop
```

三个命令分别用于：

1. 确认项目可以构建；
2. 检查 `mods.json` 格式和重复项；
3. 向 Steam 查询项目是否公开、存在且属于正确游戏。

如果不方便安装开发环境，也可以直接提交 PR，再根据 GitHub 自动检查的提示修改。

</details>

---

## 常见问题

### 没有收录，我的 Mod 就不能运行吗？

不是。中央索引只是推荐目录，不是加载白名单。只要工坊包合法并且玩家已经订阅、下载和启用，Workshop Bridge 就可以接入。

未收录但已接入时，管理器会显示：

```text
Steam 工坊 · 未收录 · 已接入
```

### 收录后需要把 DLL 上传到 GitHub Release 吗？

不需要。DLL 由 Steam Workshop 分发，索引中也不接受 DLL 下载地址。

### 每次更新 Mod 都需要修改 `mods.json` 吗？

通常不需要。保持同一个 Workshop ID，Steam 会自动向订阅者分发更新。

只有在以下情况才需要再次提交：

- 更换了 Workshop ID；
- 想修改索引中固定的名称或简介；
- 需要修正索引信息。

### `name` 和 `description` 必须填写吗？

不必。省略后会读取 Steam 标题和说明。

如果填写了非空内容，管理器会优先显示索引中的文字。因此建议简介保持简短、客观，不要塞入下载链接或大段更新日志。

### 工坊项目暂时是私密的，可以先提交吗？

不建议。自动检查无法验证私密或仅好友可见的项目。请先公开，再提交 PR。

### 可以直接填写完整 Steam 链接吗？

可以，但更推荐纯数字 ID。支持的链接形式是：

```text
https://steamcommunity.com/sharedfiles/filedetails/?id=<WORKSHOP_ID>
https://steamcommunity.com/workshop/filedetails/?id=<WORKSHOP_ID>
```

管理器最终只保存并使用规范化后的数字 ID。

### 一个工坊项目里可以有多个 DLL 吗？

可以。依赖 DLL 可以和主插件一起放在 `BepInEx/plugins/<插件目录>` 中。固定插件目录中至少要有一个真正的 BepInEx 插件 DLL。

---

## 常见失败原因

| 提示或现象 | 通常原因 | 处理方法 |
|---|---|---|
| `workshopId 必须是 JSON 字符串` | Workshop ID 没有加双引号 | 改成 `"1234567890"` |
| Workshop ID 重复 | 已有条目使用同一个工坊项目 | 搜索 `mods.json`，确认是否已经收录 |
| Mod ID 重复 | `id` 与现有条目相同，大小写差异也算重复 | 换成稳定且唯一的 ID |
| Steam 验证失败 | 项目私密、已删除或网络暂时失败 | 确认项目公开后重新运行检查 |
| AppID 不正确 | 工坊项目不是发布在《学生时代》下 | 使用 AppID `1991040` 的项目 |
| JSON 无法解析 | 漏了逗号、引号或括号 | 使用编辑器格式化 JSON，检查报错行 |
| 名称没有按预期显示 | 索引中填写了固定 `name` | 修改/删除 `name`，或使用 Steam 标题 |

---

<details>
<summary><strong>进阶规则（普通作者可以跳过）</strong></summary>

为了避免一个坏条目影响所有玩家，管理器和自动检查会使用同一套规则检查完整索引：

- `schemaVersion` 必须为 `1`，并且必须存在 `mods` 数组；
- 每个条目都必须是对象，不能为 `null`；
- `id` 必须是非空字符串，不能带首尾空白、控制字符或超过 128 个字符；
- `id` 忽略大小写后必须唯一；
- `workshopId` 必须存在并且是非空字符串；
- Workshop ID 必须是非零 ASCII 十进制数字，且不能超出无符号 64 位整数范围；
- 规范化后的 Workshop ID 必须唯一；
- `name` 和 `description` 如果存在，只能是字符串或 `null`；
- 不允许通过大小写不同的字段名重复定义同一字段。

在线 Steam 检查还会确认：

- 项目公开且可以读取；
- 返回的 ID 与请求一致；
- 标题非空；
- `consumer_app_id == 1991040`。

Steam 返回的下载地址、文件名和路径不会进入索引。管理器只把 Steam 数据用于补全显示名称和简介。

</details>

---

## 提交前快速检查

```text
[ ] Workshop ID 是真实数字，并且写在双引号中
[ ] 工坊项目已经公开，属于 AppID 1991040
[ ] workshop-plugin.json 内容和位置正确
[ ] BepInEx/plugins 下有可用的插件 DLL
[ ] mods.json 中的 id 和 Workshop ID 没有重复
[ ] 没有添加 downloadUrl / assetType / installDir
[ ] 使用 PR 时，目标分支是 main
[ ] PR 或 Issue 包含 Steam 页面、源码和授权信息
[ ] 使用 Issue 时，已提供建议内部 ID 和完整测试情况
```

准备好后，优先提交 PR；确实无法使用 PR 时再提交 Issue。感谢你为《学生时代》Mod 社区贡献内容！
