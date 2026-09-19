# EnvMonitor 环境监控上位机 Demo 详细设计

**文档类型**：代码反向设计文档  
**依据版本**：当前 `main` 分支代码  
**生成日期**：2026-09-19  
**目标框架**：.NET 8 / WPF

## 1. 文档范围

本文档根据当前仓库中的解决方案、项目文件、协议实现、TCP 服务、WPF ViewModel 和测试代码反向整理而成。文档描述的是当前代码实际行为，不把尚未实现的功能当作已完成能力。

覆盖范围：

- 解决方案与项目边界
- TCP 二进制协议和 CRC16
- 帧拆包与错误恢复
- 模拟下位机和故障注入
- 上位机客户端、请求匹配、超时与重连
- 业务服务层数据解析
- WPF MVVM、轮询、报警和曲线
- 配置、日志、构建和测试
- 已知限制与后续风险

主要代码依据：`EnvMonitor.Protocol`、`EnvMonitor.Communication`、`EnvMonitor.Simulator`、`EnvMonitor.App`、`EnvMonitor.Tests`。

## 2. 系统目标

系统用于在没有真实下位机设备时，模拟一个环境传感器设备，并通过 TCP 让 WPF 上位机完成：

1. 建立 TCP 连接。
2. 发送读取、继电器和心跳命令。
3. 接收并解析二进制帧。
4. 处理半包、粘包、CRC 错误、超时和断线。
5. 以 MVVM 方式显示传感器数据。
6. 对温度、湿度超阈值进行状态报警。
7. 显示最近两分钟的温度曲线。

## 3. 总体架构

```text
+----------------------+       +--------------------------+
| EnvMonitor.App       |       | EnvMonitor.Simulator     |
| WPF + MVVM           |       | TCP virtual device      |
| MainViewModel        |       | SimulatorServer         |
+----------+-----------+       +------------+-------------+
           |                                 |
           v                                 v
+----------------------------------------------------------+
| EnvMonitor.Communication                                |
| TcpClientService / DeviceService / IFrameClient         |
+----------------------------+-----------------------------+
                             |
                             v
+----------------------------------------------------------+
| EnvMonitor.Protocol                                      |
| Frame / Crc16 / FrameParser                             |
+----------------------------------------------------------+

EnvMonitor.Tests 直接引用 Protocol、Communication、Simulator，
通过协议单测、TCP 集成测试和故障注入测试验证行为。
```

依赖方向：

- `Communication -> Protocol`
- `Simulator -> Protocol`
- `App -> Communication`
- `Tests -> Protocol、Communication、Simulator`
- `Protocol` 不依赖 TCP、WPF 或业务层

## 4. 项目与职责

### 4.1 EnvMonitor.Protocol

职责是定义和验证通信协议：

- `Frame`：不可变命令、序号和 Payload；负责编码/解码。
- `Crc16`：实现 Modbus CRC16，初始值 `0xFFFF`，多项式 `0xA001`。
- `FrameParser`：从任意 TCP 字节片段中恢复完整 Frame。

该项目不处理网络连接，也不决定某个命令的业务含义。

### 4.2 EnvMonitor.Communication

职责是将协议帧变成可靠的请求/响应通信和业务 API：

- `IFrameClient`：客户端抽象。
- `TcpClientService`：TCP 连接、写锁、接收循环、Seq 匹配、超时、断线和自动重连。
- `DeviceService`：读取传感器、设置继电器、心跳和 Payload 校验。
- `SensorData`：强类型传感器数据记录。

### 4.3 EnvMonitor.Simulator

职责是提供无硬件 TCP 下位机：

- `SimulatorServer`：监听 TCP、按连接创建拆包器、派发响应。
- `VirtualDevice`：维护传感器和继电器状态。
- `FaultOptions`：控制丢帧、延迟、断线、CRC 错、半包和粘包。

### 4.4 EnvMonitor.App

职责是用户界面和应用生命周期：

