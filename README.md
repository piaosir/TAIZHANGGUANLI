# 市场部台账管理系统

行业市场组合同台账的本地化桌面软件。100% 纯本地、离线、加密、可审计。
把现有 Excel 台账升级为"数据录入 + 目标达成看板 + 文件摆渡式离线协同"系统。

> 设计蓝图见 [架构设计方案-v1.md](架构设计方案-v1.md)。

## 技术栈
.NET 9 · WPF · WebView2 内嵌前端 · SQLite(SQLCipher，规划中) · NPOI(Excel) · ECharts(图表)

## 解决方案结构（`src/WeitongLedger.sln`）
| 项目 | 职责 |
|---|---|
| `Weitong.Ledger.Core` | 领域模型、5级漏斗(含成交概率)、金额分制、**双口径统计引擎**(纯函数可测) |
| `Weitong.Ledger.Data` | Excel 导入(NPOI，容错脏数据)、看板数据导出；(规划)SQLCipher 持久化、离线同步 |
| `Weitong.Ledger.App` | WPF + WebView2 桌面壳；`wwwroot/` 内前端看板(dashboard.html + 本地 echarts) |
| `Weitong.Ledger.Cli` | M0 验证台：导入真实 xlsx 打印统计口径 |

## 构建与运行
```bash
# 验证口径（控制台，导入现有 xlsx 打印统计）
dotnet run --project src/Weitong.Ledger.Cli -c Release

# 运行桌面应用
dotnet run --project src/Weitong.Ledger.App -c Release

# 发布为自包含文件夹（U盘拷贝双击即用，无需装 .NET）
dotnet publish src/Weitong.Ledger.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o 发布/台账管理系统
```
> 注：`dotnet` 用系统 .NET 9 SDK；解析 xlsx 的 Python 脚本用 `C:\Users\85256\AppData\Local\Programs\Python\Python313\python.exe`（含 openpyxl）。

## 进度
- [x] M0 口径验证：导入真实 451 行，已签约收入 ¥33,174万 与领导汇总(33,126万)吻合(差0.14%)
- [x] M1 数据地基：领域模型 + Excel 导入 + 双口径引擎 + ECharts 看板 + WPF 桌面壳(可发布 exe)
- [ ] M1 续：SQLCipher 加密持久化
- [ ] M2：WebView2 内嵌 Univer 类Excel录入
- [ ] M3：销售个人达成页
- [ ] M4：`.cvpk` 离线协同(U盘汇总到经理)★
- [ ] M5：登录/RBAC/数据包签名/审计
- [ ] M6：MSIX + 代码签名分发

## 里程碑数据口径校验
| 指标 | 引擎算出 | 表1「行业团队」 | 偏差 |
|---|---|---|---|
| 已签约预计收入 | ¥33,174 万 | 33,126 万 | 0.14% |
| 已签约预计利润 | ¥29,367 万 | 29,320 万 | 0.16% |
