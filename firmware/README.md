# STM32F103C8T6 Firmware

这是 EnvMonitor 的 STM32F103C8T6 固件起始目录，使用 STM32CubeIDE/CubeMX + HAL。

## CubeMX 配置

1. MCU 选择 `STM32F103C8Tx`。
2. `PC13` 配置为 `GPIO_Output`。
3. 常见 Blue Pill 板载 LED 为低电平点亮：
   - `GPIO_PIN_RESET` = LED ON
   - `GPIO_PIN_SET` = LED OFF
4. USART1 配置为 Asynchronous：115200、8N1、无硬件流控。
5. 为 USART1 添加 `RX DMA`，方向为 `Peripheral to Memory`，数据宽度 Byte，模式选择 `Circular`，Memory Increment 开启，Peripheral Increment 关闭。
6. 在 DMA Settings 中确认 RX DMA 已链接到 USART1；重新生成代码后应存在 `MX_DMA_Init()` 和 `huart1.hdmarx`。
7. 当前联调使用 USART1：PA9 TX、PA10 RX，通过 USB-TTL 连接电脑；USB CDC 是后续可选方案。
8. 当前固件使用 `serial_dma.c` 在主循环轮询 DMA 写入位置，不再使用逐字节 `HAL_UART_Receive_IT()`。
9. 时钟、DMA、USART 和 GPIO 初始化代码由 CubeMX 生成，不要手写替换。

## 上位机协议

帧格式：

```text
| AA55 | Length 2B BE | Cmd 1B | Seq 2B BE | Payload | CRC16 2B LE |
```

LED 命令：

| Cmd              | Request Payload    | Response Payload   |
| ---------------- | ------------------ | ------------------ |
| `0x10` SetLed    | LedId 1B, State 1B | LedId 1B, State 1B |
| `0x11` GetLed    | LedId 1B           | LedId 1B, State 1B |
| `0x03` Heartbeat | empty              | empty              |

当前板载 LED 使用 `LedId = 1`。

## 需要补入 CubeIDE 工程的模块

- `protocol.c/.h`：CRC16、帧校验和命令常量
- `frame_parser.c/.h`：USB CDC/UART 接收缓存和半包拆包
- `led_service.c/.h`：PC13 GPIO 读写
- `command_dispatcher.c/.h`：把 `0x10` 和 `0x11` 映射到 LED 服务

当前仓库的第一版实现文件：

- `App/serial_dma.c/.h`：256 字节 RX 环形 DMA 缓冲区和主循环取数
- `App/protocol.c/.h`：从 DMA 取出的字节继续进入同一个协议状态机
- `App/main.c`：调用 `MX_DMA_Init()`、`SerialDma_Start()` 和 `SerialDma_Process()`

## DMA 接收流程

```text
USART1 RX -> DMA Circular Buffer -> SerialDma_Process()
                  |
                  v
               Protocol_InputByte()
                  |
                  v
               Protocol_Process()
```

DMA 缓冲区大小为 256 字节。`SerialDma_Process()` 根据 DMA 当前剩余传输计数计算写入位置，再把新增字节逐个交给协议状态机，因此协议层仍然支持半包和粘包。

注意：当前实现适合 LED 控制和低速命令通信。如果主循环长时间阻塞并且 DMA 缓冲区被完整覆盖，旧数据会丢失；后续高吞吐版本应增加生产者/消费者溢出计数或使用更大的环形缓冲区。

收到完整 `0x10` 后，固件应调用 GPIO 写函数，并返回相同 Seq 的响应帧。CRC 错误、长度错误和未知命令不得操作 GPIO。

## 联调顺序

1. 先只烧录 GPIO 闪烁程序，确认 PC13 逻辑为低电平点亮。
2. 再加入 USB CDC/UART 回环，确认上位机能看到 COM 口。
3. 加入 CRC16 和 FrameParser。
4. 用上位机第二个 `STM32 LED` Tab 连接 COM 口。
5. 点击 `Toggle PC13 LED`，确认返回帧和实际灯状态一致。

心跳，打开，关闭的三个报文分别是：
AA 55 00 09 03 00 01 2F 53
AA 55 00 0B 10 00 02 01 01 E8 7C
AA 55 00 0B 10 00 03 01 00 78 7C
