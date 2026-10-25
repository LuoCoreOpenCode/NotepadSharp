# NotepadSharp

基于 **WPF + Scintilla.NET** 的轻量级 Windows 文本编辑器，功能对标 [Notepad--](https://gitee.com/cxasm/notepad--)，
窗口停靠直接复用 Visual Studio 的停靠内核，拖标签拆窗格、浮窗停靠的手感和 VS 一致。

## 环境

| | |
| --- | --- |
| 运行 | Windows x64 + .NET 10 **桌面**运行时 |
| 开发 | .NET 10 SDK（`dotnet build` 即可，不需要本机安装 Visual Studio） |

停靠内核 `Microsoft.VisualStudio.Shell.ViewManager.dll` 及其 11 个依赖已内联在 `NotepadSharp/VsDlls/`，
编译期通过 `HintPath` 引用、发布时原样拷到输出目录，运行时由 `Services/VsShellBootstrap.cs` 用
`AssemblyResolve` 从那里补齐。**不要把这些 dll 从仓库里删掉**，否则程序在没有装过 VS 的机器上起不来。

## 构建与运行

```powershell
dotnet run --project NotepadSharp/NotepadSharp.csproj
```

发布安装包（需要 Inno Setup 6）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

详细流程、产物说明和踩坑清单见 [打包发布.md](打包发布.md)。

## 功能

- **编辑**：多标签页、标签拖拽换序、只读/锁定标签、列编辑（多行同时插入）、行号与宏
- **停靠**：标签拖到编辑器右/下边缘拆窗格、拖出成浮窗、浮窗拖回停靠，全部走 VS 的 ViewManager
- **查找替换**：当前文件与整个目录（正则、大小写、全词），结果面板可点击跳转到行列
- **编码**：UTF-8 / UTF-8-BOM / GBK / UTF-16 自动识别，手动转换与重新载入，二进制查看
- **语法高亮**：30+ 语言（Scintilla 词法器），随扩展名自动切换
- **对比**：内置两文件逐行对比 + 目录对比，也可调起外部对比工具
- **工具**：Markdown 实时预览、行操作（排序/去重/大小写/注释切换）、计算哈希、JSON/XML 格式化
- **界面**：亮/暗主题、文件树侧栏、书签、代码折叠、缩放、状态栏行列与编码信息

## 自动更新

程序启动 5 秒后静默检查一次，帮助菜单里也可以手动检查。清单固定读
`https://gitee.com/<owner>/<repo>/raw/<branch>/release/latest.json`，
安装包按清单里的 `file` + `sha256` 下载校验，再拉起 Inno Setup 静默安装并重启。

- 代码位置：`NotepadSharp/Services/UpdateService.cs`
- 换仓库时要改 `GiteeOwner` / `GiteeRepo` / `ManifestBranch` 三个常量
- 调试时可用环境变量 `NOTEPADSHARP_UPDATE_MANIFEST` 指向别的清单地址

## 目录结构

```
NotepadSharp/
  Controls/       Scintilla 编辑器封装、查找替换面板、目录搜索面板
  Converters/     WPF 值转换器
  Models/         标签页、搜索结果等数据模型
  Resources/      主题字典（含 VS 停靠的运行时 XAML 主题）
  Services/       编码/词法/主题/设置/目录搜索/差异/停靠/自动更新
  Views/          对比窗口等独立窗口
  VsDlls/         内联的 VS 停靠内核
packaging/        Inno Setup 脚本与中文语言文件
release/          更新清单 latest.json
build.ps1         一键 publish + 编译安装包 + 生成清单
```

## 许可

代码可自由使用。`VsDlls/` 下的 dll 属于 Microsoft Visual Studio 组件，版权归 Microsoft，
此处内联仅供内部使用；对外分发前需要确认授权或改为依赖本机 VS 安装路径。
