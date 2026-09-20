#include "serial_dma.h"
#include "protocol.h"
#include "stm32f1xx_hal_dma.h"

static UART_HandleTypeDef* serial_uart;
static uint8_t dma_rx_buffer[SERIAL_DMA_RX_BUFFER_SIZE];
static uint16_t dma_read_index;

HAL_StatusTypeDef SerialDma_Start(UART_HandleTypeDef* huart)
{
    if (huart == 0 || huart->hdmarx == 0)
    {
        return HAL_ERROR;
    }

    serial_uart = huart;
    dma_read_index = 0U;

    return HAL_UART_Receive_DMA(
        serial_uart,
        dma_rx_buffer,
        SERIAL_DMA_RX_BUFFER_SIZE);
}

void SerialDma_Process(void)
{
    if (serial_uart == 0 || serial_uart->hdmarx == 0)
    {
        return;
    }

    uint16_t write_index = (uint16_t)(
        SERIAL_DMA_RX_BUFFER_SIZE -
        __HAL_DMA_GET_COUNTER(serial_uart->hdmarx));

    if (write_index >= SERIAL_DMA_RX_BUFFER_SIZE)
    {
        write_index = 0U;
    }

    while (dma_read_index != write_index)
    {
        Protocol_InputByte(dma_rx_buffer[dma_read_index]);
        dma_read_index++;

        if (dma_read_index >= SERIAL_DMA_RX_BUFFER_SIZE)
        {
            dma_read_index = 0U;
        }
    }
}