- `App`：读取 JSON 配置、初始化 Serilog、组装对象和释放资源。
- `MainViewModel`：连接命令、轮询、报警、曲线和 UI 状态。
- `MainWindow.xaml`：WPF 绑定、数据卡片、继电器、曲线和事件区域。

### 4.5 EnvMonitor.Tests

职责是验证协议、通信、业务解析和异常行为。测试使用端口 `0` 启动模拟器，避免依赖固定端口。

## 5. 协议详细设计

### 5.1 帧布局

```text
偏移       长度       字段
0          1          Header1 = 0xAA
1          1          Header2 = 0x55
2          2          Length，大端序
4          1          Command
5          2          Sequence，大端序
7          N          Payload
7 + N      2          CRC16，低字节在前
```

最小帧没有 Payload，总长为 9 字节。`Frame.MinimumLength = 9`，`Frame.MaximumLength = 1024`。

### 5.2 编码流程

`Frame.Encode()` 的实际流程：

1. 计算 `Length = 9 + Payload.Length`。
2. 写入 `0xAA 0x55`。
3. 以大端序写入 Length 和 Sequence。
4. 写入 Command 与 Payload。
5. 对从 Header 到 Payload 的全部字节计算 CRC16。
6. 将 CRC 低字节写入前，高字节写入后。

标准空读帧测试向量：

```text
AA 55 00 09 01 00 01 8E 93
```

### 5.3 解码校验

`Frame.Decode()` 依次检查：

- 缓冲区长度是否在 9 到 1024 之间。
- 前两个字节是否为帧头。
- 声明长度是否等于实际缓冲区长度。
- 计算 CRC 是否等于线路中的 CRC。

任一校验失败都会抛出 `ArgumentException`。

### 5.4 命令定义

| 命令             | 请求                  | 成功响应                                                                         | 错误响应           |
| ---------------- | --------------------- | -------------------------------------------------------------------------------- | ------------------ |
| `0x01 Read`      | 空                    | 温度 Int16 BE、湿度 UInt16 BE、压力 UInt16 BE，均除以 10，再加 Relay Byte，共 7B | `0x81 + ErrorCode` |
| `0x02 SetRelay`  | Channel Byte、On Byte | Channel Byte、On Byte                                                            | `0x82 + ErrorCode` |
| `0x03 Heartbeat` | 空                    | 空                                                                               | `0x83 + ErrorCode` |

当前模拟设备只接受 Channel `1`，On 值只能是 `0` 或 `1`。

错误码：

- `0x01`：非法命令
- `0x02`：非法 Payload
- `0x03`：非法继电器通道

## 6. 帧拆包设计

`FrameParser` 内部使用 `List<byte>` 缓存。每次 `Feed(byte[])`：

1. 将新字节追加到缓存。
2. 搜索 `AA 55`。
3. 丢弃帧头前的垃圾字节。
4. 不足 4 字节时保留缓存等待下次输入。
5. 读取 Length；小于 9 或大于 1024 时丢弃一个字节重新同步。
6. 数据不足一个完整帧时保留缓存。
7. 对候选帧调用 `Frame.Decode()`。
8. CRC 或解码失败时只丢弃一个候选起始字节，继续搜索。
9. 成功时移除完整帧并 `yield return`。

这套策略支持 TCP 读边界与协议帧边界不一致的情况，包括：

- 帧头被拆成两次读取。
- 一帧被拆成多次读取。
- 多帧在一次读取中合并。
- 错误帧后紧跟合法帧。

当前实现为学习阶段的 `List.RemoveRange` 方案，吞吐量很高时可以替换为环形缓冲区或 `PipeReader`。

## 7. 模拟器设计

### 7.1 生命周期

`SimulatorServer.StartAsync()`：

- 创建 `TcpListener(IPAddress.Loopback, port)`。
- `port = 0` 时由操作系统分配临时端口。
- 暴露实际端口 `Port`。
- 启动 Accept 循环。

每个客户端有独立的 `FrameParser` 和处理任务。`StopAsync()` 取消服务器 CTS、停止 Listener、关闭客户端并等待 Accept 循环结束。

