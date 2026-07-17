# StellarisDownloader V1 功能矩阵

## 1. 审计基线

- V1 路径：`C:\Projects\StellarisDownloaderV1`
- 审计提交：`01161b78895579f7ff1475a27b76cd9895a32d8d`
- 审计分支：`main`，与 `origin/main` 对齐，审计开始时工作树干净。
- 审计方式：只读检查源码、文档、GitHub Actions 与测试源码；为避免在 V1 生成 `.pytest_cache`、`__pycache__` 或其他运行时文件，本批次未在 V1 目录执行测试。
- 范围：V1 1.6.2 的可见桌面功能、支撑行为和现有自动测试；CLI 仅用于确认明确排除项。
- V2 权威目标：以 [`V2_PLAN.md`](V2_PLAN.md) 为准。本矩阵记录 V1 现状、V2 处置和后续验收，不把 V1 缺陷当作兼容要求。

状态说明：

- `保留`：V2 正式版必须覆盖用户可见能力。
- `保留并改进`：保留能力，但按 V2 计划替换不安全、重复或不可靠的实现。
- `替换`：用户目标不变，底层机制改用 V2 计划指定方案。
- `新增`：V1 不具备，但 V2 计划明确要求。
- `排除`：V2 明确不再提供。

## 2. 桌面主窗口与模组列表

| ID | 能力 | V1 实际行为 | 源码/测试证据 | V2 处置 | 批次 | V2 验收要点 |
|---|---|---|---|---|---|---|
| UI-01 | 双栏主窗口 | PySide6 `QSplitter`；实际为左侧详情、右侧搜索/排序/列表，初始宽度 500/900。 | `gui.py:MainWindow.init_ui`、`GUI_README.md` | 保留 | 4 | 保留左侧详情、右侧搜索/排序/列表；窗口可调整且信息不丢失。 |
| UI-02 | 模组列表 | 从 SQLite 读取全部记录并显示“标题 + Workshop ID”；使用 `QListWidget`。 | `gui.py:refresh_mod_list`、`ui/mod_list.py` | 保留并改进 | 4 | WPF 列表启用 UI 虚拟化；列表项只显示标题，不显示 Workshop ID；缓存有效时显示全部记录。 |
| UI-03 | 搜索 | 标题和 Workshop ID 不区分大小写包含匹配。 | `ui/mod_list.py:mod_matches_search`、`tests/test_mod_list.py` | 保留 | 4 | `ICollectionView` 过滤；标题与 ID 均可命中。 |
| UI-04 | 四种排序 | 标题升序；远程更新时间、最后下载时间、文件大小降序。 | `ui/mod_list.py:sort_mod_records`、`tests/test_mod_list.py` | 保留 | 4 | 四种排序正确且不重建整个控件。 |
| UI-05 | 列表刷新 | 重新查询数据库、清空并重建整个 `QListWidget`。 | `gui.py:refresh_mod_list` | 保留并改进 | 4 | 刷新数据源并保留合理的筛选/排序/选择状态。 |
| UI-06 | 选择详情 | 点击列表项时把该项已附带的记录传给详情面板。 | `gui.py:on_mod_selected` | 保留 | 4 | 选择变化只更新详情，不重新查询整个数据库。 |
| UI-07 | 右键操作 | 刷新列表、从磁盘重载、打开文件夹、打开 Workshop、删除、删除并重下。 | `gui.py:show_mod_list_context_menu` | 保留 | 4/5 | 所有入口可用；危险操作受缓存状态和写锁保护。 |
| UI-08 | 模组详情 | 标题、作者、文件大小、远程更新时间、最后下载时间、Workshop 链接、本地路径、描述。 | `ui/mod_detail_panel.py` | 保留 | 4 | 字段完整；无值时有明确占位。 |
| UI-09 | 描述链接安全 | 描述中的 HTTP/HTTPS 链接用系统浏览器打开；拒绝本地、自定义协议和相对路径。 | `ui/mod_detail_panel.py:is_safe_external_link`、`tests/test_mod_detail_links.py` | 保留 | 4 | 仅安全 Web 链接可打开。 |
| UI-10 | 预览图 | 后台线程下载，10 秒超时、10 MB 上限；失败显示文字占位。每次选择会新建线程，无内存缓存。 | `ui/workers.py:PreviewImageThread`、`ui/mod_detail_panel.py` | 保留并改进 | 4 | 异步加载、内存缓存、失败占位；过期请求不得覆盖当前选择。 |
| UI-11 | 文件大小回退 | 元数据无大小时，在 UI 线程递归遍历模组目录计算。 | `ui/mod_detail_panel.py:update_mod_details` | 保留并改进 | 4 | 不阻塞 UI；优先缓存结果。 |
| UI-12 | 运行中关闭保护 | 存在任意后台 `QThread` 时拒绝关闭，只提示等待。 | `gui.py:closeEvent` | 保留并改进 | 4/5 | 写操作进行时提示等待或取消；取消走受控流程。 |

