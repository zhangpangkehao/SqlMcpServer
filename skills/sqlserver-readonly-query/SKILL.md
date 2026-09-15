---
name: sqlserver-readonly-query
description: 通过 SqlMcpServer（MCP）对 SQL Server 执行只读查询与分析。当用户需要查数据库、看表结构、写 SELECT 语句、做数据统计、核对业务数据、排查数据问题，或提到"数据库/表/SQL/查询/字段/记录/数据源/SQL Server/MSSQL"等时使用。仅支持读取，禁止任何增删改。
agent_created: true
---

# SQL Server 只读查询

通过已经挂载的 `sqlserver` MCP 服务器访问 SQL Server。**本能力是只读的**：任何写操作都会被服务端内核拒绝，所以不要尝试构造写语句。

## 何时使用

- 用户想查看某个库有哪些表、某张表有哪些字段
- 用户需要统计、核对、导出（以文本形式）业务数据
- 用户描述一个业务问题，需要先用数据验证
- 用户提到具体的库名/表名/字段名，希望查里面的内容

## 可用工具

| 工具 | 用途 | 关键参数 |
| --- | --- | --- |
| `execute_readonly_query` | 执行单条 SELECT / WITH 查询 | `sql`（必填）、`maxRows`、`format` |
| `list_databases` | 列出实例上的全部数据库 | 无 |
| `list_tables` | 列出某库的用户表 | `database`、`schema` |
| `describe_table` | 查看表的字段定义 | `table`（必填）、`schema`、`database` |
| `get_server_info` | 确认连接目标与版本 | 无 |

## 标准工作流

**永远先探查结构，再写查询。** 不要在不知道列名的情况下猜字段。

1. `list_databases` —— 确认目标库存在（除非用户已明确给出库名）
2. `list_tables(database)` —— 找到目标表
3. `describe_table(table, schema, database)` —— 拿到准确的列名与类型
4. `execute_readonly_query(sql)` —— 执行查询

如果用户已经明确给出了库、表、字段，可以跳过 1–3，直接查询并在报错时回退到探查。

## 写查询的约定

- **T-SQL 方言**：用 `SELECT TOP n`，不要用 `LIMIT`。字符串拼接用 `+`。当前时间用 `GETDATE()`。
- **标识符引用**：名字含空格、中文或保留字时用方括号，例如 `[dbo].[订单表]`、`[SET]`。
- **只有一条语句**：不要写分号拼接的多语句，会被直接拒绝。
- **控制结果规模**：默认最多 200 行。先用 `COUNT(*)` 或 `TOP` 试探规模，再决定是否拉明细；需要更多行时显式传 `maxRows`（上限 10000）。
- **格式选择**：默认 markdown 表格便于阅读；`json` 适合后续程序化处理；`csv` 适合导出。
- **只读函数**：`GETDATE()`、`CAST`、`CONVERT`、`ROW_NUMBER()`、`STRING_AGG`、窗口函数等都可以正常使用。

## 结果呈现（给用户看的部分）

1. **先给结论**：一句话说明查到什么，例如"9 月订单共 1,284 笔，其中 3 笔状态异常"。
2. **再给表格**：把关键字段整理成 markdown 表格，不要把原始结果整块贴出来。
3. **数据量大时**：只展示代表性样本 + 汇总数字，并说明已截断。
4. **NULL 与空字符串**：结果里的 `NULL` 就是数据库 NULL，不要解释成"0"。
5. 用中文解释；字段名如果是英文，给出中文含义。

## 常见错误与处置

| 现象 | 原因 | 处置 |
| --- | --- | --- |
| `检测到被禁止的关键字 X` | 语句里出现了写操作词 | 改写成纯 SELECT；不要试图用注释或引号绕过 |
| `检测到多条语句` | 用了分号拼接 | 拆成单条查询，分多次调用 |
| `对象不存在`（#208） | 库名/架构名/表名不对 | 回到 `list_tables` / `describe_table` 重新确认 |
| `权限不足`（#229/#230） | 连接账号没有该对象的 SELECT 权限 | 告知用户需要为账号授予 SELECT 或加入 db_datareader |
| `无法与 SQL Server 建立会话` | 实例未启动 / TCP 未启用 / 端口未放行 | 提示用户检查 SQL Server 服务与 1433 端口 |
| 结果被截断 | 超过 `maxRows` | 收紧 WHERE、加聚合，或显式调大 `maxRows` |

## 安全边界（不要触碰）

- 不要尝试 INSERT / UPDATE / DELETE / MERGE / TRUNCATE
- 不要尝试 CREATE / ALTER / DROP
- 不要尝试 EXEC / EXECUTE 动态 SQL
- 不要尝试 SELECT ... INTO、OPENROWSET、OPENQUERY、OPENDATASOURCE
- 不要尝试用注释、方括号、字符串拼接规避关键字检测——服务端在词法层面识别，绕不过去

以上请求会被拒绝并返回 `isError=true`，此时应当**改写为只读查询**，而不是换一种写法再试一次同样的写操作。

## 隐私提醒

查询结果可能包含业务敏感数据。不要主动扩大查询范围去"顺便看看"无关的表；只取用户当前问题所需的最小数据集。
