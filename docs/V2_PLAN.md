# StellarisDownloader V2 完整重构与新任务交接计划

## 一、固定决策与仓库边界

- 使用同级目录 `C:\Projects\StellarisDownloaderV2`，不得在 `StellarisDownloaderV1` 内创建子目录，也不得覆盖 V1。
- 新建独立公开仓库 `https://github.com/project10581/StellarisDownloaderV2`，使用全新 Git 历史。
- 当前 V1 仓库保持可运行、可回退，只作为只读功能参考：`https://github.com/project10581/StellarisDownloaderV1`。
- 新 Codex 任务绑定 V2 文件夹；当前任务继续保留审查记录。Codex 本地任务按打开的本地文件夹授予访问权限，不依赖当前任务切换目录。[OpenAI 本地项目说明](https://help.openai.com/en/articles/20001275/)
- 新任务第一步必须把本计划保存为 `docs/V2_PLAN.md`，再建立 `docs/V1_FEATURE_MATRIX.md`；禁止未建立功能矩阵便一次性生成整个项目。
- 公开仓库暂不添加开源许可证；公开可见不等于自动授权他人使用。后续由项目所有者单独决定许可证。
- V2 使用 C#、WPF、.NET 10 LTS、Windows x64；.NET 10 官方支持到 2028 年 11 月。[.NET 支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy)
- 删除 CLI，只维护桌面程序。
- 第一个正式版本必须达到 V1 可见桌面功能完整覆盖。
- 不迁移 V1 的设置和 SQLite 数据库；允许直接选择已有 V1 模组目录并重新扫描，因此不要求重新下载模组。
- 发布形式为自包含便携 ZIP，不提供安装程序作为主要下载方式。

## 二、项目结构与架构约束

只建立两个生产项目和一个测试项目：

```text
StellarisDownloaderV2/
├─ src/
│  ├─ StellarisDownloader.App/
│  │  ├─ Views/
│  │  ├─ ViewModels/
│  │  ├─ Resources/
│  │  └─ Assets/
│  └─ StellarisDownloader.Core/
│     ├─ Models/
│     ├─ Services/
│     ├─ Persistence/
│     └─ Integrations/
├─ tests/
│  └─ StellarisDownloader.Tests/
├─ docs/
│  ├─ V2_PLAN.md
│  └─ V1_FEATURE_MATRIX.md
├─ scripts/
└─ StellarisDownloader.sln
```

架构规则：

- `App` 只负责 WPF 界面、ViewModel、资源和程序启动。
- `Core` 包含设置、SQLite、文件系统、junction、SteamCMD、Workshop API 和模组操作，不引用 WPF。
- 不拆分额外的 Domain/Application/Infrastructure 项目。
- 不引入通用 Host、全局 Service Locator、事件总线或大型依赖注入框架。
- 在 `App.xaml.cs` 手动创建服务并注入 ViewModel，所有依赖位置清楚可查。
- 启用 Nullable、隐式 using、代码分析器；CI 将编译警告视为错误。
- UI 采用 MVVM，使用 `CommunityToolkit.Mvvm`。
- SQLite 使用 `Microsoft.Data.Sqlite`。
- 日志使用 Serilog，按天滚动，最多保留 14 天，单文件上限 10 MB。
- WebView2 使用 `Microsoft.Web.WebView2`。
- 应用更新使用 Velopack，不再维护自制 updater/helper 进程。
- 测试使用 xUnit。
- 不引入 Polly、MediatR、AutoMapper 等当前规模不需要的抽象。

## 三、数据、接口与核心行为

### 3.1 数据事实来源

必须固定以下优先级：

1. 模组库文件夹是模组是否存在的唯一事实来源。
2. `settings.json` 记录用户选择的当前模组库。
3. junction 让 SteamCMD 的 `281990` 目录映射到当前模组库。
4. SQLite 只是可丢弃、可重建的界面索引缓存。
5. Workshop API 数据是远程元数据，不得决定本地文件是否存在。

运行数据统一位于：

```text
%LOCALAPPDATA%\StellarisDownloaderV2\
├─ settings.json
├─ library.db
├─ steamcmd\
├─ cache\
│  └─ previews\
└─ logs\
```

应用安装/解压目录不得保存用户设置和数据库，确保 ZIP 更新不会覆盖用户数据。

### 3.2 设置模型

`AppSettings` 固定包含：

```text
SchemaVersion
LibraryRoot
Language                     // en 或 zh-CN
RefreshLibraryOnStartup      // 默认 false
CheckModUpdatesOnStartup     // 默认 false
CheckAppUpdatesOnStartup     // 默认 false
```

设置行为：

- 首次启动显示初始化向导，选择英文/简体中文和模组库目录。
- 未配置模组库时，下载、更新、删除等写操作不可用，并引导打开设置。
- 每次点击保存只写一次完整设置对象。
- 使用同目录临时文件写入、刷新磁盘后原子替换正式文件。
- 设置损坏时将原文件改名为带时间戳的 `.corrupt` 文件，不静默覆盖，重新进入初始化向导。
- 语言通过 WPF `ResourceDictionary` 实时切换，不要求重启。
- 英文与中文资源键必须通过测试保证完全一致。

### 3.3 SQLite 缓存

数据库初始化只在进程启动时执行一次；每项操作使用短生命周期连接。

`mods` 缓存记录至少包含：

```text
WorkshopId
AppId
Title
Description
PreviewUrl
CreatorId
CreatedAtUtc
ContentPath
FileSize
ImportedOrDownloadedAtUtc
InstalledWorkshopUpdatedAtUtc
LastOperationStatus
LastError
LastScannedAtUtc
```

另设单行 `cache_state`：

```text
SchemaVersion
LibraryRoot
IsStale
LastRebuiltAtUtc
```

规则：

- 数据库中的 `LibraryRoot` 与设置不一致时，禁止显示或操作旧记录。
- 重建使用单一 SQLite 事务替换全部缓存。
- 重建失败保留原事务前数据，但设置 `IsStale=true`；若根目录已变化，旧记录不显示。
- API 元数据请求失败时，同一个 Workshop ID 可以保留旧缓存元数据；新 ID 使用空元数据，不阻止目录扫描。
- 下载成功只进行一次最终数据库写入。
- 初次下载失败且本地没有有效文件夹时，不创建虚假模组记录。
- 更新失败时保留旧的有效模组记录，只更新“最近操作失败”和错误信息。

### 3.4 强类型模型与接口

禁止使用字段可能缺失的字典作为服务返回值。

固定模型：

- `ModRecord`
- `WorkshopMetadata`
- `DownloadRequest`
- `DownloadResult`
- `UpdateCheckResult`
- `LibraryScanResult`
- `LibrarySwitchResult`
- `DeleteResult`
- `OperationProgress`
- `AppUpdateInfo`

固定状态枚举：

- `LocalModState`: `Available`、`Empty`、`Missing`
- `OperationStatus`: `Succeeded`、`Failed`、`Cancelled`
- `UpdateState`: `Unknown`、`UpToDate`、`UpdateAvailable`、`CheckFailed`
- `CacheState`: `Valid`、`Stale`、`RootMismatch`

服务接口：

```text
ISettingsStore
  LoadAsync
  SaveAsync

IModRepository
  InitializeAsync
  ListAsync
  GetAsync
  UpsertFinalResultAsync
  DeleteAsync
  ReplaceSnapshotAsync
  GetCacheStateAsync
  MarkCacheStaleAsync

ILibraryService
  ValidateAsync
  EnsureJunctionAsync
  ScanAsync
  SwitchAsync

ISteamCmdService
  EnsureInstalledAsync
  DownloadAsync

IWorkshopClient
  GetMetadataBatchAsync

IModOperationService
  DownloadBatchAsync
  CheckUpdatesAsync
  UpdateSelectedAsync
  DeleteAsync
  RedownloadAsync

IAppUpdateService
  CheckAsync
  DownloadAsync
  ApplyAndRestartAsync
```

所有耗时方法统一接收：

```text
CancellationToken
IProgress<OperationProgress>
```

`OperationProgress` 包含阶段、已完成数、总数、当前 Workshop ID 和面向用户的消息；不再用“刚排队便接近 100%”的虚假百分比。

### 3.5 并发规则

- 使用一个进程级 `SemaphoreSlim(1,1)` 管理所有会修改文件、数据库或 junction 的操作。
- 下载、更新、删除、重新下载、切换根目录和重建缓存互斥。
- 查询数据库、搜索、排序和加载预览图可以并行。
- Workshop 元数据按批次请求；每批最多 100 个 ID，最多两个批次并行。
- SteamCMD 下载队列顺序执行，避免共享目录和输出互相干扰。
- 用户取消时终止当前 SteamCMD 进程树，未执行的队列项标记为取消。
- 关闭程序时若有写操作，提示用户等待或取消；不得直接留下半完成状态。

## 四、关键工作流

### 4.1 模组库初始化与切换

切换顺序固定为：

1. 规范化并验证新目录。
2. 创建不存在的新目录。
3. 检查 SteamCMD `281990` 路径。
4. 创建或切换 junction，并验证实际目标。
5. 原子保存新的 `LibraryRoot`。
6. 立即使旧缓存失效并清空可操作列表。
7. 扫描新目录并在事务内建立新缓存。
8. 显示增加、移除、无效目录和导入数量。

失败规则：

- junction 创建或验证失败：设置和数据库都不改变。
- junction 成功但设置保存失败：只恢复旧 junction；数据库尚未重建，因此不需要数据库回滚。
- junction 与设置已经提交后，扫描或数据库重建失败：不恢复旧 junction、不恢复旧设置，缓存标记过期并提供“重试扫描”。
- 下次启动发现 junction 与设置不一致时，以设置中的目录为目标尝试修复；修复失败时禁用写操作并显示明确错误。
- junction 路径若是非空普通目录或文件，禁止自动删除，提示用户手动处理。
- 删除 junction 时只能删除 reparse point 本身，绝不能递归删除目标目录。

扫描规则：

- 只把模组库根目录下一层的纯数字文件夹视为 Workshop 模组。
- 非数字文件夹忽略并计入扫描摘要。
- 空数字目录标记为无效，不作为成功安装记录。
- 已有 V1 模组导入时，以目录最后修改时间作为初始安装时间近似值。
- V2 自己成功下载后，保存当时 Workshop 的远程更新时间，后续优先使用该快照判断更新。

### 4.2 SteamCMD

- 不把 SteamCMD 二进制提交到 Git 仓库。
- 首次使用时下载官方 Windows SteamCMD ZIP，解压到 `%LOCALAPPDATA%\StellarisDownloaderV2\steamcmd`。
- 下载完成后必须验证 `steamcmd.exe` 存在并可启动；失败时保留日志并允许重试。
- 固定 Stellaris App ID 为 `281990`。
- 命令使用 anonymous login 和 `workshop_download_item 281990 <id>`。
- stdout/stderr 异步读取，防止管道堵塞。
- 单个模组默认超时 60 分钟，支持用户取消。
- 进程退出码仅作为诊断信号，不能单独代表下载成功。
- 成功必须同时满足：
  - 输出中出现与当前 ID 对应的下载成功终态；
  - 没有晚于该成功终态的同 ID 明确失败终态；
  - 目标数字目录存在；
  - 目标目录非空。
- 无法明确分类的输出按失败处理，但不得删除已经存在的旧模组。
- 输出解析使用脱敏后的真实日志样本建立测试夹具，避免凭空假设 SteamCMD 文案。
- 元数据请求失败不推翻已经验证的文件下载成功；记录为空并安排后续刷新。
- 最终结果一次性写入数据库，取消 V1 的“先写成功、获取元数据后再写一次”流程。

### 4.3 更新检查

- 批量获取当前列表的 Workshop 元数据。
- V2 下载的模组比较 `LatestRemoteUpdatedAtUtc` 与 `InstalledWorkshopUpdatedAtUtc`。
- 从 V1 目录导入且没有安装快照的模组，比较远程更新时间与目录最后修改时间，并在界面标识为近似判断。
- 元数据缺失或网络失败返回 `CheckFailed`，不能伪装为“已是最新”。
- 更新窗口显示：标题、ID、当前状态、远程更新时间、失败原因。
- 默认勾选 `UpdateAvailable` 项，允许用户取消单项后更新所选。
- 不保留另一个重复的“全部更新”后端；“全选”只是同一选择更新流程的界面动作。
- 更新失败保留旧文件和旧安装快照，用户可以重试。
- 启动检查顺序为：必要的缓存重建 → 模组更新检查 → 应用更新检查。

### 4.4 删除与重新下载

- 删除目标必须解析为 `LibraryRoot\<WorkshopId>` 的直接子目录。
- Workshop ID 必须为纯数字。
- 解析后的父目录必须等于当前模组库根目录。
- 禁止删除根目录、SteamCMD 目录、junction 本身或任意外部路径。
- 默认发送到 Windows 回收站。
- 回收站删除失败时停止操作；只有用户再次明确确认才允许永久删除。
- 文件删除成功后才删除数据库缓存记录。
- 数据库删除失败时提示重新扫描，不能声称整个操作成功。
- “删除并重新下载”复用同一个安全删除流程，成功后将原 ID 放入公共下载队列。

### 4.5 WebView2 创意工坊浏览器

- 使用系统共享的 Evergreen WebView2 Runtime；启动时检测缺失并提供微软安装入口，不把固定 Runtime 打进 ZIP。[WebView2 分发说明](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- 注入脚本存放为独立 `.js` 资源，不嵌入超长 C# 字符串。
- 本地桥接只接受结构固定的消息：

```json
{
  "type": "enqueueWorkshopIds",
  "ids": ["123456789"]
}
```

- 只接受纯数字、去重后的 ID；单条消息最多 100 个 ID。
- 只有可信 Steam Community 页面可以调用桥接。
- 非 Steam 链接使用系统浏览器打开。
- WebView2 不得传入本地路径、执行命令或访问任意服务方法。
- 浏览器和“URL/ID 下载”窗口共用同一个 `DownloadQueueViewModel`，彻底删除两套重复队列逻辑。
- 支持裸 ID、标准 Workshop URL 及带 `id` 查询参数的链接。

## 五、WPF 功能矩阵与界面行为

### 5.1 主窗口

保留 V1 的双栏结构：

- 左侧：预览图、标题、作者、大小、远程更新时间、最后下载时间、Workshop 链接、本地路径和描述。
- 右侧：搜索框、排序方式、虚拟化模组列表；列表项只显示标题，不显示 Workshop ID。
- 使用 `ICollectionView` 完成过滤和排序，不清空并重建整个控件。
- 搜索同时匹配标题和 Workshop ID。
- 排序包含标题、远程更新时间、最后下载时间和文件大小。
- 选择变化只更新详情，不重新查询整个数据库。
- 预览图异步加载，使用内存缓存；加载失败显示占位图。

模组操作：

- 刷新列表。
- 从磁盘重新扫描模组库。
- 打开模组文件夹。
- 打开 Workshop 页面。
- 删除模组。
- 删除并重新下载。
- 检查更新。
- 下载所选更新。

### 5.2 下载窗口

- 接受一行或多行 ID/URL。
- 解析后显示去重队列和标题查询状态。
- 无效输入逐项提示，不阻止其他有效 ID。
- 开始下载后显示总进度、当前模组阶段、可滚动日志和取消按钮。
- 队列完成后显示成功、失败和取消数量。
- 失败项保留在结果列表，允许一键重新加入队列。

### 5.3 设置窗口

完整保留 V1 设置项：

- 模组库目录。
- English / 简体中文。
- 启动时重新扫描模组库。
- 启动时检查模组更新。
- 启动时检查应用更新。

行为改进：

- 一次保存整个设置对象。
- 修改普通设置不触发目录切换。
- 目录变化才执行 junction 和重建流程。
- 保存前只进行一次确认。
- 语言即时生效。
- 模组库切换完成后显示新增和移除的模组摘要。

### 5.4 应用更新

- 程序入口最先执行 Velopack 生命周期初始化。
- `AppUpdateService` 使用公开 GitHub 仓库作为更新源。
- 手动检查显示当前版本、最新版本和 Release Notes。
- 自动检查无更新时保持静默；失败只写日志，不在每次启动弹错误窗。
- 用户确认后下载更新，提供进度和取消。
- 下载完成后允许“立即重启更新”或“稍后”。
- 不再创建自定义 updater Python/EXE/helper。

## 六、实施批次与提交纪律

### 批次 0：V2 仓库建立

- 创建 V2 文件夹、solution、三个项目、`.gitignore`、README 和 CI。
- 保存本计划和 V1 功能矩阵。
- 配置 Nullable、分析器和统一格式。
- 第一个提交只包含骨架，不包含业务代码。

### 批次 1：设置与缓存

- 实现运行目录、设置原子读写、损坏恢复。
- 实现 SQLite schema、cache state 和 repository。
- 完成设置与数据库测试后提交。

### 批次 2：模组库与 junction

- 实现路径验证、junction 创建/验证、扫描和切换。
- 实现根目录变更后的缓存失效语义。
- 完成临时目录集成测试后提交。

### 批次 3：Workshop 与 SteamCMD

- 实现批量元数据客户端。
- 实现 SteamCMD 安装、进程、取消、超时和结果分类。
- 实现下载、更新、删除和重新下载服务。
- 每个业务操作完成后分别提交，禁止合成一个巨型提交。

### 批次 4：WPF 基础界面

- 实现主窗口、列表、详情、搜索、排序和设置。
- 实现实时中英文资源切换。
- ViewModel 测试通过后提交。

### 批次 5：队列与浏览器

- 实现共享下载队列和进度窗口。
- 实现 URL/ID 输入。
- 接入 WebView2 浏览器和受限消息桥。
- 验证两种入口使用同一队列服务后提交。

### 批次 6：更新与发布

- 接入 Velopack。
- `dotnet publish` 使用 `win-x64`、self-contained。
- 保持 `PublishSingleFile=false`、`PublishTrimmed=false`，避免 WPF、WebView2 和原生依赖被错误裁剪。
- Velopack 生成的 `Portable.zip` 作为用户下载包；官方说明该包可以免安装运行和更新。[Velopack 打包输出](https://docs.velopack.io/packaging/overview)
- GitHub Release 上传 Portable ZIP、完整/增量更新包和 `releases.win.json`，不上传 Setup 作为主要发行物。
- 更新 feed 与 nupkg 必须一同发布，否则客户端无法发现更新。[Velopack 分发说明](https://docs.velopack.io/distributing/overview)
- 开发阶段只发布 Actions artifact；通过功能矩阵后才创建正式 GitHub Release。
- CI 预留代码签名步骤；没有证书时保持关闭并在 README 标明未签名，不阻塞开发。

### 提交纪律

- 每个提交只完成一个可以说明、测试和回退的行为。
- 提交前必须运行对应测试。
- 禁止使用“完整重构”“清理全部代码”作为一个提交。
- 禁止为了架构整齐提前创建没有调用者的接口、工厂和管理器。
- 每完成一个批次，更新功能矩阵和已知限制。
- V1 仓库不得产生任何修改。

## 七、测试与验收标准

### 7.1 自动测试

设置：

- 首次启动默认值。
- 一次保存所有字段。
- 临时文件写入失败不破坏旧设置。
- 损坏 JSON 被备份并进入初始化流程。
- 中英文资源键完全一致。

数据库：

- schema 只初始化一次。
- CRUD 和事务替换。
- 下载成功只写一次最终记录。
- 重建失败不产生半张新表。
- 根目录不匹配时旧缓存不可操作。
- API 失败时保留同 ID 的旧元数据。

模组库：

- 只导入直接数字子目录。
- 正确统计新增、移除、忽略和空目录。
- junction 创建、目标验证和删除不影响目标内容。
- junction 失败时设置不变。
- 设置保存失败时恢复旧 junction。
- 重建失败时保留新设置和新 junction，并标记缓存过期。
- 启动时能够检测和修复 junction/设置不一致。

SteamCMD：

- 无效 ID。
- `steamcmd.exe` 缺失。
- 安装 ZIP 下载或解压失败。
- 正常成功输出和有效目录。
- 成功输出但目录缺失或为空。
- 明确失败输出。
- 同时包含历史错误和最终成功的输出。
- 同时包含成功但随后出现同 ID 最终失败的输出。
- 无法分类的输出。
- 超时和用户取消。
- 初次失败不创建记录。
- 更新失败保留旧模组和旧安装时间。
- 元数据失败不推翻文件下载成功。

删除：

- 正常发送回收站。
- 越界路径、根目录、非数字 ID 和 junction 目标防护。
- 文件删除失败时数据库不变。
- 数据库删除失败时返回部分失败并建议扫描。
- 删除并重新下载复用相同路径验证。

UI/ViewModel：

- 搜索、四种排序和选择详情。
- 下载队列去重、无效项和取消。
- 更新选择流程。
- 设置只保存一次。
- 写操作进行时冲突命令被禁用。
- 缓存过期时列表不可执行危险操作。
- WebView2 消息来源、类型、数量和数字 ID 验证。
- 应用更新无更新、有更新、下载失败和延迟重启。

### 7.2 CI

每次提交运行：

```text
dotnet restore
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release
```

CI 使用 Windows runner。普通 CI 不访问真实 SteamCMD 或 Workshop 网络，使用 HTTP、进程和文件系统替身，避免随机失败。

### 7.3 人工验收

正式发布前必须逐项通过：

1. 首次启动可选择中文/英文和新建或已有模组目录。
2. 选择 V1 原模组目录后能重新扫描，无需重新下载。
3. 列表、搜索、排序、详情和预览正常。
4. 单个及批量 ID/URL 下载正常。
5. 浏览器页面可以加入相同下载队列。
6. 检查更新、选择更新和失败重试正常。
7. 删除、回收站恢复和删除后重新下载正常。
8. 切换模组库后 junction 指向新目录。
9. 切换后的扫描失败不会切回旧目录，重试扫描可以恢复。
10. 重启后设置、语言、缓存和启动检查符合配置。
11. 从上一正式 ZIP 版本可以通过 Velopack 更新到下一版本。
12. V1 在整个开发和验收过程中保持原样且仍可启动。

只有以上功能矩阵全部通过，V2 才能替代 V1；此前 V1 不归档、不删除。
