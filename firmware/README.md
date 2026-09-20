# STM32F103C8T6 Firmware

这是 EnvMonitor 的 STM32F103C8T6 固件起始目录，使用 STM32CubeIDE/CubeMX + HAL。

## CubeMX 配置

1. MCU 选择 `STM32F103C8Tx`。
2. `PC13` 配置为 `GPIO_Output`。
3. 常见 Blue Pill 板载 LED 为低电平点亮：
   - `GPIO_PIN_RESET` = LED ON
   - `GPIO_PIN_SET` = LED OFF
4. 第一阶段建议启用 USB Device FS + CDC。
5. 如果当前板卡 USB CDC 不稳定，可改用 USART1：PA9 TX、PA10 RX，通过 USB-TTL 连接电脑。
6. 时钟、USB 和 GPIO 初始化代码由 CubeMX 生成，不要手写替换。

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

收到完整 `0x10` 后，固件应调用 GPIO 写函数，并返回相同 Seq 的响应帧。CRC 错误、长度错误和未知命令不得操作 GPIO。

## 联调顺序

1. 先只烧录 GPIO 闪烁程序，确认 PC13 逻辑为低电平点亮。
2. 再加入 USB CDC/UART 回环，确认上位机能看到 COM 口。
3. 加入 CRC16 和 FrameParser。
4. 用上位机第二个 `STM32 LED` Tab 连接 COM 口。
5. 点击 `Toggle PC13 LED`，确认返回帧和实际灯状态一致。
