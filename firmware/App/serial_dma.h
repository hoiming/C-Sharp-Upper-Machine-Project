#ifndef ENVMONITOR_SERIAL_DMA_H
#define ENVMONITOR_SERIAL_DMA_H

#include "main.h"
#include <stdint.h>

#define SERIAL_DMA_RX_BUFFER_SIZE 256U

HAL_StatusTypeDef SerialDma_Start(UART_HandleTypeDef* huart);
void SerialDma_Process(void);

#endif
