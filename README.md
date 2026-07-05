# StudentAge Mod 管理器

一个 exe 搞定所有 StudentAge mod 的下载、更新、启用/禁用、卸载。

## 用户使用方式

1. 下载 `ModManager.exe`（约 43KB，无需安装任何运行时，Win10/11 直接双击）
2. 放在任意位置运行（放游戏目录最稳，工具也会自动通过 Steam 找游戏目录）
3. 界面里点「安装」/「更新」即可；改动需重启游戏生效

## 架构

```
[中央索引仓库 mods.json] ← 作者发版后运行 update_index.ps1 更新
        ↓ raw.githubusercontent.com（自动镜像回退）
[ModManager.exe] → 对比 BepInEx/ModManager/installed.json → 下载 Release 资产（自动镜像回退）
```

- **无 GitHub API 依赖**：版本号和下载直链都写在 mods.json 里，规避 API 限流，且 raw/release 链接都可走 ghproxy 类镜像
- **镜像列表可热更**：镜像列表存在 mods.json 里，镜像挂了改 json 即可，不用发新版工具
- **状态记录**：`游戏目录/BepInEx/ModManager/installed.json` 记录每个 mod 的版本和文件清单；禁用的 mod 移到 `BepInEx/ModManager/disabled/`

## 开发者维护流程

发布新 mod 版本后：

```powershell
# 在索引仓库目录：
.\update_index.ps1 -ModId StudentAgeEditorPlus   # 自动查 latest release 更新清单
git add mods.json; git commit -m "update index"; git push
```

新增一个 mod：往 `mods.json` 的 `mods` 数组加一条（id/name/description/repo/version/downloadUrl/assetType/installDir），推送即可，所有用户下次打开管理器就能看到。

`assetType` 说明：
- `dll`：Release 资产是单个 DLL，放入 `installDir`
- `zip`：Release 资产是 zip，解压到 `installDir`

## 构建

```powershell
dotnet build -c Release
# 产物: bin/Release/net48/ModManager.exe（单文件）
```

## 本地调试

exe 同目录放 `index_url.txt`，内容为本地 mods.json 路径或任意 URL，即可覆盖默认索引地址。
