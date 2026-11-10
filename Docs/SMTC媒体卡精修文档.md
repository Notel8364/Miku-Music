# SMTC 媒体卡精修文档

> 适用版本：v2.1.4 · Miku Music（MikuMusic.exe）
> 涉及代码：`Services/AppIdentity.cs`、`Services/SmtcService.cs`、`App.xaml.cs`
> 最终效果：Win11 音量浮层媒体卡正确显示应用名「Miku Music」+ 程序图标，封面/曲名/歌手正常，上一首/下一首/暂停按键全部可用。

---

## 1. 这块是什么

SMTC（System Media Transport Controls）是 Windows 的系统媒体控制通道：音量飞轮滑出的媒体浮层、耳机按键、键盘媒体键、蓝牙控制，都从这条通道读「现在在放什么」并回传按键。Music_Box 的出声引擎是 NAudio，不经过系统音频会话的媒体协议，所以需要一条桥接链把状态报上去、把按键接回来。

架构：**静音影子播放器**。程序内建一个 WinRT `MediaPlayer`，`IsMuted = true`，跟着 NAudio 同步播放/暂停/对时。它不出声，只负责：

- 把曲目信息、时间轴、播放状态上报给系统（媒体卡显示的内容）；
- 接收系统按键，转发回 NAudio（`PlayRequested` / `PauseRequested` / `NextRequested` / `PrevRequested` / `SeekRequested` 事件）。

好处：进度条由影子播放器自己推进，不用手动补时间轴，天然不会越走越偏（偏差超 1.5 秒才纠一次，避免抖动）。

## 2. 应用名与图标：三层解析链（本次精修核心）

媒体卡左上角显示「未知应用」的根因不是单一 bug，而是免安装（非 MSIX）Win32 程序的身份要被系统认出来，**三层缺一不可**。三层由 `AppIdentity.Apply()` 在最早期一次性登记：

### 2.1 第一层：进程显式 AUMID（身份的根）

非打包 exe 默认没有 AppUserModelID，系统拿到的是「exe 路径形态」的隐式 AUMID，谁都反查不出名字和图标。

```csharp
SetCurrentProcessExplicitAppUserModelID("MikuMusic");
```

**时序是命门**：SMTC 媒体会话建立时拍下的是*当时的*进程 AUMID。WPF 的 `App.OnStartup` 前面还排着一串 InitializeComponent / 静态初始化链，任何一处先建了窗口或托盘都可能抢跑。因此由 `AppIdentityBootstrap` 的 **`[ModuleInitializer]` 在 Main 之前执行**（比 `App.OnStartup` 还早），OnStartup 里的调用保留作幂等兜底。

- AUMID 用短名 `MikuMusic`（旧值 `MikuMusic.Desktop.NAudioPlayer` 是 NAudio 默认命名习惯，当应用名显示极难看）。
- 显示名统一 `Miku Music`。

### 2.2 第二层：注册表通知身份（Toast 链）

```
HKCU\Software\Classes\AppUserModelId\MikuMusic
    DisplayName = "Miku Music"
    IconUri     = <当前 exe 完整路径>
```

这是**通知（Toast/托盘气泡）**显示名与图标的标准登记处。先查再写，值一致就不动注册表，避免每次启动白写。

**图标的坑**：`IconUri` 必须指向 **apphost exe**（`Environment.ProcessPath`），不能指向托管 DLL。`<ApplicationIcon>` 编译时内嵌在 exe 的 Win32 资源里，`Assembly.Location` 给出的 DLL 不带图标资源，系统从 DLL 抽图标会失败——旧版这里登记的就是 DLL，浮层图标因此挂过空。

### 2.3 第三层：开始菜单快捷方式（媒体卡真正走的链）

**媒体卡不认第二层的注册表键**。它按「开始菜单里带 `System.AppUserModel.ID` 属性、且与进程 AUMID 匹配的 .lnk 快捷方式」解析应用名和图标（Electron 应用同款问题，见 Obsidian 论坛 #11966）。所以 `EnsureStartMenuShortcut` 会：

1. 在 `%APPDATA%\Microsoft\Windows\Start Menu\Programs` 写一个 `Miku Music.lk`（路径/图标/描述），目标指向当前 exe；
2. 用 `SHGetPropertyStoreFromParsingName`（GPS_READWRITE）按文件路径打开该 .lnk 的属性库，写入 `PKEY_AppUserModel_ID`（FmtId `9F4C2855-9F98-11D0-A54F-0014A34736F9`，Pid 5）。

实现细节上的两个教训：

- **不能对 ShellLink COM 对象直接 QI `IPropertyStore`**——实测 `QueryInterface` 返回 `E_NOINTERFACE`，旧版因此 InvalidCastException，基础 .lnk 从未落盘，这正是媒体卡一直「未知应用」的根因之一。必须走属性系统 API（资源管理器写 .lnk 属性的同一条路）。
- **幂等条件只看「文件在且目标指向当前 exe」**，不把 AUMID 属性齐备算进重建条件。否则本机属性写不进去时，每次启动都会重写快捷方式、反复刷开始菜单索引。exe 换了发布目录才重建，保证 .lnk 永远指向当前 exe（原地替换发布文件后快捷方式依旧可用）。

### 2.4 本机实况与降级

排查中实锤：这台机器的 **Windows 属性系统整体损坏**——`SHGetPropertyStoreFromParsingName` 对所有文件类型（连 notepad.exe）一律返回 `E_NOINTERFACE`，正常系统上 .exe 必可读。因此：