### 7.2 虚拟设备状态

`VirtualDevice` 在锁内处理请求，维护：

- 温度：初始 `25.0°C`，范围约 `20.0-30.0°C`。
- 湿度：初始 `48.0%`，范围约 `30.0-70.0%`。
- 压力：初始 `101.3kPa`，范围约 `98.0-104.0kPa`。
- Relay：初始关闭。

每次 Read 命令都会使用 `Random.Shared` 做小幅波动。

### 7.3 故障注入

`FaultOptions` 支持：

- `DropProbabilityPercent`：按概率丢弃响应。
- `ResponseDelay`：发送前异步延迟。
- `CloseAfter`：每个客户端连接建立后延迟关闭。
- `CrcErrorResponseNumber`：指定序号的响应损坏 CRC。
- `FragmentResponses`：一帧拆成两次写入。
- `CoalesceResponses`：将同一批次多帧合并写入。

命令行 `Program.cs` 将参数转换成 `FaultOptions`。测试直接构造 `FaultOptions`，因此不依赖随机概率。

## 8. 上位机通信设计

### 8.1 请求发送

`TcpClientService.SendAsync()`：

1. 检查已连接和超时参数。
2. 原子递增 `_nextSequence`，跳过仍被占用的 Seq。
3. 创建 `RunContinuationsAsynchronously` 的 TCS 并放入 `_pending`。
4. 使用 `SemaphoreSlim` 串行化 NetworkStream 写操作。
5. 等待响应 TCS 或超时 Task。
6. 超时、取消或写失败时从 `_pending` 清理。

`PendingCount` 用于测试和诊断。

### 8.2 接收循环

接收循环使用独立 `FrameParser` 处理 NetworkStream 的任意读块：

- Seq 在 `_pending` 中：完成对应 TCS。
- Seq 不在 `_pending` 中：触发 `UnsolicitedFrame`。
- CRC 错误：由 FrameParser 丢弃，原请求最终超时。
- 读到 0 或发生异常：完成未决请求并进入断线处理。

### 8.3 状态与自动重连

`ConnectionState`：

- `Connecting`
- `Connected`
- `Disconnected`
- `Reconnecting`

意外断线启动唯一重连循环，等待时间为 1、2、4、8 秒，最大 30 秒。重连使用新的 `TcpClient`、NetworkStream 和接收 CTS。手动 `DisconnectAsync()` 会取消重连循环，并完成所有未决请求。

## 9. 业务服务设计

`DeviceService` 隔离了 Frame 细节：

- `ReadAsync()`：发送 `0x01`，要求 7 字节 Payload，按大端序解析并除以 10。
- `SetRelayAsync(channel, on)`：发送两字节设置请求，检查响应 Channel 和状态都匹配。
- `HeartbeatAsync()`：发送 `0x03`，要求空响应。

错误命令会转换为 `InvalidOperationException`；长度或字段不合法会转换为 `InvalidDataException`。

## 10. WPF 与 MVVM 设计

### 10.1 启动组装

`App.OnStartup()`：

1. 创建 Serilog File Sink，路径为 `logs/app-.log`。
2. 从输出目录的 `appsettings.json` 加载 `AppSettings`。
3. 校验主机、端口、轮询周期和超时。
4. 创建 `TcpClientService`、`DeviceService`、`MainViewModel`。
5. 将 ViewModel 作为 `MainWindow.DataContext`。

`App.OnExit()` 停止 ViewModel、释放客户端并刷新日志。

### 10.2 ViewModel 状态

`MainViewModel` 暴露：

- 状态和连接按钮文本
- 温度、湿度、压力、继电器状态
- 温湿度报警状态和状态文案
- 最近 120 个 `DateTimePoint`
- `ConnectCommand`、`ReconnectCommand`、`ToggleRelayCommand`

### 10.3 轮询与曲线

`DispatcherTimer` 按配置周期 Tick：

- `_isPolling` 防止上一轮未结束时重入。
- 成功读取后在 UI 线程更新属性和曲线集合。
- 曲线超过 120 点时删除最旧点。
- 断开连接时停止 Timer 并清空曲线。

