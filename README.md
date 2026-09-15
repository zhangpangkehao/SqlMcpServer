# SqlMcpServer — SQL Server 只读 MCP 服务器

[![Release](https://img.shields.io/github/v/release/zhangpangkehao/SqlMcpServer)](https://github.com/zhangpangkehao/SqlMcpServer/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/zhangpangkehao/SqlMcpServer/total)](https://github.com/zhangpangkehao/SqlMcpServer/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078d4)](#)

让 AI 助手通过 [MCP](https://modelcontextprotocol.io) 直接查询你的 SQL Server。

**设计目标**：复制即用。单个 exe，不需要启动任何后台服务，不需要安装运行时，双击即可配置。

---

## 下载

**→ [前往 Releases 下载最新版](https://github.com/zhangpangkehao/SqlMcpServer/releases/latest)**

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `SqlMcpServer-1.0.0-win-x64-selfcontained.exe` | 73 MB | **推荐**。自包含 .NET 运行时，双击即用，无需安装任何依赖 |
| `SqlMcpServer-1.0.0-win-x64-framework.zip` | 1.8 MB | 体积小，需预装 [.NET 9 运行时](https://dotnet.microsoft.com/download/dotnet/9.0) |

要求 Windows x64 + SQL Server 2012 及以上（实测 2019 / 2022）。下载后请用同页的 `SHA256SUMS.txt` 校验完整性。

---

## 为什么好用

- **零部署负担** —— 自包含单文件 exe，丢到一个固定目录就能用，不需要 Windows 服务、不需要 IIS、不需要后台进程常驻
- **stdio 传输** —— 由 AI 客户端按需拉起进程、用完关闭，不占端口、不暴露网络
- **双击即配置** —— 手动运行时进入中文交互控制台：测连接、填账号、生成客户端配置片段，一气呵成
- **内核级只读** —— 写操作不是"靠 AI 自觉"，而是在词法层面强制拦截，注释混淆、引号包裹、分号拼接都绕不过
- **对模型友好** —— 结果自动渲染成 markdown 表格，行数与单元格长度双重限流，不会把上下文撑爆
- **诊断到位** —— 把 SQL Server 的错误码翻译成可执行的排查建议，AI 也能读懂

---

## 快速开始

```bat
:: 1. 双击运行下载到的 SqlMcpServer.exe，进入交互控制台
SqlMcpServer.exe

:: 2. 选 [2] 配置数据库连接，选 [1] 测试连通性
:: 3. 选 [4] 生成 MCP 客户端配置片段，粘贴进客户端的 MCP 配置
```

详细步骤、各客户端配置位置、故障排查见 **[docs/接入指南.md](docs/接入指南.md)**。

命令行自测：

```bat
SqlMcpServer.exe --test                                    :: 测试默认配置
SqlMcpServer.exe --server localhost --integrated --test    :: 指定参数测试
SqlMcpServer.exe --help
```

---

## 提供的工具

| 工具 | 用途 |
| --- | --- |
| `execute_readonly_query` | 执行单条 SELECT / WITH 查询（`sql` / `maxRows` / `format`） |
| `list_databases` | 列出实例上的数据库 |
| `list_tables` | 列出指定库的用户表 |
| `describe_table` | 查看表的字段定义 |
| `get_server_info` | 确认连接目标与版本 |

---

## 目录结构

```
sqlserverMCP/
├── src/SqlMcpServer/              源码（C# / .NET 9）
│   ├── Program.cs                 入口：stdio 服务 / 交互控制台双模式
│   ├── Mcp/                       MCP 协议层（JSON-RPC over stdio）
│   │   ├── McpServerHost.cs       生命周期、方法分发、协议版本协商
│   │   ├── McpTool.cs             工具模型与 JSON Schema 构造
│   │   └── JsonRpc.cs             JSON-RPC 2.0 编解码
│   ├── Sql/
│   │   ├── SqlGuard.cs            ★ 只读校验（词法级，安全核心）
│   │   ├── SqlGateway.cs          数据访问、元数据查询、错误诊断
│   │   └── QueryResult.cs         结果模型与 markdown/json/csv 渲染
│   ├── Tools/SqlToolset.cs        五个工具的注册与实现
│   ├── Config/ServerConfig.cs     三层配置（文件 / 环境变量 / 命令行）
│   └── Cli/InteractiveShell.cs    双击时的配置向导
├── dist/                          发布产物（.gitignore 已排除，请从 Releases 下载）
│   ├── win-x64-selfcontained/     自包含单文件（推荐，免运行时）
│   └── win-x64-framework/         依赖 .NET 9 运行时，体积小
├── skills/
│   ├── sqlserver-readonly-query/  让 AI 会用这套工具的技能包
│   └── mcp-skill-template/        新增其它 MCP 技能的样板
├── docs/接入指南.md                面向使用者的完整接入文档
├── tests/                         JSON-RPC 协议测试报文
└── build/dnet.sh                  沙箱环境的构建辅助脚本
```

---

## 从源码构建

需要 .NET 9 SDK：

```bat
cd src\SqlMcpServer
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o ..\..\dist\win-x64-selfcontained
```

> `IncludeNativeLibrariesForSelfExtract=true` 不可省略：Microsoft.Data.SqlClient 含原生 SNI 组件，不加这个参数不会被打进单文件。

---

## 安全说明

只读约束由 `Sql/SqlGuard.cs` 强制执行，共五道检查：词法中性化 → 单语句约束 → 必须以 SELECT/WITH 开头 → 关键字黑名单 → 行数与超时上限。

**建议同时使用只读数据库账号**（`db_datareader` 角色），形成程序侧与数据库侧的双重防线。详见接入指南第五节。
