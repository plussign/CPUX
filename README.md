# CPUX

CPU-Z 风格的 Windows 本机硬件信息查看器。原生 WPF，中文界面，普通用户运行，含离线 CPU/GPU 参考资料库。无内核驱动、性能测试、网络请求或信息提交。

## 运行

- 系统：Windows 10 / 11 x64。
- 已生成的 `artifacts\CPUX-win-x64\CPUX.exe` 是包含运行时的单文件便携版，直接双击，无需安装 .NET。
- `powershell -ExecutionPolicy Bypass -File .\Build.ps1`：重新生成依赖运行时的版本，需要 **.NET 8 Desktop Runtime x64**。
- `powershell -ExecutionPolicy Bypass -File .\Build.ps1 -SelfContained`：重新生成包含运行时的单文件便携版；首次需要还原微软运行时包，无需在目标机器安装 .NET。
- 源码开发需要 .NET 8 或更新的 SDK，执行 `dotnet run --project CPUX.csproj`。

## 功能与数据边界

| 页面 | 本机数据来源 |
| --- | --- |
| 概览 | 处理器、内存、图形设备、平台摘要 |
| 处理器 | WMI 插槽/核心/线程/标称频率，CPUID 标识与指令集；libcpuid 代号/制程识别表 |
| 缓存 | Windows 缓存拓扑，按级别、类型、容量分组，显示实例数 |
| 主板 | SMBIOS/WMI 制造商、型号、版本、BIOS、整机信息；注册表安全启动状态 |
| 内存 | 固件内存模块、料号、容量、DDR 类型、配置传输率、位宽、Rank、配置电压 |
| 显卡 | DXGI 64 位显存与 PCI 标识；WMI 驱动；离线 GPU 芯片、架构、制造、显存/计算单元/功耗/接口等参考规格 |
| 存储 | Windows 物理磁盘和本地卷，容量、固件、分区、可用空间 |
| 系统 | Windows 版本、启动时间、虚拟化能力与状态 |
| 资料库 | 记录数、快照、来源、匹配规则与署名 |

顶部 CPU 使用率与系统可用物理内存每秒更新。硬件静态信息按刷新或 F5 重读；报告导出的是最近一次硬件快照。CPU 使用率不显示于超过 64 逻辑处理器的系统，避免把单一处理器组的使用率误报成整机数据。

数值可选择复制；悬停显示来源；整份报告支持复制和 TXT / JSON 本地导出。报告可能包含设备序列号和 PnP 标识。程序不上传报告。

不安装驱动读取实时核心频率、电压、温度、SPD 时序表或 XMP/EXPO。内存来自固件，不代表直接读取 SPD；DDR 传输率使用 MT/s，配置电压不是实时测量；共享显存是上限而非已占用容量。DXGI 适配器可能包含虚拟/间接显示适配器，不等于物理显卡数。多插槽系统的 CPUID 是当前执行 CPU 的自述，CPU 资料匹配则分别使用 WMI 各插槽的签名。硬件/虚拟机/固件可能隐去或错误报告数据，`未读取` 不代表不支持。Windows WMI 的磁盘接口和介质描述不用于推断 NVMe 或 SSD。

## 离线资料库（1.1）

内置 **888 条 CPU 识别规则、2,923 款 GPU 资料**，以及 2026.10.26 版 PCI 名称库。数据压缩为资源嵌入 EXE，不依赖外部数据库服务或运行时网络。

- CPU：来自 [libcpuid](https://github.com/anrieff/libcpuid) 的 Intel/AMD 代号和制程表；独立实现保守匹配，签名字段必须符合，再比较品牌模式。它不是逐 SKU 的完整 CPU 规格库，不提供所有 CPU 的 TDP、插槽或发布日期。
- GPU：以 [Open GPU DB](https://github.com/onepunk/open-gpu-db) v1.3.0 的规范化数据为主，使用 [RightNow GPU Database](https://github.com/RightNow-AI/RightNow-GPU-Database) 为相同 ID、名称和厂商补充更多字段。前者的已维护字段优先。上游基于 dbgpu / TechPowerUp，部分字段附厂商修正，不能把整库视为已经逐条实测验证。
- PCI：来自 [PCI ID Repository](https://pci-ids.ucw.cz/)，用于厂商、设备、子系统名称及芯片提示。
- “本机读取”与“参考资料”在卡片、TXT 和 JSON 中分别标记，数据库不会覆盖本机测量值。数据库的 API 版本、频率、功耗、供电、尺寸是对应库条目的参考设计信息，非当前驱动能力验证或实际板卡确认。
- GPU 保留 Ti / SUPER / 移动版后缀，结合显存容量和 PCI 芯片提示区分变体；冲突时不采用规格，未唯一确定时仅显示候选共同字段，未知设备不进行相近名称猜测。

`Data/manifest.json` 记录快照日期、版本、输入文件 URL 与 SHA-256。`_internal/import_databases.py` 可从 `.build/upstream` 的原始缓存重建资源，不执行上游代码；普通编译直接使用提交的资源，无需重新下载。第三方资料的许可证与改动说明在 `ThirdPartyNotices/`，发行包附带同目录和 `数据库版本.json`；应用本身不因此改为开源。

## 高 DPI

清单声明 Per-Monitor V2（回退到 Per-Monitor），采用 WPF 设备无关单位、矢量控件、布局取整和文本显示模式。卡片按可用宽度切换单/双列；主内容、导航独立滚动，工具栏可换行；初始窗口适配工作区。保留原生窗口边框与缩放、最大化操作。实现参考 [微软 DPI 清单说明](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests) 和 [WPF 示例](https://github.com/microsoft/WPF-Samples/tree/main/PerMonitorDPI)。

## 采集架构与验证

UI 使用独立的短生命周期采集子进程，WMI 调用不阻塞窗口；45 秒超时会终止子进程，关闭窗口同样取消采集。临时 JSON 在系统临时目录中传递，读取后删除。某项 API 失败保留其它来源的数据并展示提示。全量失败后可刷新重试；刷新失败保留旧快照并标注时间。

无外部 NuGet 包。WMI 通过 Windows 原生 COM 接口访问，DXGI、缓存拓扑、实时内存和系统 CPU 时间通过只读 Win32 API 获取。

命令行采集（PowerShell 使用 `Start-Process -Wait` 等待 Windows GUI 程序）：

```powershell
Start-Process .\artifacts\CPUX-win-x64\CPUX.exe -ArgumentList '--report', 'E:\AgentProject\CPUX\artifacts\hardware.json' -Wait
Start-Process .\artifacts\CPUX-win-x64\CPUX.exe -ArgumentList '--self-test', 'E:\AgentProject\CPUX\artifacts\self-test.txt' -Wait
```

`--self-test` 检查本机 API、DPI 上下文、序列化，以及 Intel/AMD 识别、移动后缀、GPU 芯片/显存变体、资料冲突和未知设备回退。采集命令不会执行跑分。CLI `--report` 在进程内同步采集；自动化调用者应设置超时。UI 的采集有独立的 45 秒超时。

`--layout-check <输出目录>` 渲染真实 WPF 控件树，检查 100% / 150% / 200% / 300% 像素密度和窄窗口的卡片/数值布局，写出 PNG 与检查日志。此检查使用合成长文本数据，不会切换 Windows 显示设置；不能替代实际跨屏 DPI 测试。

人工验收建议：100% / 150% / 200% / 300% 缩放、不同 DPI 屏幕之间移动、窄窗口、键盘导航、长型号文字、WMI 禁用、虚拟机和多显卡环境。本机单次验证不能替代这些硬件环境的覆盖。
