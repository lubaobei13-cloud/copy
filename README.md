<img src="src/Copy.png" width="80" alt="Copy icon">

# Copy · 本地剪贴板历史

一个小巧的 Windows 剪贴板历史工具。文字、图片和文件路径保存在本机，支持搜索、置顶和再次复制。

## 功能

- 380 × 340 的小窗，可移动和调整大小。
- `Ctrl + Alt + V` 呼出或收起；关闭窗口后在系统托盘继续记录。
- 文字、图片、文件分类，以及搜索、置顶、删除和完整预览。
- 单张本地图片文件（包括微信的 PNG 文件路径）直接显示为图片。
- 按像素滚动，每格滚轮约 42 像素。
- 相邻重复内容去重；普通记录最多 1000 条，总内容容量 200 MB。
- 支持暂停记录；开机启动默认关闭，可在设置中打开。
- 不使用网络服务。数据保存在程序旁的 `data/` 文件夹。

## 运行

支持 Windows 10 / 11 x64，需要系统的 .NET Framework 4.8。
下载发布页的 Windows 压缩包，解压到可写文件夹，双击 `Copy.exe`。
点击历史卡片后，到目标窗口按 `Ctrl + V` 粘贴。

## 从源码构建

不需要 .NET SDK 或 NuGet 包。构建使用 Windows 的 .NET Framework 编译器和系统 `winsqlite3.dll`。

在项目文件夹内打开 PowerShell：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\Copy.exe
```

修改图标设计后重新生成并构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\generate-icon.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

## 测试

先退出运行中的 Copy，再执行以下命令。集成测试会短暂使用系统剪贴板，请在测试期间暂停复制操作；测试结束后尝试恢复原剪贴板。

```powershell
Start-Process .\Copy.exe -ArgumentList '--self-test' -Wait
Get-Content .\tests\latest-results.txt
```

测试覆盖数据库持久化、去重、容量清理、图片路径转换、历史迁移、三类内容的剪贴板读写及小窗滚动。

## 范围与限制

- 仅记录运行之后的剪贴板变化，无法找回之前或退出期间的历史。
- 文字按纯文本保存，图片不做 OCR。
- 普通文件和文件夹仅保存路径；单张本地图片保存像素。
- 单张图片最多 20 MB / 4000 万像素，单条文字最多 200 万字符。
- 应用明确标记禁止保存历史的内容会跳过。
- 截图工具自动写入剪贴板也会触发记录。要仅记录手动复制，应关闭该截图方式的自动复制；Copy 无法可靠判断每次写入是否由用户点击触发。

## 目录

`src/` 是 C#、XAML 与图标源码；`build.ps1` 编译程序；`generate-icon.ps1` 生成图标；`使用说明.txt` 提供完整操作说明。

`.gitignore` 排除了 `data/`、`backups/`、`work/` 和运行时测试数据。上传源码时只上传项目文件，不上传个人剪贴板历史。程序压缩包可单独放到 GitHub Releases。
