# StudentAge Mod 管理器

这是《学生时代》的 BepInEx 与 Steam 创意工坊 DLL Mod 管理器。

它可以：

- 一键安装 BepInEx 和 Workshop Bridge；
- 打开推荐 Mod 的 Steam 工坊页面；
- 显示已接入的工坊 DLL，包括没有进入推荐目录的项目；
- 识别手动放入 `BepInEx/plugins` 的本地 DLL；
- 启用或禁用本地 DLL，不会直接删除文件。

> 中央索引只是推荐目录，不是加载白名单。没有 Git 收录的合法工坊 DLL 也可以正常接入和运行。

## 📥 下载

**[下载 ModManager.exe](https://github.com/white12666/StudentAgeModManager/releases/latest/download/ModManager.exe)**

- 支持 Windows 10 / Windows 11；
- 下载后直接运行，不需要安装；
- 可以放在任意位置，管理器会尝试自动找到游戏目录。

## 🚀 三步开始使用

### 1. 安装前置

1. 完全退出游戏；
2. 运行 `ModManager.exe`；
3. 点击顶部黄色提示中的“**一键安装完整前置**”；
4. 安装完成后启动一次游戏。

管理器会安装 BepInEx，并把 Workshop Bridge 放到：

```text
BepInEx/patchers/StudentAge.WorkshopBridge.dll
```

如果已经安装 BepInEx，按钮会显示为“**安装工坊 DLL 支持**”，只补装或更新 Bridge。

### 2. 安装工坊 DLL Mod

你可以从管理器的推荐目录打开工坊页面，也可以直接打开作者提供的 Steam 链接。

```text
点击“打开工坊页面”
        ↓
在 Steam 中点击订阅
        ↓
等待下载完成
        ↓
下次启动游戏时接入
```

“打开工坊页面”只负责打开官方页面。订阅和取消订阅仍然在 Steam 中完成。

安装 Bridge 后的新订阅，通常会在下载完成后的下一次游戏启动自动启用。以前已经订阅的项目属于首次基线，可能需要先在游戏“本地”页手动开启，再重启游戏。

### 3. 管理 Mod

- 工坊 Mod：在 Steam 中订阅/取消，在游戏“本地”页开启或关闭；
- 手动 DLL：在 Mod Manager 中点击“启用”或“禁用”；
- 所有开关改动都建议在游戏关闭时进行，并在下次启动生效。

## 🖥️ 界面状态是什么意思

状态由来源、是否收录和当前接入/启用情况组成。

| 界面状态 | 含义 |
|---|---|
| `Steam 工坊 · 已收录 · 未接入` | 已进入推荐目录，但本机目前没有 Bridge 联接 |
| `Steam 工坊 · 已收录 · 已接入` | 已进入推荐目录，并已在本机接入；只显示一张合并卡片 |
| `Steam 工坊 · 未收录 · 已接入` | 没进推荐目录，但已经合法接入，可以正常运行 |
| `本地 · 未收录 · 已启用` | 手动安装的本地 DLL，目前位于插件目录 |
| `本地 · 未收录 · 未启用` | 本地 DLL 已被管理器移出插件目录 |
| `本地 · 未收录 · 路径冲突` | 启用区和禁用区同时有同名项目，需要手动处理 |

颜色规则：

- “已收录 / 已接入 / 已启用”为绿色；
- “未收录 / 未接入 / 未启用”为红色；
- 路径冲突为橙色。

### “收录”和“接入”不是一回事

- **收录**：是否进入 Git 推荐目录；
- **接入**：Workshop Bridge 是否已经把工坊 DLL 连接到 BepInEx 插件目录。

因此，“未收录 · 已接入”是正常状态，不代表 Mod 非法或无法运行。

## 🔌 本地 DLL 怎么显示

管理器会扫描：

```text
BepInEx/plugins
BepInEx/ModManager/disabled
```

只要 DLL 中有有效的 `BepInPlugin` 声明，就会显示名称、版本和路径。

推荐每个 Mod 使用独立目录：

```text
BepInEx/plugins/MyMod/
├─ MyMod.dll
└─ Dependency.dll
```

点击“禁用”时，整个目录会移动到：

```text
BepInEx/ModManager/disabled/MyMod
```

重新启用时会原样移回。管理器不会提供删除按钮，也不会自动删除你的 DLL。

以前由旧管理器直装的 DLL 与手动复制的 DLL 没有结构区别，现在都会统一显示为本地未收录 Mod。

## 🖱️ Mod 很多时怎么浏览

卡片超出窗口后会出现滚动条。

鼠标停在以下位置时都可以使用上下滚轮：

- 列表空白处；
- Mod 卡片；
- 卡片中的文字；
- 卡片按钮。

底部的投稿提示和运行状态栏是固定区域，不会跟随列表滚动，也不会被卡片覆盖。

## ❓ 常见问题

### 未收录的工坊 Mod 能运行吗？

可以。只要项目已订阅、下载完成、在游戏中启用，并且工坊包符合 Bridge 格式，就可以接入。

未收录项目成功接入后会显示：

```text
Steam 工坊 · 未收录 · 已接入
```

### 已收录的工坊 Mod 为什么显示“未接入”？

“未接入”不一定代表没有订阅，也可能是：

- Steam 还没有下载完成；
- 项目正在更新；
- 属于首次基线，尚未在游戏“本地”页开启；
- 玩家已经手动关闭；
- 工坊包缺少合法声明；
- Bridge 尚未随游戏启动运行。

### 为什么同一个已收录工坊 Mod 没有显示两次？

管理器会按 Workshop ID 合并推荐目录信息和本地 Bridge 联接：

```text
索引条目 + 本地工坊联接
          ↓
      一张合并卡片
```

没有进入推荐目录的工坊联接仍会单独显示在“本地已安装插件”中。

### 在游戏“本地”页关闭后还会自动开启吗？

不会。Bridge 会尊重手动关闭。取消后重新订阅同一个 Workshop ID，也不会强制再次开启。

### 普通 JSON Mod 会被 Bridge 当成 DLL 加载吗？

不会。没有合法 DLL Bridge 声明的普通工坊项目继续由游戏原生 Mod 系统管理。

### 点击按钮后为什么没有自动订阅？

按钮名称是“打开工坊页面”，它只打开 Steam 官方页面。请在 Steam 中自行点击订阅或取消订阅。

### 顶部显示“工坊 DLL 支持缺失或需更新”怎么办？

完全退出游戏，然后点击“安装工坊 DLL 支持”。管理器会用内嵌版本修复 Bridge。

### 如何查看加载结果？

检查：

```text
BepInEx/WorkshopBridge.log
BepInEx/LogOutput.log
```

### 取消订阅后会留下 DLL 吗？

Steam 负责删除工坊内容。Bridge 会在后续游戏启动时移除对应联接，不会复制或删除 Steam 的源目录。

### 杀毒软件为什么可能提示？

管理器会写入 BepInEx 目录，Bridge 会创建目录联接，少数安全软件可能提示。建议从本仓库 Release 下载并核对来源。

## 🔐 使用 DLL Mod 前请注意

DLL Mod 是可执行代码，权限与游戏进程相同。请只安装可信作者发布的项目，并优先查看源码和社区反馈。

管理器和 Bridge 会检查订阅、下载状态、manifest 与路径，但无法替你判断一个 Mod 的功能是否值得信任。

<details>
<summary><strong>想了解 Workshop Bridge 如何工作？</strong></summary>

游戏原生 Mod 系统会记录启用的 Workshop ID，但不会加载 DLL。Workshop Bridge 是 BepInEx Preloader Patcher，会在 BepInEx 扫描插件前运行。

```text
Steam 工坊项目/BepInEx/plugins
                 │
                 └──目录联接──> BepInEx/plugins/.workshop/<WorkshopId>
```

Bridge 只有在以下条件明确时才接入：

- 当前 Steam 用户确实订阅了该项目；
- Steam 已完成下载，项目不在更新中；
- 项目已在游戏原生 `_mod` 中启用，或是首次识别到的新合法订阅；
- 项目根目录有合法的 `workshop-plugin.json`；
- 固定的 `BepInEx/plugins` 目录中至少有一个 DLL。

任何关键状态不明确时，Bridge 会选择本次不加载，而不是猜测。

每个 Steam 用户都有独立的首次基线。第一次运行 Bridge 时，已有订阅只会被登记，不会全部自动开启；之后的新合法订阅才会在下载完成后的下一次启动自动开启。

详细实现见 [DEVELOPMENT.md](DEVELOPMENT.md)。

</details>

<details>
<summary><strong>想了解推荐目录和 Steam 显示资料？</strong></summary>

推荐目录来自仓库中的 `mods.json`。每个条目必须有唯一 `id` 和 Workshop ID。

如果索引没有填写名称或简介，管理器会从 Steam 读取并缓存显示资料；索引中明确填写的非空文字始终优先。

Steam 返回的下载地址、路径和文件名不会被用于安装。管理器只使用标题和说明，并自行根据数字 ID 构造官方工坊页面地址。

缓存位置：

```text
BepInEx/ModManager/workshop-metadata.json
```

</details>

## 🧩 Mod 作者

想让项目出现在推荐目录，可以点击管理器底部的“GitHub 提交收录”，或阅读：

- [面向 Mod 作者的投稿教程](CONTRIBUTING.md)
- [开发与维护说明](DEVELOPMENT.md)

当前 `test` 渠道读取 `test` 分支中的 `mods.json`。投稿时请把 PR 的目标分支选择为 `test`。
