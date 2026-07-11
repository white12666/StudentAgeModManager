# StudentAge Mod 管理器

为《学生时代》安装一次 **BepInEx + 创意工坊 DLL 桥接器**。完成首次前置后，正式 DLL Mod 可以像普通创意工坊项目一样由 Steam 订阅、更新和卸载，不再需要每个版本都手动复制 DLL。

## 📥 下载

**[下载 ModManager.exe](https://github.com/white12666/StudentAgeModManager/releases/latest/download/ModManager.exe)**

- 无需安装，下载后直接运行（Windows 10 / Windows 11）
- 可以放在任意目录；工具会自动定位《学生时代》
- 管理器内已包含与当前版本配套的 Workshop Bridge，不需要单独下载 Bridge DLL

## 🚀 推荐使用流程

### 首次准备（只需一次）

1. 完全退出游戏。
2. 运行 `ModManager.exe`。
3. 点击顶部黄条的「**一键安装完整前置**」。
4. 工具会安装 BepInEx，并把桥接器部署到：
   `BepInEx/patchers/StudentAge.WorkshopBridge.dll`。

如果电脑上已有 BepInEx，按钮会变成「**安装工坊 DLL 支持**」，只补装或更新 Bridge，不会重新下载整套前置。

### 以后安装 DLL Mod

1. 在 Steam 创意工坊订阅支持 Bridge 的 DLL Mod。
2. 启动游戏，进入游戏原生 **Mod 页面**。
3. 启用刚订阅的项目。
4. 按游戏提示重启。

之后的下载、更新和取消订阅均由 Steam 处理。Bridge 只在游戏启动时接入当前已启用项目，不会复制或接管工坊文件。

## 🔌 Bridge 如何工作

游戏原生 Mod 系统会保存玩家启用的 Workshop ID，但本身只读取 JSON 配置，不会加载 DLL。Bridge 作为 BepInEx Preloader Patcher，在 BepInEx 扫描插件之前执行：

```text
Steam 工坊项目/BepInEx/plugins
                 │
                 └──目录联接──> 游戏目录/BepInEx/plugins/.workshop/<WorkshopId>
```

只有同时满足以下条件的项目才会接入：

- ID 存在于游戏原生 `_mod` 启用列表；
- 项目已经由 Steam 下载；
- 根目录包含有效的 `workshop-plugin.json`；
- 固定目录 `BepInEx/plugins` 中至少有一个 DLL。

禁用或取消订阅后，Bridge 会在下次启动时删除对应的**联接入口**。Steam 原始目录及其中的文件不会被 Bridge 删除。

## ✨ 功能

| 功能 | 说明 |
|---|---|
| 一键安装完整前置 | 安装 BepInEx，并部署内嵌的 Workshop Bridge |
| Bridge 自动修复/升级 | 已安装 BepInEx 时，只需点击一次即可补齐当前 Bridge |
| Steam 原生分发 | 工坊负责 DLL Mod 的下载、更新与卸载 |
| 复用游戏原生开关 | 只桥接玩家在游戏 Mod 页面明确启用的项目 |
| 安全声明校验 | 严格校验 manifest，拒绝自定义绝对路径和 `..` 路径 |
| 旧版直装兼容 | 没有 `workshopId` 的旧索引条目仍可暂时按原流程安装 |
| 国内网络回退 | 下载前置或旧版 GitHub 资产时可自动切换镜像 |

索引中带 `workshopId` 的项目不会回退成直接 DLL 安装：管理器只打开它的 Steam 页面。若检测到旧版直装文件，可用「清理旧安装」移除后再改用工坊版。

## 🔐 安全提示

DLL Mod 是可执行代码，权限与游戏进程相同。请只订阅可信作者发布的项目。

Bridge 不会自动加载所有订阅内容，也不会联网下载 DLL。它只接受：

1. 游戏原生页面已启用的 Workshop ID；
2. 带有合法 `workshop-plugin.json` 的项目；
3. manifest 中固定的 `BepInEx/plugins` 路径。

若 Bridge 无法确定当前用户的 `_mod` 启用列表，或暂时找不到 Workshop 根目录，会安全停用已有的规范工坊联接，本次不加载 DLL 工坊项目；它不会沿用可能属于其他 Steam 用户的旧启用集合。

`.workshop` 中的普通目录、普通文件、非规范 ID 项目以及可疑重解析点都会保留并记录错误，不会递归删除。

## ❓ 常见问题

**Q：点击安装提示“游戏正在运行”？**
先完全退出游戏。游戏运行期间管理器不会覆盖 BepInEx 或 Bridge DLL。

**Q：订阅后 DLL Mod 没生效？**
确认已在游戏原生 Mod 页面启用该项目，并按提示**完全重启游戏**。只订阅但未启用不会加载。

**Q：顶部显示“Bridge 缺少或需更新”？**
关闭游戏，点击「安装工坊 DLL 支持」。管理器会用内嵌版本覆盖旧 Bridge，并进行 SHA-256 校验。

**Q：如何查看 Bridge 运行结果？**
检查：

```text
游戏目录/BepInEx/WorkshopBridge.log
游戏目录/BepInEx/LogOutput.log
```

`WorkshopBridge.log` 会列出启用 ID 数、已建立联接、已清理联接、跳过项及错误。

**Q：取消订阅会不会留下 DLL？**
Steam 删除工坊内容后，Bridge 会在下次启动时清理对应联接。Bridge 不会复制 DLL 到其他目录。

**Q：杀毒软件报警？**
管理器会写入 BepInEx 目录，Bridge 会调用 Windows `mklink /J` 创建目录联接，因此少数安全软件可能提示。代码完全开源；请从本仓库 Release 下载并自行核对。

**Q：以前手动安装的 DLL 怎么办？**
工坊版条目若显示“检测到旧版直装文件”，先点击「清理旧安装」，再订阅并启用工坊版，避免同一插件被加载两次。

## 📦 当前索引

当前推荐索引暂不列出旧版直装项目。作者发布真实的 `workshopId` 后，再以「订阅 / 查看工坊」条目加入索引；后续版本更新均由 Steam 负责。

---

开发者、维护者与 DLL Mod 作者请阅读 [DEVELOPMENT.md](DEVELOPMENT.md)。