### 10.4 报警

报警阈值来自配置：

- `Temperature > Thresholds.Temperature`
- `Humidity > Thresholds.Humidity`

报警时状态为 `Alarm`，对应数值通过 XAML `DataTrigger` 显示红色；读取超时显示黄色状态。当前报警采用状态栏和数值颜色，不使用弹窗。

## 11. 配置和日志

默认配置：

```json
{
  "Device": {
    "Host": "127.0.0.1",
    "Port": 9000,
    "PollIntervalMs": 1000,
    "RequestTimeoutMs": 1000
  },
  "Thresholds": {
    "Temperature": 30,
    "Humidity": 70
  }
}
```

配置文件通过 `CopyToOutputDirectory=PreserveNewest` 复制到 App 输出目录。Serilog 使用按天滚动的文件 Sink，保留 14 个文件。当前日志主要由 WPF ViewModel 和 App 生命周期写入，包含连接、状态变化、轮询、超时、继电器和异常。

## 12. 测试设计与结果

测试项目当前覆盖 30 个测试：

| 测试层          | 覆盖内容                                         |
| --------------- | ------------------------------------------------ |
| Protocol        | CRC16 标准向量、帧字节序、编解码往返             |
| FrameParser     | 完整帧、半包、粘包、垃圾、非法长度、CRC 恢复     |
| Simulator       | Read、SetRelay、Heartbeat、动态数据、错误帧      |
| Communication   | 并发请求、Seq 匹配、超时、Pending 清理、断线重连 |
| DeviceService   | 大端解析、缩放、Payload 校验、错误响应           |
| Fault injection | 丢帧、延迟、CRC 错、fragment、coalesce、非法命令 |

执行命令：

```powershell
.\build.ps1
```

验收结果：30/30 通过，0 错误。WPF 图表依赖目前仍报告 3 类 `NU1701` 传递依赖警告：OpenTK、OpenTK.GLWpfControl、SkiaSharp.Views.WPF。该警告来自 LiveCharts 包依赖链，当前不影响构建和测试。

## 13. 运行与演示顺序

1. `dotnet run --project EnvMonitor.Simulator -- --port 9000`
2. `dotnet run --project EnvMonitor.App`
3. 点击 `Connect`。
4. 观察每秒刷新、温度曲线和继电器切换。
5. 使用 `--close-after 10` 观察断线重连。
6. 使用 `--drop 30` 或 `--delay 2000` 观察超时。
7. 修改 `Thresholds` 或扩大模拟数据范围观察报警。
8. 关闭窗口，确认客户端和日志正常结束。

## 14. 已知限制与风险

1. `FrameParser` 使用 `List<byte>`，高吞吐场景会产生额外复制。
2. `TcpClientService` 直接依赖 TCP，不支持 TLS、认证或设备身份校验。
3. 重连策略按客户端实例共享，当前没有重连次数上限和 UI 取消按钮之外的外部策略。
4. WPF ViewModel 目前没有独立测试项目，主要通过通信集成测试和构建验证。
5. 日志使用 App 层 Serilog，Communication 层没有独立的 `ILogger` 抽象。
6. LiveCharts 依赖链存在 `NU1701`，发布前应确认目标机器运行时兼容性，或升级/替换图表包。
7. 模拟器的随机波动适合演示，不代表真实传感器噪声模型。

## 15. 可发布检查清单

- [ ] `dotnet build EnvMonitor.sln --configuration Release`
- [ ] `dotnet test EnvMonitor.Tests/EnvMonitor.Tests.csproj --configuration Release`
- [ ] 在干净目录启动模拟器和 WPF App
- [ ] 修改端口配置后重启验证
- [ ] 验证日志文件生成
- [ ] 验证断线重连、超时和报警
- [ ] 录制连接、刷新、曲线、继电器、重连、报警演示
- [ ] 确认 LiveCharts 传递依赖警告的发布结论
- [ ] 再创建 `v1.0` 标签
