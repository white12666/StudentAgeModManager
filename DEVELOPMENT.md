# 开发与维护说明

面向玩家的使用说明见 [README.md](README.md)。

## 架构

```
[本仓库 main/mods.json] ← 作者发版后运行 update_index.ps1 更新
        ↓ raw.githubusercontent.com（自动镜像回退）
[ModManager.exe] → 对比 游戏目录/BepInEx/ModManager/installed.json
        ↓
   下载 GitHub Release 资产（自动镜像回退）→ BepInEx/plugins/<ModDir>/
```

设计要点：

- **无 GitHub API 依赖**：版本号和下载直链都写在 mods.json 里，规避 API 匿名限流；且 raw / release 链接都可走 ghproxy 类加速镜像，`api.github.com` 不行
- **镜像列表可热更**：镜像列表存 mods.json（exe 内置一份种子用于首次拉取），镜像挂了改 json 即可，不用发新版工具
- **状态记录**：`游戏目录/BepInEx/ModManager/installed.json` 记录每个 mod 的版本和文件清单；禁用的 mod 移到 `BepInEx/ModManager/disabled/`

## 日常维护流程

### 某个 Mod 发布了新版本

```powershell
.\update_index.ps1 -ModId StudentAgeEditorPlus   # 自动查该仓库 latest release
git add mods.json; git commit -m "update index"; git push
```

推送后所有用户打开管理器即可看到更新按钮。不带 `-ModId` 参数则检查所有条目。

### 新增一个 Mod

往 `mods.json` 的 `mods` 数组加一条：

```json
{
  "id": "唯一英文ID",
  "name": "显示名称",
  "description": "一句话简介",
  "repo": "white12666/仓库名",
  "version": "v1.0.0",
  "downloadUrl": "https://github.com/white12666/仓库名/releases/download/v1.0.0/xxx.dll",
  "assetType": "dll",
  "installDir": "BepInEx/plugins/插件目录名",
  "workshopId": ""
}
```

- `assetType`: `dll` = Release 资产是单个 DLL，放入 `installDir`；`zip` = 解压到 `installDir`
- `workshopId`: 填了的话「主页」按钮跳创意工坊，否则跳 GitHub 仓库

push 即上架，管理器 exe 不用改。

### 发布管理器新版本

1. 改 `csproj` 里的 `Version` / `AssemblyVersion` / `FileVersion`
2. `dotnet build -c Release`，产物 `bin/Release/net48/ModManager.exe`
3. 建 Release（tag 如 `v1.1.0`）上传 exe
4. 更新 mods.json 的 `manager.version`，push → 老用户启动时会弹更新提示

## 构建

```powershell
dotnet build -c Release
# 产物: bin/Release/net48/ModManager.exe（单文件，net48，无第三方依赖）
```

## 本地调试

exe 同目录放 `index_url.txt`，内容为本地 mods.json 路径或任意 URL，可覆盖默认索引地址。

## 编码注意事项（踩过的坑）

- `WebClient.Encoding` 默认是系统 ANSI（中文系统=GBK），下载 UTF-8 json 必须显式设 `Encoding.UTF8`，否则中文乱码
- 含中文的 `.ps1` 必须存成 **UTF-8 带 BOM**，否则 PowerShell 5.1 按 GBK 解析会报语法错误
- `.cs` 源文件统一 UTF-8 带 BOM，防止某些工具链把无 BOM 文件按 GBK 编译导致字符串字面量乱码

## Release 资产约定

| tag | 资产 | 用途 |
|---|---|---|
| `bepinex` | BepInEx-5.4.23-package.zip | 「一键安装 BepInEx」的下载源（纯净包：winhttp + doorstop + core） |
| `v*.*.*` | ModManager.exe | 玩家下载入口，`releases/latest/download/ModManager.exe` 永远指向最新 |
