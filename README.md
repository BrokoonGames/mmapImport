# MmapImport — ICC-CE 导图导入插件

把 MindManager `.mmap` 思维导图导入 [InkCanvasForClass Community Edition](https://github.com/InkCanvasForClass/community)（ICC-CE）白板的教学插件。

## 功能

- **双导入模式**
  - 「导入导图」：导图作为可交互元素上画布——按 MindManager 保存态还原（含折叠），点击卡片旁 **＋/－** 徽标逐层展开/收起，支持整体拖动缩放
  - 「导图上背景」：导图**全展开**铺在白板背景层（InjectBackgroundLayer），笔迹直接写在图上批注——当"纸"用
- **画布跟随同步**：漫游/双指手势时导图与墨迹同步移动缩放（笔迹锚点矩阵同步器，CanvasSync）
- **还原度**：主题填充色/边框色/加粗/字号、浮动批注、关系线（虚线弧）、备注大图与行内图标（PNG/JPEG）
- MindManager 2003 XML schema（ZIP 容器）全解析，含蛋白质课堂样本实测（71 主题/6 层）

## 安装

1. 下载 Release 中的 `top.brokoon.mmapimport.icpx`
2. 放入 ICC-CE 安装目录的 `PluginPackages` 文件夹（或用插件管理器安装）
3. 重启 ICC-CE → 白板工具栏出现「导入导图」「导图上背景」两个按钮

## 使用

- 点「导入导图」选择 `.mmap` → 折叠态还原；点 **＋** 展开分支，**－** 收起
- 点「导图上背景」→ 全展开铺底，笔模式直接在图上板书
- 漫游模式或双指手势：导图与笔迹一起移动缩放（画布上需至少一条笔迹作锚点）

## 构建

需 .NET SDK 8+。SDK 引用 ICC-CE community `net6` 分支源码（NuGet 包落后于宿主运行时 API，故用源码引用）：

```powershell
git clone -b net6 --depth 1 https://github.com/InkCanvasForClass/community community
git clone https://github.com/BrokoonNetwork/MmapImport MmapImport
dotnet build MmapImport/src/InkCanvas.Plugin.MmapImport.csproj -c Release -p:SdkRoot="<路径>/community"
.\pack.ps1   # 生成 top.brokoon.mmapimport.icpx
```

推送 `v*` tag 时 CI 自动构建并发布 Release。

## 许可

GPL-3.0（随宿主生态）。插件代码 100% 原创，未复制宿主源码，仅依赖其公开 PluginSdk 接口。