- 在这台机器上，.lnk 的 AUMID 属性写不进去（代码里此路径降级为 Debug 级日志，避免每次启动刷 Error）；
- 属性缺失时媒体卡应用名**退回第二层注册表链**解析——本次实测该链路生效，「Miku Music」+ 图标正确显示；
- 在属性系统健康的机器上，快捷方式链完整生效，显示效果相同。

## 3. 按键修复：连环双坑

现象：媒体浮层显示了，但折叠态的上一首/下一首点了没反应。

1. **坑一：`MediaPlaybackCommandManager` 默认接管。** 影子播放器的命令管理器会把系统按键拦截去操作自己——播放/暂停它管得了（看起来「有反应」），上一首/下一首它找不到曲队列，什么都不做**也不把按键转发给 `ButtonPressed`**。手动接管 SMTC 时官方要求 `CommandManager.IsEnabled = false`，`ButtonPressed` 才会收齐全部按键。
2. **坑二：关掉 CommandManager 连带关闭了整个 SMTC 集成链。** `MediaPlayer` 自带的 SMTC Enable/Update 逻辑被停掉后，必须显式补一句：

```csharp
_player.CommandManager.IsEnabled = false;   // 坑一：停掉接管
_smtc = _player.SystemMediaTransportControls;
_smtc.IsEnabled = true;                     // 坑二：显式拉起，漏了整链静默失效
```

漏掉坑二的表现极具迷惑性：所有按钮开关、`DisplayUpdater.Update()`、`PlaybackStatus` 上报都被系统**静默忽略**（不抛异常、日志无错）——正是「程序在播放，音量浮层里却没有媒体卡片」的根因。

另外不用 `ApiInformation.IsTypePresent` 探测 SMTC 可用性——它对 `Windows.Media.SystemMediaTransportControls` 这个运行时类名恒返回 false，会让 SMTC 静默地整个不启用。Tfm 已要求 10.0.19041，直接建、失败当不支持即可。

## 4. 曲目信息与封面的精修

### 4.1 标题与副标题

- **上行大字**：优先曲名 → 退文件名（无扩展名）→ 退程序名，绝不显示路径（`ResolveTitle`）。
- **下行小字**：固定为专辑名，专辑为空时写程序名 `Miku Music`（`ResolveSubtitle`）。不能留空——`MediaPlaybackItem` 会把文件全路径顶上来。

### 4.2 显示信息双通道

只填 `_smtc.DisplayUpdater` 时系统浮层经常还是空的，所以换曲时同时走：

1. `DisplayUpdater.MusicProperties.*` + `Update()`；
2. 影子播放器换 `MediaPlaybackItem` 源并 `ApplyDisplayProperties`（系统读的媒体信息主要来自播放项显示属性）。

`MediaItemDisplayProperties` 无无参构造，从播放项 `GetDisplayProperties()` 取一份现成的再改。

### 4.3 封面：异步 + 代次票

`SetTrack` 在 UI 线程被调，取缩略图是 WinRT 异步（`StorageFile.GetFileFromPathAsync`），**绝不能同步 `GetResult()`**——否则切歌整窗假死。现流程：

- 后台 `Task.Run` 取图 → 回 UI 线程 `display.Update()`；
- **代次票 `_thumbTicket`**：快速连切时只有最后一次取回的封面允许写入，防止慢的那次把新曲封面盖成旧的。曲目封面和程序图标兜底共用同一张票（先要封面、封面没落地又要图标时，谁后到谁生效）；
- **无封面兜底**：曲目封面匹配不到/网络图未落地时，用 `Assets\app_icon.png`（退 `app.ico`）顶位，浮层不留空图。

### 4.4 资源释放

`MediaSource` 实现 `IDisposable`，每次换曲 new 不释放会让 Media Foundation 源读取器一个个堆着不放——连播久了内存涨、CPU 空转。换源前显式 `ReleaseShadowSource()` 拆掉旧源。`_mirroredPath` 比对保证只有真换曲才换源，避免每 tick 重建解码器。

## 5. 排查速查表

| 现象 | 先查什么 |
| --- | --- |
| 媒体卡显示「未知应用」 | 日志搜 `[AUMID]`：`applied` 是否 true；注册表 `HKCU\...\AppUserModelId\MikuMusic` 是否有 DisplayName/IconUri；`Programs\Miku Music.lnk` 是否存在且目标正确 |
| 浮层图标空 | IconUri 是否指向 exe（而非 DLL） |
| 浮层完全不出现 | 日志搜 `[SMTC]`：会话是否建立；`CommandManager.IsEnabled=false` 之后是否补了 `_smtc.IsEnabled=true` |
| 上一首/下一首无反应 | `CommandManager.IsEnabled` 是否为 false（默认 true 会吞按键） |
| 切歌窗口假死 | 封面取图是否被同步等待（GetResult/GetAwaiter().GetResult()） |
| 连播内存涨/CPU 空转 | 换源前是否 ReleaseShadowSource |

## 6. 已知边界

- **本机属性系统损坏**（E_NOINTERFACE 波及全部文件类型）：.lnk 的 AUMID 属性在此机器写不进去，属环境问题非代码问题；媒体卡应用名走注册表链已正确显示，健康机器上走完整快捷方式链。重装/修复系统后无需改代码自动完整生效。
- SMTC 不支持时（创建失败）只降级为无系统媒体卡，播放功能不受影响。
