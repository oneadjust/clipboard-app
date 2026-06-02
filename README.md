# 剪切板

一个 Windows 托盘剪贴板历史工具。程序常驻通知区域，自动记录文字和图片剪贴板内容，支持从历史记录中一键复制回剪贴板，并自动清理 3 天以前的历史。

## 功能概览

- 托盘常驻：左键托盘图标显示或隐藏主窗口。
- 托盘菜单：右键托盘图标可显示/隐藏窗口、切换开机自启、清空历史、退出程序。
- 文字历史：记录纯文本剪贴板内容，支持搜索。
- 图片历史：记录图片剪贴板内容，并在列表中显示缩略图。
- 点击复制：左键点击历史项即可复制回系统剪贴板。
- 右键删除：右键历史项后显示删除按钮，可删除单条记录。
- 自动过期：启动时自动清理 3 天以前的记录和对应图片文件。
- 单实例运行：重复启动 exe 不会创建多个后台进程，而是唤醒已有实例。
- 重复内容去重：重复复制同一内容时不会新增多条记录。

## 使用方法

直接运行发布包：

```text
publish/win-x64/ClipboardApp.exe
```

启动后程序默认进入 Windows 托盘，不一定立即显示窗口。

- 左键托盘图标：显示或隐藏剪贴板窗口。
- 右键托盘图标：打开管理菜单。
- 在窗口中左键点击某条记录：复制该内容。
- 在窗口中右键点击某条记录：显示该记录的删除按钮。
- 搜索框：筛选文字记录。

发布包中需要保留这些文件：

```text
publish/win-x64/ClipboardApp.exe
publish/win-x64/e_sqlite3.dll
publish/win-x64/Assets/App.ico
publish/win-x64/*.dll
```

## 技术栈

- .NET 9
- WPF
- Windows Forms `NotifyIcon`
- SQLite via `Microsoft.Data.Sqlite`
- Windows Registry 当前用户 Run 项用于开机自启
- Win32 `AddClipboardFormatListener` 监听剪贴板变化
- Win32 窗口合成属性实现半透明背景效果

项目入口：

```text
ClipboardApp/App.xaml.cs
ClipboardApp/MainWindow.xaml
ClipboardApp/MainWindow.xaml.cs
```

核心服务：

```text
ClipboardApp/Services/ClipboardWatcher.cs
ClipboardApp/Services/ClipboardStore.cs
ClipboardApp/Services/StartupService.cs
ClipboardApp/Services/WindowBackdrop.cs
```

## 存储方案

历史数据存储在当前用户本地目录：

```text
%LOCALAPPDATA%/ClipboardApp/history.db
%LOCALAPPDATA%/ClipboardApp/images/
```

SQLite 表结构由程序启动时自动创建：

```sql
CREATE TABLE IF NOT EXISTS ClipboardEntries (
    Id TEXT PRIMARY KEY,
    Type INTEGER NOT NULL,
    ContentHash TEXT NOT NULL UNIQUE,
    Text TEXT NULL,
    ImagePath TEXT NULL,
    CreatedAtTicks INTEGER NOT NULL
);
```

字段说明：

- `Id`：记录 ID，也是图片文件名的基础。
- `Type`：记录类型，`0` 为文字，`1` 为图片。
- `ContentHash`：内容 hash，用于去重。
- `Text`：文字内容。
- `ImagePath`：图片文件路径。
- `CreatedAtTicks`：记录时间，用于排序和过期清理。

早期版本使用 JSON：

```text
%LOCALAPPDATA%/ClipboardApp/history.json
```

新版启动时会在 SQLite 为空时尝试导入旧 JSON。导入成功后会把旧文件备份为：

```text
history.migrated.yyyyMMddHHmmss.json
```

如果旧 JSON 损坏，会备份为：

```text
history.corrupt.yyyyMMddHHmmss.json
```

## 去重策略

程序对剪贴板内容生成 SHA-256 hash：

- 文字：`text:` + UTF-8 文本内容的 SHA-256。
- 图片：`image:` + 保存后的 PNG 文件内容 SHA-256。

当外部应用复制了已存在的内容时：

1. 不新增记录。
2. 更新已有记录的 `CreatedAt`。
3. 将该记录移动到列表顶部。

当用户点击历史项复制时：

1. 程序先记录该条内容的 `ContentHash`。
2. 在短时间窗口内监听到相同 hash 的剪贴板事件时，判定为内部复制。
3. 内部复制不会更新数据库，也不会改变历史顺序。

这样可以区分“外部复制”和“从历史列表点击复制”。

## 单实例方案

程序使用命名 `Mutex` 防止多开：

```text
Local\ClipboardApp.SingleInstance
```

后启动的实例如果发现已有实例，会通过命名事件通知已有实例显示窗口，然后自己退出：

```text
Local\ClipboardApp.Activate
```

## 构建

开发构建：

```powershell
dotnet build ClipboardApp\ClipboardApp.csproj
```

发布 win-x64 自包含单文件版本：

```powershell
dotnet publish ClipboardApp\ClipboardApp.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish\win-x64
```

## 注意事项

- 剪贴板历史属于敏感数据，当前版本是本地明文 SQLite 存储。
- 只有能访问当前 Windows 用户目录的人才能读取这些数据。
- 如果后续要进一步提升安全性，可以考虑对文字内容和图片文件使用 Windows DPAPI 加密。
- 当前保留策略是 3 天，启动时自动清理过期记录。

## 当前状态

当前版本已经实现：

- WPF 托盘剪贴板历史窗口
- 文字和图片记录
- SQLite 持久化
- 内容 hash 去重
- 内部复制顺序保护
- 单实例唤醒
- 托盘菜单管理
- win-x64 发布包
