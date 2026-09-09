# MissFisher AutoDuty Scheduler

一个用于协调 [MissFisher](https://github.com/Makuragami/MissFisher) 与 AutoDuty 的 Dalamud 插件。

当 MissFisher 处于等待状态，并且距离下一个可用钓鱼窗口仍有足够时间时，本插件会暂停当前钓鱼任务，选择一个未满级战斗职业并调用 AutoDuty。时间不足或 AutoDuty 流程结束后，插件会切回原捕鱼人套装，并恢复指定的 MissFisher 图鉴、分组或合集。

本插件只负责整体调度，不修改 MissFisher、AutoDuty 或其他插件。

## 主要功能

- 直接读取 MissFisher 当前目标窗口，不自行计算天气或重新排列目标。
- 只有等待时间同时满足触发阈值和完整时间预算时才调用 AutoDuty。
- 选择拥有套装的最低等级 15-99 级正式战斗职业，同等级按套装顺序选择；排除青魔法师。
- 从当前职业可用、未排除且 AutoDuty 存在路径的候选内容中按等级由高到低尝试。
- 支持多选排除不希望运行的副本，默认排除 `978` 星海深幽寻因星晶镜。
- 支持设置 AutoDuty 单次运行时限，超时后请求停止并退出，再恢复捕鱼状态。
- “恢复目标”下拉菜单直接读取 MissFisher，可选择 `鱼类图鉴（非副本）`、内置分组或自定义合集。
- 每个关键状态都会保存检查点。插件重新加载后会先核对游戏和 AutoDuty 状态，再继续监控或恢复捕鱼。
- 只有由本调度周期启动的 AutoDuty 才会被插件停止，不会接管外部手动任务。

## 依赖

- XIVLauncherCN / Dalamud API 15
- MissFisher 2.2.4.1
- AutoDuty
- vnavmesh，以及 AutoDuty 正常运行所需的战斗与路径插件

MissFisher 内部接口可能随版本变化。升级 MissFisher 后如果恢复目标列表无法读取，请先停用调度并等待本项目适配。

## 下载与安装

1. 下载 [FisherDutyScheduler 0.6.0](release/FisherDutyScheduler-0.6.0.zip) 并解压到固定目录。
2. 打开 Dalamud 设置的“测试版”页面，将解压后的 `FisherDutyScheduler.dll` 添加为开发插件。
3. 在插件列表中启用 **Fisher Duty Scheduler**。
4. 输入 `/fds` 打开配置窗口。

首次使用时建议保持“只观察”开启，确认插件显示的 MissFisher 剩余时间和触发状态正确，再进行受监督测试。

## 配置说明

- **启用调度**：允许插件安排新的调度周期。关闭后不会开始新周期，正在进行的周期会在安全点恢复捕鱼。
- **只观察**：只显示判断结果，不暂停 MissFisher，也不调用 AutoDuty。
- **等待阈值**：MissFisher 距离下一目标至少还有多少分钟时才考虑调度。
- **预计耗时 / 恢复预留 / 安全余量**：共同构成一次调度所需的完整时间预算。
- **单次运行时限**：AutoDuty 超过该时间后请求停止和退出。
- **排除副本**：可多选不希望调度器交给 AutoDuty 的候选。
- **恢复目标**：返回捕鱼人后需要恢复的 MissFisher 图鉴、分组或合集。

选择的恢复目标会在每个周期开始时冻结，因此周期中途修改配置只会影响下一轮。

## 命令

```text
/fds               打开配置
/fds enable        启用调度
/fds disable       停止安排新周期，并在安全点恢复捕鱼
/fds status        打开状态窗口
/fds reset         清除故障状态
/fds abort         停止本周期启动的 AutoDuty，并进入捕鱼恢复
/fds test-restore  不调用 AutoDuty，快速测试职业切换和 MissFisher 恢复
```

快速恢复测试要求正常调度已关闭、AutoDuty 已停止、角色已离开副本，并且当前以捕鱼人运行 MissFisher。测试会短暂切换至一个符合条件的战斗职业，然后切回捕鱼人并验证所选目标能否恢复。

## 构建

安装 .NET 10 SDK 后，在项目目录运行：

```powershell
.\build.ps1
```

也可以显式指定 `dotnet.exe`：

```powershell
.\build.ps1 -DotNet 'C:\path\to\dotnet.exe'
```

第三方自动化插件可能违反游戏服务条款，请自行评估账号风险。请勿在真人匹配队伍中使用。
