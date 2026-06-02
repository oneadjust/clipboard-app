# AI 复刻提示词：Windows 托盘剪贴板历史工具

把下面这段提示词复制给 AI，即可复刻这个小项目。

```text
请帮我做一个 Windows 桌面端剪贴板历史工具，使用 .NET 9 + WPF 开发。

项目目标：
做一个轻量、极简、常驻托盘的剪贴板历史工具。它可以自动记录用户复制过的文字和图片，用户可以从历史列表中快速复制回剪贴板。程序需要能长期后台运行，避免重复记录相同内容，并且交互尽量接近 Windows 原生托盘小工具。

核心功能：
1. 程序启动后常驻 Windows 托盘，不在任务栏显示。
2. 左键点击托盘图标显示或隐藏主窗口。
3. 右键点击托盘图标显示菜单，包含：
   - 显示窗口 / 隐藏窗口
   - 开机自启
   - 清空历史
   - 退出
4. 主窗口展示剪贴板历史列表。
5. 支持记录文字剪贴板内容。
6. 支持记录图片剪贴板内容，图片要保存到本地文件，并在列表中显示缩略图。
7. 点击某条历史记录时，把对应文字或图片复制回系统剪贴板。
8. 点击历史记录复制时，不应该改变历史记录顺序。
9. 外部应用再次复制已存在的内容时，不新增记录，只更新时间，并把这条记录移动到顶部。
10. 右键点击历史项时，显示该项的删除按钮。
11. 支持搜索文字历史。
12. 自动保留最近 3 天历史，启动时清理过期记录和对应图片文件。
13. 程序必须单实例运行，重复启动 exe 时只唤醒已有实例，不创建多个后台进程。
14. 提供 win-x64 发布包。

技术栈要求：
- .NET 9
- WPF
- Windows Forms NotifyIcon 用于托盘图标
- SQLite，推荐 Microsoft.Data.Sqlite
- Win32 AddClipboardFormatListener 监听剪贴板变化
- Windows Registry 当前用户 Run 项实现开机自启

推荐项目结构：
- App.xaml / App.xaml.cs
  - 程序入口
  - 创建托盘图标
  - 创建托盘右键菜单
  - 处理单实例和唤醒已有实例

- MainWindow.xaml / MainWindow.xaml.cs
  - 主窗口 UI
  - 历史列表
  - 搜索框
  - 点击复制
  - 右键删除
  - 清空历史
  - 剪贴板事件处理

- Models/ClipboardEntry.cs
  - 剪贴板历史模型
  - 字段包括 Id、Type、ContentHash、Text、ImagePath、CreatedAt
  - 支持 INotifyPropertyChanged

- Services/ClipboardWatcher.cs
  - 使用 AddClipboardFormatListener 监听 WM_CLIPBOARDUPDATE

- Services/ClipboardStore.cs
  - 管理 SQLite 数据库
  - 保存和加载历史
  - 保存图片文件
  - 生成内容 hash
  - 清理过期记录

- Services/StartupService.cs
  - 管理开机自启

- Services/WindowBackdrop.cs
  - 可选：实现窗口半透明/模糊背景

SQLite 存储设计：
数据库位置：
%LOCALAPPDATA%\ClipboardApp\history.db

图片目录：
%LOCALAPPDATA%\ClipboardApp\images\

表结构：
CREATE TABLE IF NOT EXISTS ClipboardEntries (
    Id TEXT PRIMARY KEY,
    Type INTEGER NOT NULL,
    ContentHash TEXT NOT NULL UNIQUE,
    Text TEXT NULL,
    ImagePath TEXT NULL,
    CreatedAtTicks INTEGER NOT NULL
);

字段说明：
- Id：记录唯一 ID。
- Type：记录类型，0 表示文字，1 表示图片。
- ContentHash：内容 hash，用于去重。
- Text：文字内容。
- ImagePath：图片文件路径。
- CreatedAtTicks：记录时间，用于排序和过期清理。

去重策略：
对剪贴板内容生成 SHA-256 hash。

文字：
ContentHash = "text:" + SHA256(UTF8(text))

图片：
先保存为 PNG 文件。
ContentHash = "image:" + SHA256(image file bytes)

当监听到外部剪贴板变化：
1. 如果 hash 不存在，插入新记录到顶部。
2. 如果 hash 已存在，不新增记录，只更新 CreatedAt，并移动到顶部。
3. 保存到 SQLite。

内部点击复制保护：
点击历史项复制时，程序本身会调用 Clipboard.SetText 或 Clipboard.SetImage，这也会触发剪贴板变化事件。

为了避免点击历史项后改变排序：
1. 点击历史项复制前，记录该项 ContentHash。
2. 设置一个短时间抑制窗口，比如 2 秒。
3. 剪贴板监听器在窗口期内收到相同 hash 时，判定为内部复制。
4. 内部复制事件直接忽略，不更新数据库，不改变历史顺序。
5. 时间窗口过期后恢复正常监听。

单实例方案：
使用命名 Mutex：
Local\ClipboardApp.SingleInstance

第一个实例正常运行。

后续实例如果发现 Mutex 已存在：
1. 通过 EventWaitHandle 通知已有实例显示窗口。
2. 自己立即退出。

唤醒事件名：
Local\ClipboardApp.Activate

UI 设计要求：
1. 主窗口是小型托盘弹窗，不做复杂页面。
2. 窗口无边框、置顶、靠近系统托盘弹出。
3. 主窗口只保留标题、数量提示、搜索框、历史列表。
4. 清空、开机自启、退出等管理功能放到托盘右键菜单，不放在主窗口里。
5. 托盘图标使用极简白色线性图标，风格接近 Windows 状态栏输入法、网络、声音图标。
6. 滚动条使用细的半透明滑块，不要使用默认白色滚动条。
7. 历史项以卡片形式展示，文字显示预览，图片显示缩略图。

发布命令：
dotnet publish ClipboardApp\ClipboardApp.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish\win-x64

发布包需要包含：
- ClipboardApp.exe
- e_sqlite3.dll
- Assets\App.ico
- WPF 运行需要的原生 dll

安全注意：
当前版本使用本地明文 SQLite 存储剪贴板历史。剪贴板可能包含敏感信息。后续可以考虑：
- 暂停记录
- 敏感内容过滤
- Windows DPAPI 加密文字和图片
- 收藏/置顶
- 快捷键唤醒
- GitHub Releases 发布 exe

最终交付：
1. 可运行的 WPF 项目源码。
2. README.md，说明功能、技术方案、使用方法、构建命令。
3. win-x64 发布包。
4. 确保 dotnet build 通过，0 警告 0 错误。
```

## 一句话版本

```text
做一个 Windows 托盘剪贴板历史工具，使用 WPF 监听系统剪贴板，记录文字和图片到 SQLite，通过 SHA-256 hash 去重，外部重复复制只更新时间并移动到顶部，内部点击历史复制不改变顺序，支持搜索、删除、清空、开机自启、单实例唤醒，并发布 win-x64 自包含 exe。
```
