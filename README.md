# StudentAge Mod 管理器

为《学生时代》安装一次 **BepInEx + 创意工坊 DLL Bridge**。完成前置后，DLL Mod 可以由 Steam 订阅、更新和卸载；Bridge 会在安全校验后，把符合条件的工坊 DLL 接入 BepInEx，而不会复制或接管 Steam 工坊文件。

## 📥 下载

**[下载 ModManager.exe](https://github.com/white12666/StudentAgeModManager/releases/latest/download/ModManager.exe)**

- 无需安装，下载后直接运行（Windows 10 / Windows 11）
- 可以放在任意目录；工具会自动定位《学生时代》
- 管理器内已包含与当前版本配套的 Workshop Bridge，不需要单独下载 Bridge DLL

## 🚀 推荐使用流程

### 首次准备（每个 Steam 用户一次）

1. 完全退出游戏。
2. 运行 `ModManager.exe`。
3. 点击顶部黄条的「**一键安装完整前置**」。
4. 工具会安装 BepInEx，并把 Bridge 部署到：
   `BepInEx/patchers/StudentAge.WorkshopBridge.dll`。
5. 安装后启动一次游戏。Bridge 0.2.0 会为**当前 Steam 用户**建立订阅基线。

建立基线时，当前已经订阅的所有项目只会被登记，**绝不会自动开启**。如果电脑上已有 BepInEx，按钮会变成「**安装工坊 DLL 支持**」，只补装或更新 Bridge，不会重新下载整套前置。

每个 Steam 用户都有独立基线。安装 Bridge 后如果尚未启动过游戏，就先订阅了 DLL 项目，那么该项目会在首次启动时进入基线，仍需按下文的“现有基线项目”方式手动开启。

### 以后安装新的 DLL Mod

1. 在基线建立后，通过 Steam 创意工坊订阅支持 Bridge 的 DLL Mod。
2. 等待 Steam **下载完成**。
3. 下一次启动游戏时，Bridge 会在 BepInEx 扫描插件前：
   - 把该项目加入当前用户的游戏原生 `_mod`；
   - 立即创建 Bridge 目录联接；
   - 让 DLL 在**这一次启动**中直接加载。

自动启用发生在“**Steam 下载完成后的下一次游戏启动**”。它不会在下载完成瞬间执行，因为此时 Mod Manager 和 Bridge 通常没有运行；Bridge 只在游戏启动的 Preloader 阶段执行。如果启动时项目仍在下载或更新，Bridge 会保持关闭，并在下载完成后的后续启动重试。

### 现有项目与手动关闭

- 基线中的现有 DLL 项目不会自动开启；需要在游戏“**本地**”页手动开启，再重启游戏。
- 玩家在游戏“本地”页关闭项目后，Bridge 不会重新开启它；重启后对应 DLL 不再接入。
- 取消订阅后再订阅同一个 Workshop ID，也不会强制恢复开启。
- 普通 JSON 工坊项目不会被 Bridge 自动开启，仍由游戏原生 Mod 系统处理。

之后的下载、更新和取消订阅均由 Steam 处理。Bridge 不下载 DLL，也不会把 DLL 复制到工坊目录以外的位置。

## 🔌 Bridge 如何工作

游戏原生 Mod 系统把启用的 Workshop ID 按行保存在当前用户的 `_mod` 中，但本身只读取 JSON 配置，不会加载 DLL。Bridge 是 BepInEx **Preloader Patcher**，在 Chainloader 扫描插件之前执行：

```text
Steam 工坊项目/BepInEx/plugins
                 │
                 └──目录联接──> 游戏目录/BepInEx/plugins/.workshop/<WorkshopId>
```

Bridge 0.2.0 还会在每个 Steam 用户的存档目录维护：

```text
_workshop_bridge_state.json
```

其中记录该用户已经见过的 Workshop ID。记录只会增长，因此手动关闭、取消订阅或重新订阅同一个 ID 都不会触发第二次自动开启。

项目只有同时满足以下条件才会接入：

- `appworkshop_1991040.acf` 的 `subscribedby` 明确表明**当前 Steam 账号**仍订阅该 ID；
- Steam 的已安装 manifest 与项目详情（以及存在时的最新 manifest）一致，确认下载完成且不在更新中；
- ID 已在游戏原生 `_mod` 中，或是基线建立后首次识别到的新合法 DLL 项目；
- 工坊项目根目录包含有效的 `workshop-plugin.json`；
- 固定目录 `BepInEx/plugins` 中至少有一个 DLL。

当前用户取消订阅后，即使工坊目录、Steam 的已安装记录或 `_mod` 仍有残留，Bridge 也不会继续接入。禁用、取消订阅或更新未完成时，Bridge 会在下次启动中删除对应的**联接入口**；Steam 原始目录及其中的文件不会被 Bridge 删除。

## ✨ 功能

| 功能 | 说明 |
|---|---|
| 一键安装完整前置 | 安装 BepInEx，并部署内嵌的 Workshop Bridge |
| Bridge 自动修复/升级 | 已安装 BepInEx 时，只需点击一次即可补齐当前 Bridge |
| Steam 原生分发 | 工坊负责 DLL Mod 的下载、更新与卸载 |
| 每用户安全基线 | 首次运行只登记现有订阅，不意外执行历史 DLL |
| 新 DLL 一次启动生效 | 下载完成后的下一次启动自动写入 `_mod` 并立即接入 |
| 尊重游戏原生开关 | 玩家手动关闭后不反弹，取消后重订阅也不强制恢复 |
| 当前用户严格过滤 | 不把残留目录或其他 Steam 用户的订阅当成当前授权 |
| 安全声明校验 | 严格校验 manifest，只接受固定 `BepInEx/plugins` 路径 |
| 中央索引整体验证 | 非法或重复条目会让整份索引被拒绝，不会静默跳过 |
| Steam 显示资料补全 | 名称/说明缺失时读取官方元数据，并支持缓存与离线安全回退 |
| 旧版直装兼容 | 没有 `workshopId` 的旧索引条目仍可暂时按原流程安装 |
| 国内网络回退 | 下载前置或旧版 GitHub 资产时可自动切换镜像 |

索引中的 `workshopId` 可以是纯数字 ID，也可以是两种官方 `https://steamcommunity.com/.../filedetails/?id=...` 链接。管理器会先把它规范化为纯数字，再自行构造受信任的 Steam 页面地址；索引提供的原始 URL 永远不会被直接打开。

带 `workshopId` 的项目不会回退成直接 DLL 安装。若 ID/链接非法、Mod ID 重复，或多个条目规范化后指向同一 Workshop ID，管理器会拒绝整份索引。若检测到旧版直装文件，可用「清理旧安装」移除后再改用工坊版。

### 工坊名称、说明与离线回退

工坊作者只需在中央索引提供稳定且唯一的内部 `id` 和合法的 `workshopId`。`name` 或 `description` 省略、为 `null`、空字符串或纯空白时，管理器会从 Steam 官方公开接口补全显示名称和说明；索引中任何非空文案始终优先，不会被 Steam 覆盖或清理。

Steam 文本只作为不可信的显示资料处理：管理器会解码 HTML 实体、移除 BBCode、控制字符、格式字符和多余空白，并限制标题和摘要长度。Steam 返回的 URL、文件名、路径、下载地址和安装信息全部忽略，不会改变 Workshop ID、安装目录、直装/工坊分流或 Bridge 行为。页面地址仍由管理器根据规范化数字 ID 构造固定官方链接。

元数据缓存位于游戏目录：

```text
BepInEx/ModManager/workshop-metadata.json
```

新鲜缓存可减少请求；需要刷新时，有效实时结果优先于旧缓存。Steam 临时不可用不会让已经通过确定性安全验证的索引失效：名称最终回退到内部 `id`，说明回退到“Steam 创意工坊项目”。管理器不会抓取 Steam 网页，也不会通过 GitHub 镜像代理 Steam 元数据接口。

## 🔐 安全提示

DLL Mod 是可执行代码，权限与游戏进程相同。**在基线建立后订阅新的合法 DLL 工坊项目，意味着同意它在下载完成后的下一次游戏启动执行代码。**请只订阅可信作者发布的项目。

Bridge 不会自动加载所有历史订阅，也不会联网下载 DLL。它只接受当前用户仍订阅、Steam 已确认下载完成、路径与声明合法，并且由玩家手动开启或按上述一次性规则自动开启的项目。

以下任一状态不明确时，Bridge 会 fail-closed：

- 当前 Steam 用户身份；
- 当前用户的 `subscribedby` 订阅列表；
- 下载完成状态；
- `_mod` 或每用户 Bridge 状态文件；
- Workshop 根目录或安全路径约束。

fail-closed 时，Bridge 会清理能确认由自己创建的规范联接，本次不加载 DLL 工坊项目；它不会沿用可能属于其他 Steam 用户的旧启用集合。

`.workshop` 中的普通目录、普通文件、非规范 ID 项目以及可疑重解析点都会保留并记录错误，不会递归删除。Bridge 绝不会递归删除 Steam 工坊源目录。

## ❓ 常见问题

**Q：自动启用到底发生在什么时候？**

Steam 下载完成后的下一次游戏启动。下载完成瞬间不会运行 Bridge；Bridge 是随游戏启动执行的 Preloader Patcher。

**Q：订阅后 DLL Mod 没生效？**

先确认 Bridge 已安装，并等待 Steam 下载完成。如果该项目在当前用户首次运行 Bridge 时已经订阅，它属于基线项目，需要在游戏“本地”页手动开启并重启。若它是基线之后的新订阅，请完全退出并重新启动游戏；下载未完成或正在更新时不会接入。

**Q：在游戏“本地”页关闭后还会自动打开吗？**

不会。该 Workshop ID 已被记为见过，手动关闭优先；取消订阅后重新订阅同一 ID 也不会恢复开启。

**Q：普通 JSON Mod 会被自动开启吗？**

不会。没有合法 DLL Bridge 声明的普通工坊项目不会被写入 `_mod`，继续由游戏原生系统管理。

**Q：点击安装提示“游戏正在运行”？**

先完全退出游戏。游戏运行期间管理器不会覆盖 BepInEx 或 Bridge DLL。

**Q：顶部显示“Bridge 缺少或需更新”？**

关闭游戏，点击「安装工坊 DLL 支持」。管理器会用内嵌版本覆盖旧 Bridge，并进行 SHA-256 校验。

**Q：如何查看 Bridge 运行结果？**

检查：

```text
游戏目录/BepInEx/WorkshopBridge.log
游戏目录/BepInEx/LogOutput.log
```

`WorkshopBridge.log` 会列出基线 ID 数、自动启用 ID 数、游戏启用 ID 数、已建立联接、已清理联接、跳过项及错误。

**Q：取消订阅会不会留下 DLL？**

Steam 负责删除工坊内容。即使 Steam 暂时留下目录或安装记录，Bridge 也会依据当前账号的 `subscribedby` 拒绝接入，并在下次启动清理对应联接。Bridge 不会复制 DLL 到其他目录。

**Q：杀毒软件报警？**

管理器会写入 BepInEx 目录，Bridge 会调用 Windows `mklink /J` 创建目录联接，因此少数安全软件可能提示。代码完全开源；请从本仓库 Release 下载并自行核对。

**Q：以前手动安装的 DLL 怎么办？**

工坊版条目若显示“检测到旧版直装文件”，先点击「清理旧安装」，再使用工坊版，避免同一插件被加载两次。清理旧安装不会取消 Steam 订阅或删除 Steam 工坊目录。

## 📦 当前索引

当前推荐索引暂不列出旧版直装项目。作者发布真实的 Workshop ID 后，可以按 [CONTRIBUTING.md](CONTRIBUTING.md) 提交纯数字 ID 或允许的官方链接；名称和说明可以交给 Steam 自动补全。PR 会先运行与管理器相同的确定性索引验证，再实时确认工坊项目公开可读、标题非空且属于 AppID `1991040`。加入后显示为「订阅 / 查看工坊」，后续版本更新由 Steam 负责。

---

DLL Mod 投稿请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)；Bridge 开发、维护与发布细节见 [DEVELOPMENT.md](DEVELOPMENT.md)。