## 3. 设置、语言与运行目录

| ID | 能力 | V1 实际行为 | 源码/测试证据 | V2 处置 | 批次 | V2 验收要点 |
|---|---|---|---|---|---|---|
| SET-01 | 设置项 | 库目录、`en`/`zh`、启动重扫、启动检查模组更新、启动检查应用更新。默认均为 false，语言默认英文。 | `settings_dialog.py`、`core/settings.py` | 保留 | 1/4 | 使用计划规定的 `AppSettings` 字段和 `zh-CN`。 |
| SET-02 | 首次设置 | V1 没有独立初始化向导；写操作遇到无效库目录时提示打开设置。 | `gui.py:require_valid_library_root`、`GUI_README.md` | 新增 | 1/4 | 首次启动选择语言和库目录；未配置时禁用危险写操作。 |
| SET-03 | 设置保存 | 每个变更字段通过独立 setter 分别读取并重写 JSON；可能一次点击多次写文件。 | `settings_dialog.py:apply_non_root_settings_changes`、`core/settings.py` | 替换 | 1 | 一次保存完整对象，只执行一次原子写入。 |
| SET-04 | 原子性与损坏恢复 | 普通文本覆盖写；解析失败只记录日志并返回空字典，下一次写入可静默覆盖。 | `core/settings.py` | 新增 | 1 | 同目录临时文件、flush 后原子替换；损坏文件改名 `.corrupt`。 |
| SET-05 | 语言切换 | 翻译字典含英文/中文；语言值缓存，设置后提示重启，当前窗口不会即时刷新。 | `core/i18n.py`、`settings_dialog.py:on_language_changed` | 保留并改进 | 4 | WPF `ResourceDictionary` 实时切换，无需重启；资源键集合相同。 |
| SET-06 | 修改确认 | 编辑库路径时可能先确认一次，保存任意设置时再确认一次。 | `settings_dialog.py:confirm_root_change_intent`、`save_settings` | 保留并改进 | 4 | 保存前最多一次确认。 |
| SET-07 | 用户数据目录 | `%LOCALAPPDATA%\StellarisModManager\` 下含 `data/app.db`、`data/settings.json`、日志、SteamCMD 与更新暂存。 | `core/runtime_paths.py`、`PACKAGING.md` | 替换 | 1 | 使用 `%LOCALAPPDATA%\StellarisDownloaderV2\` 的计划结构。 |
| SET-08 | 日志 | Python rotating file；1 MB、3 个备份，同时写控制台。 | `core/runtime_paths.py:configure_logging` | 替换 | 1/6 | Serilog 按天滚动、14 天、单文件 10 MB。 |

## 4. 模组库、junction 与 SQLite

| ID | 能力 | V1 实际行为 | 源码/测试证据 | V2 处置 | 批次 | V2 验收要点 |
|---|---|---|---|---|---|---|
| LIB-01 | 库目录验证 | 验证目录存在且为目录；SteamCMD 目标若为非空普通目录或文件则报错。已有 junction 的实际目标不在此函数中与设置比对。 | `core/library_root.py:validate_library_root` | 保留并改进 | 2 | 规范化路径；junction 必须与设置目标一致，否则修复或禁用写入。 |
| LIB-02 | junction 创建/替换 | 使用 `mklink /J`；可替换错指 junction；仅允许删除 reparse point；可删除空普通占位目录。 | `core/library_root.py`、`tests/test_library_root.py` | 保留并改进 | 2 | 创建、验证、回滚严格按计划顺序；绝不递归删除 junction 目标。 |
| LIB-03 | 库扫描范围 | 只扫描根目录下一层的纯数字目录；忽略其他项。V1 不统计非数字目录，也不排除空数字目录。 | `core/library_root.py:build_import_records` | 保留并改进 | 2 | 统计新增、移除、忽略与空目录；空数字目录不算有效安装。 |
| LIB-04 | V1 目录导入 | 数字目录可直接导入；以目录 mtime 作为 `last_downloaded_at` 近似值，并批量补元数据。 | `core/library_root.py:build_import_records` | 保留并改进 | 2 | 允许选择现有 V1 模组目录；明确标记安装时间和更新判断为近似。 |
| LIB-05 | 扫描时元数据失败 | 同 ID 的旧元数据会合并回新记录；新 ID 可保留空元数据。 | `core/library_root.py:merge_cached_metadata`、`tests/test_library_root.py` | 保留 | 2/3 | API 失败不得阻止文件系统扫描。 |
| LIB-06 | 全量重建 | `DELETE` + `executemany INSERT` 在一个 SQLite 上下文事务中执行；失败返回 false。 | `core/database.py:replace_all_mods` | 保留并改进 | 1/2 | 单事务替换；失败保留旧快照并标记 stale。 |
| LIB-07 | 库切换事务语义 | V1 顺序为 junction → 扫描/重建数据库 → 保存设置；任意异常尝试恢复旧 junction，但数据库可能已被替换。 | `core/library_root.py:switch_library_root` | 替换 | 2 | 严格执行计划中的提交顺序和分阶段失败语义。 |
| LIB-08 | 启动重扫 | 启用后启动时清空列表、后台扫描并重建；完成后再继续启动检查。 | `gui.py:refresh_mod_db_on_startup_if_enabled` | 保留并改进 | 2/4 | 必要缓存重建完成后再检查模组与应用更新。 |
| DB-01 | 数据库模型 | 单表 `mods`，字典行；无 `cache_state`、无 schema version、无库根绑定。 | `core/database.py` | 替换 | 1 | 强类型 `ModRecord` 与单行 `cache_state`；RootMismatch 时不可显示/操作旧记录。 |
| DB-02 | 初始化 | 每次构造 `ModDatabase` 都运行 schema 检查/迁移。 | `core/database.py:__init__` | 替换 | 1 | 进程启动只初始化一次，操作使用短连接。 |
| DB-03 | 下载写入 | 成功时先写无元数据记录，API 成功后再写一次。 | `core/steamcmd.py:download_mod` | 替换 | 3 | 每个成功下载只进行一次最终写入。 |

## 5. 下载、SteamCMD、更新与删除

| ID | 能力 | V1 实际行为 | 源码/测试证据 | V2 处置 | 批次 | V2 验收要点 |
|---|---|---|---|---|---|---|
| DL-01 | ID/URL 输入 | 单行输入；支持裸数字、`id` 查询参数、末尾数字路径和任意文本中的 6–20 位数字回退。 | `download_dialog.py`、`core/workshop_ids.py`、`tests/test_workshop_ids.py` | 保留并改进 | 5 | 支持单行/多行；逐项报告无效输入；只接受计划规定格式。 |
| DL-02 | 下载队列 | 独立对话框维护有序去重列表，异步查询标题，可删除选中或清空。 | `download_dialog.py` | 保留并改进 | 5 | 共享 `DownloadQueueViewModel`；保留失败项并可一键重试。 |
| DL-03 | 队列执行 | 按顺序一次启动一个 `DownloadModThread`；显示总数、当前项和日志。 | `ui/download_flow.py` | 保留并改进 | 3/5 | SteamCMD 严格串行；进度使用阶段/计数，不使用虚假百分比。 |
| DL-04 | 取消 | V1 进度框只有完成后才启用关闭按钮；无下载取消和队列取消。 | `ui/progress.py`、`core/steamcmd.py` | 新增 | 3/5 | 用户取消终止当前进程树，未执行项标记 Cancelled。 |
| STM-01 | SteamCMD 获取 | V1 仓库跟踪并打包 `steamcmd.exe`，冻结运行时复制到用户目录。 | `StellarisModManager.spec`、`core/runtime_paths.py`、`docs/STEAMCMD.md` | 替换 | 3 | V2 仓库不含二进制；首次使用下载官方 ZIP 并验证可启动。 |
| STM-02 | 命令与超时 | anonymous login、App ID 281990、单项 60 分钟超时；`subprocess.run` 同步收集输出。 | `core/steamcmd.py:download_mod` | 保留并改进 | 3 | 异步读取 stdout/stderr，支持取消并终止进程树。 |
| STM-03 | 成功判定 | 输出中先查任一成功短语，成功优先于所有失败短语；另要求目标目录存在且非空；退出码仅返回不参与判定。 | `core/steamcmd.py:classify_steamcmd_output`、`tests/test_steamcmd.py` | 保留并改进 | 3 | 使用按 ID、按终态顺序的真实日志解析；晚于成功的明确失败必须失败。 |
| STM-04 | 初次失败 | 本地无既有数据库记录时不创建失败记录。 | `core/steamcmd.py:_record_failed_attempt`、`tests/test_steamcmd.py` | 保留 | 3 | 无有效本地目录时不创建虚假记录。 |
| STM-05 | 更新失败 | 保留既有记录与上次成功时间，状态置 failed、写错误；同一失败可能由两层逻辑重复 upsert。 | `core/steamcmd.py`、`core/updater.py:update_mod`、`tests/test_steamcmd.py` | 保留并改进 | 3 | 保留旧文件/快照，只做一次明确的最终状态写入。 |
| UPD-01 | 批量检查 | Steam API 每批最多 100，当前实现串行请求批次。 | `core/workshop_api.py:fetch_mod_metadata_batch` | 保留并改进 | 3 | 每批最多 100，最多两个批次并行。 |
| UPD-02 | 更新时间判断 | 通常比较远程更新时间与 `last_downloaded_at`；metadata 失败为 `failed_check`；本地 failed 强制可重试。 | `core/updater.py`、`tests/test_updater.py` | 保留并改进 | 3 | V2 下载使用安装时远程快照；导入项使用目录 mtime 近似并标识。 |
| UPD-03 | 更新选择 UI | 仅把 `update_available` 传入选择窗口；有“更新所选”和“全部更新”两个后端入口。 | `ui/mod_update_flow.py`、`update_dialogs.py` | 保留并改进 | 4 | 显示全部检查状态、时间和错误；默认选中可更新项；全选只是同一流程的 UI 动作。 |
| DEL-01 | 删除安全检查 | 要求纯数字 ID、精确的 `LibraryRoot\ID` 直接子目录，拒绝 reparse point。 | `core/file_safety.py`、`tests/test_file_safety.py` | 保留并加强 | 3 | 同时防护根目录、SteamCMD、junction 和外部路径。 |
| DEL-02 | 删除方式 | `shutil.rmtree`/`unlink` 永久删除，成功后删数据库；没有回收站。数据库删除失败只返回 false，调用方仍可能视为成功。 | `core/mod_service.py`、`gui.py` | 替换 | 3 | 默认回收站；失败后需二次明确确认才能永久删除；数据库失败返回部分失败。 |
| DEL-03 | 删除并重下 | 复用删除函数，成功后调用公共下载入口。 | `gui.py:delete_mod_and_redownload` | 保留并改进 | 3/5 | 复用同一安全删除服务，再加入共享队列。 |

## 6. Workshop 浏览器

| ID | 能力 | V1 实际行为 | 源码/测试证据 | V2 处置 | 批次 | V2 验收要点 |
|---|---|---|---|---|---|---|
| WEB-01 | 内嵌浏览器 | Qt WebEngine 打开 Stellaris Workshop；有后退、前进、刷新、当前标题和只读 URL。 | `workshop_browser_dialog.py` | 保留 | 5 | 改用 Evergreen WebView2，缺失时提供微软安装入口。 |
| WEB-02 | 浏览器侧队列 | 左侧队列可添加当前模组、删除、清空、下载；与 URL/ID 下载窗口是两套队列状态。 | `workshop_browser_dialog.py`、`core/workshop_queue.py` | 保留并改进 | 5 | 浏览器和输入窗口共用同一个 `DownloadQueueViewModel`。 |
| WEB-03 | 卡片按钮注入 | Python 内嵌一段大型 JavaScript，为列表卡片注入加号/减号/已下载状态，支持 DOM adapter。 | `core/workshop_browser_injection.py`、相关测试 | 保留并改进 | 5 | 独立 `.js` 资源；保留列表页加入队列体验。 |
| WEB-04 | 导航策略 | 允许 `steamcommunity.com`、`steampowered.com` 及子域的 HTTP/HTTPS；其他主框架导航被清空阻止。 | `core/workshop_browser_policy.py`、`tests/test_workshop_browser_policy.py` | 保留并改进 | 5 | 仅可信 Steam Community 页面可调用桥；非 Steam 链接交给系统浏览器。 |
| WEB-05 | 本地桥 | WebChannel 暴露 `toggleQueueItem(str)`，另有自定义 scheme 和控制台回退；本地入口未验证数字格式、来源页面或数量。 | `ui/workshop_web.py` | 替换 | 5 | 只接收固定 JSON 消息；可信来源、纯数字、去重、最多 100 个 ID。 |
| WEB-06 | 下载状态 | 浏览器打开时从数据库缓存成功记录 ID，并在卡片显示绿色已下载状态。 | `workshop_browser_dialog.py:refresh_downloaded_workshop_ids` | 保留并改进 | 5 | 本地文件系统/有效缓存状态驱动，不让远程数据决定本地存在性。 |

## 7. 应用更新、发布与明确排除项

| ID | 能力 | V1 实际行为 | 源码/测试证据 | V2 处置 | 批次 | V2 验收要点 |
|---|---|---|---|---|---|---|
| APPUP-01 | 检查应用更新 | 查询 V1 GitHub 最新 release，比较数字版本；手动无更新会提示，自动失败只写日志。 | `core/app_updater.py`、`ui/app_update_flow.py`、`tests/test_app_updater.py` | 保留 | 6 | 改指向 V2 仓库；手动显示版本和 notes，自动无更新静默。 |
| APPUP-02 | 下载更新 | 流式下载 ZIP 并显示进度；无取消。下载完立即启动 helper 并退出。 | `core/app_updater.py`、`ui/app_update_flow.py` | 保留并改进 | 6 | Velopack 下载支持取消；完成后可选择立即重启或稍后。 |
| APPUP-03 | 应用更新器 | 自制独立 PyInstaller helper 替换安装目录并重启。 | `updater_helper.py`、`StellarisModManager.spec` | 替换 | 6 | 使用 Velopack 生命周期，不创建自制 updater/helper。 |
| REL-01 | Windows 包 | PyInstaller one-folder ZIP，包含 Qt WebEngine、主程序、helper 和 SteamCMD；CI 有 400 MiB 限制。 | `StellarisModManager.spec`、`build_windows.ps1`、`PACKAGING.md` | 替换 | 6 | .NET `win-x64` self-contained，Velopack `Portable.zip` 为主要下载物。 |
| REL-02 | CI/Release | Windows CI 执行 Ruff、pytest、打包；标签触发 GitHub Release ZIP。 | `.github/workflows/ci.yml`、`release.yml` | 替换 | 0/6 | 批次 0 先建立 restore/format/build/test；发布批次再加入 Velopack 与签名占位。 |
| EX-01 | CLI | V1 提供 download/list/check/update/update-all/set-library-root/show-settings。 | `app.py` | 排除 | 0 | V2 不创建 CLI 项目或入口。 |
| EX-02 | V1 设置/数据库迁移 | V1 数据位于旧应用目录且使用不同 schema。 | `core/runtime_paths.py`、`core/database.py` | 排除 | 1/2 | 不迁移旧设置/SQLite；只允许从 V1 模组文件夹重扫。 |
| EX-03 | 许可证 | V1 审计范围未发现许可证文件。 | V1 文件清单 | 保留现状 | 0 | V2 批次 0 不添加许可证。 |

## 8. V1 自动测试覆盖与 V2 缺口

V1 当前测试源码覆盖 37 个测试函数，主要涉及：

- 搜索和排序；
- Workshop ID/URL 提取、队列去重、浏览器域名策略、DOM adapter 与脚本生成；
- 删除路径边界和 reparse point 拒绝；
- junction 辅助行为和扫描元数据保留；
- SteamCMD 基础成功/失败短语、初次失败不入库、失败保留上次成功时间；
- 更新状态判断；
- 应用版本比较与 ZIP 文件名安全；
- Qt worker 不覆盖 `QThread.finished` 的契约。

V1 自动测试未覆盖、V2 必须补齐的高风险区域：

- 设置原子写入、损坏备份、一次保存完整对象和中英文资源键一致性；
- SQLite `cache_state`、根目录不匹配、全量替换失败和单次最终写入；
- 真实 Windows junction 创建/验证/安全删除以及库切换各失败阶段；
- 空数字目录、非数字目录与扫描摘要；
- SteamCMD 安装下载、异步管道、晚于成功的同 ID 失败终态、目录存在性、超时与进程树取消；
- 回收站删除、永久删除二次确认和数据库删除部分失败；
- 写操作全局互斥、关闭等待/取消；
- WebView2 固定消息结构、来源、类型、数量和纯数字验证；
- Velopack 无更新、有更新、下载失败、取消与延迟重启。

## 9. 批次 0 结论

- 功能矩阵已在任何业务代码之前建立。
- 批次 0 只允许创建 .NET solution、两个生产项目、一个测试项目、仓库配置、README 和 Windows CI。
- 本矩阵中的功能状态均为后续批次目标；批次 0 不实现任何下载、数据库、junction、更新或 UI 业务行为。
- 每完成一个后续批次，应更新相应行的实现状态、测试证据和已知限制。

## 10. 批次 1 实现状态

已完成：

- `SET-01`、`SET-02`、`SET-03`、`SET-04`、`SET-07` 的 Core 层能力：固定设置模型、首次启动判定、完整对象单次保存、同目录临时文件与落盘刷新后的原子替换、损坏文件带时间戳备份，以及 `%LOCALAPPDATA%\StellarisDownloaderV2\` 运行目录结构。
- `DB-01`、`DB-02`、`DB-03` 与 `LIB-06` 的持久化基础：强类型 `ModRecord`、单行 `cache_state`、显式一次初始化、短生命周期连接、根目录绑定、RootMismatch 隔离、事务快照替换、失败回滚后 stale 标记、同 ID 旧元数据保留，以及单次最终 upsert。
- 自动测试覆盖首次默认值、完整设置往返、原子替换失败保护、损坏 JSON 备份、schema 幂等、CRUD、根目录不匹配、旧元数据保留、快照失败回滚和单次最终数据库写入。

验证证据：`JsonSettingsStoreTests`、`AppDataPathsTests`、`SqliteModRepositoryTests`；批次完成时运行 `dotnet restore`、`dotnet format --verify-no-changes`、`dotnet build -c Release` 和 `dotnet test -c Release`。

已知限制与后续批次边界：

- `SET-05` 的 WPF 中英文资源和即时切换留到批次 4；批次 1 尚无 UI 初始化向导。
- `SET-08` 的 Serilog 配置留到应用组合与发布阶段；本批次只建立日志目录。
- SQLite 缓存目前由调用方显式初始化并传入预期库根；批次 2 将接入扫描、junction、切换失败语义和启动修复流程。
- 全进程写操作互斥将在批次 2/3 的业务服务组合时建立；当前 repository 自身只保证 SQLite 事务原子性。

## 11. 批次 2 实现状态

已完成：

- `LIB-01`、`LIB-02`：库路径规范化与边界验证、SteamCMD `281990` junction 检查/创建/目标验证/替换/回滚；普通文件和非空普通目录不会被自动删除，删除 junction 时只移除 reparse point 本身。
- `LIB-03`、`LIB-04`、`LIB-05`：只扫描库根下一层的 ASCII 纯数字目录；统计非数字目录和空数字目录；空目录不进入有效缓存；导入项以目录最后修改时间作为近似安装时间；同 ID 缓存元数据在 API 尚未接入或刷新失败时保留。
- `LIB-07`：切换按“验证并创建目录 → junction 切换并验证 → 原子保存完整设置 → 立即标记旧缓存 stale → 扫描并事务重建”执行。junction 失败不提交设置/缓存；设置保存失败恢复旧 junction；设置提交后的扫描失败保留新设置和新 junction，并返回可重试扫描状态。
- `LIB-08` 的 Core 层启动修复入口：`EnsureJunctionAsync` 可按设置中的库目录检测并修复 junction 目标不一致；UI 启动编排留到批次 4。
- 新增进程级 `WriteOperationCoordinator`，库扫描、junction 修复和库切换使用同一把写锁；批次 3 的下载、更新和删除将复用该实例。

验证证据：`WindowsJunctionManagerTests` 在 Windows 上真实创建、替换和回滚 junction，并验证目标文件不受删除影响；`LibraryServiceTests` 覆盖扫描摘要、提交顺序和三个失败阶段；`LibraryServiceIntegrationTests` 使用真实 JSON 设置、SQLite 和 Windows junction 完成端到端切换；`WriteOperationCoordinatorTests` 验证写操作互斥。

已知限制与后续批次边界：

- 扫描阶段尚未访问 Workshop API；批次 3 接入批量元数据后补齐新 ID 的标题等远程字段，当前同 ID 会保留已有缓存元数据。
- 批次 2 只提供 Core 结果与重试标记；设置窗口、切换摘要和启动时自动编排留到批次 4。
- 删除模组的回收站策略不属于 junction 删除，留到批次 3 的模组操作服务实现。

## 12. 批次 3 实现状态

已完成：

- `STM-01`、`STM-02`、`STM-03`：SteamCMD 在首次使用时从官方 Windows ZIP 安装到 V2 运行数据目录，安装后验证可启动；进程标准输出与错误输出异步读取，支持 60 分钟默认超时、用户取消和进程树终止；成功判断同时验证当前 Workshop ID 的最终输出状态、目标目录存在且非空，退出码仅作为诊断信息。
- `UPD-01`：Workshop 元数据按每批最多 100 个 ID 请求，最多两个批次并行；单批失败与缺失元数据不会阻止其他批次完成。
- `DB-03`、`DL-03`、`DL-04`、`STM-04`、`STM-05`：下载队列去重并顺序执行，复用进程级写锁；取消时当前项和未执行项均产生明确结果；首次失败不创建虚假记录，更新失败保留旧记录与安装快照，文件下载成功后即使元数据获取失败也只进行一次最终数据库写入。
- `UPD-02`：更新检查优先比较 V2 下载时保存的远程更新时间快照；从旧目录导入的记录使用目录最后修改时间作近似判断，并通过结果字段标识；元数据缺失返回 `CheckFailed`。
- `DEL-01`、`DEL-02`、`DEL-03`：删除目标只由当前库根和 ASCII 纯数字 ID 解析为直接子目录；上层解析器和底层 Windows 删除适配器均拒绝 reparse point；默认发送到回收站，失败后仅返回可再次确认永久删除，不自动降级；文件移除成功后才删除缓存记录，数据库失败返回部分失败和重新扫描提示；删除并重新下载复用同一删除核心及公共下载队列。
- 新增强类型结果与接口，包括 `WorkshopMetadata`、`DownloadRequest`、`DownloadResult`、`UpdateCheckResult`、`DeleteResult`、`RedownloadResult`、`ISteamCmdService`、`IWorkshopClient`、`IModOperationService` 和文件删除边界。

验证证据：`WorkshopClientTests`、`ProcessRunnerTests`、`SteamCmdOutputClassifierTests`、`SteamCmdServiceTests`、`ModOperationServiceTests`、`ModDeletePathResolverTests` 与 `WindowsFileDeletionServiceTests` 覆盖批处理边界、超时/取消、同 ID 最终终态、目录验证、单次最终写入、失败保留、缓存状态、安全路径、reparse point 和部分失败；批次完成时共有 69 项自动测试通过。

已知限制与后续批次边界：

- 普通 CI 不访问真实 SteamCMD 或 Workshop 网络；使用 HTTP、进程和文件系统替身以及脱敏日志夹具，真实联网下载留到人工验收。
- 为避免自动测试污染用户回收站，回收站调用通过适配器替身验证；真实 Windows 回收站交互留到人工验收。永久删除只在显式请求时执行，并有真实临时目录测试。
- 批次 3 只完成 Core 层服务；下载/更新选择、删除二次确认、进度与取消界面在批次 4/5 接入。
- `DL-01` 的多行 ID/URL 解析和共享 `DownloadQueueViewModel` 属于批次 5；本批次公共下载服务接收已经验证的强类型请求。
