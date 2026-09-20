#include "protocol.h"
#include "gpio.h"
#include "usart.h"
#include <string.h>

#define LED_ID_PC13 1U

/* CubeMX generates huart1 in usart.c/usart.h. */
extern UART_HandleTypeDef huart1;

typedef enum
{
    RX_WAIT_HEADER_1,
    RX_WAIT_HEADER_2,
    RX_READ_LENGTH_HIGH,
    RX_READ_LENGTH_LOW,
    RX_READ_REST
} ProtocolRxState;

static ProtocolRxState rx_state;
static uint8_t rx_buffer[PROTOCOL_MAX_FRAME_SIZE];
static uint16_t rx_index;
static uint16_t rx_expected_length;

static uint8_t pending_frame[PROTOCOL_MAX_FRAME_SIZE];
static uint16_t pending_length;
static volatile uint8_t pending_frame_ready;

static uint8_t led_state;

uint16_t Protocol_Crc16(const uint8_t* data, uint16_t length)
{
    uint16_t crc = 0xFFFFU;

    for (uint16_t i = 0; i < length; i++)
    {
        crc ^= data[i];

        for (uint8_t bit = 0; bit < 8U; bit++)
        {
            if ((crc & 1U) != 0U)
            {
                crc = (uint16_t)((crc >> 1U) ^ 0xA001U);
            }
            else
            {
                crc >>= 1U;
            }
        }
    }

    return crc;
}

void Protocol_Init(void)
{
    rx_state = RX_WAIT_HEADER_1;
    rx_index = 0U;
    rx_expected_length = 0U;
    pending_length = 0U;
    pending_frame_ready = 0U;
    led_state = 0U;

    HAL_GPIO_WritePin(GPIOC, GPIO_PIN_13, GPIO_PIN_SET);
}

static void Protocol_ResetReceiver(void)
{
    rx_state = RX_WAIT_HEADER_1;
    rx_index = 0U;
    rx_expected_length = 0U;
}

static void Protocol_SaveCompleteFrame(void)
{
    if (pending_frame_ready != 0U)
    {
        return;
    }

    memcpy(pending_frame, rx_buffer, rx_expected_length);
    pending_length = rx_expected_length;
    pending_frame_ready = 1U;
}

void Protocol_InputByte(uint8_t byte)
{
    switch (rx_state)
    {
        case RX_WAIT_HEADER_1:
            if (byte == PROTOCOL_HEADER_1)
            {
                rx_buffer[0] = byte;
                rx_index = 1U;
                rx_state = RX_WAIT_HEADER_2;
            }
            break;

        case RX_WAIT_HEADER_2:
            if (byte == PROTOCOL_HEADER_2)
            {
                rx_buffer[rx_index++] = byte;
                rx_state = RX_READ_LENGTH_HIGH;
            }
            else if (byte == PROTOCOL_HEADER_1)
            {
                rx_buffer[0] = byte;
                rx_index = 1U;
            }
            else
            {
                Protocol_ResetReceiver();
            }
            break;

        case RX_READ_LENGTH_HIGH:
            rx_buffer[rx_index++] = byte;
            rx_state = RX_READ_LENGTH_LOW;
            break;

        case RX_READ_LENGTH_LOW:
            rx_buffer[rx_index++] = byte;
            rx_expected_length = (uint16_t)(((uint16_t)rx_buffer[2] << 8U) | rx_buffer[3]);

            if (rx_expected_length < PROTOCOL_MIN_FRAME_SIZE ||
                rx_expected_length > PROTOCOL_MAX_FRAME_SIZE)
            {
                Protocol_ResetReceiver();
            }
            else
            {
                rx_state = RX_READ_REST;
            }
            break;

        case RX_READ_REST:
            if (rx_index >= PROTOCOL_MAX_FRAME_SIZE)
            {
                Protocol_ResetReceiver();
                break;
            }

            rx_buffer[rx_index++] = byte;
            if (rx_index == rx_expected_length)
            {
                Protocol_SaveCompleteFrame();
                Protocol_ResetReceiver();
            }
            break;

        default:
            Protocol_ResetReceiver();
            break;
    }
}

static void Protocol_BuildResponse(
    uint8_t command,
    uint16_t sequence,
    const uint8_t* payload,
    uint8_t payload_length)
{
    uint8_t response[PROTOCOL_MAX_FRAME_SIZE];
    uint16_t length = (uint16_t)(PROTOCOL_MIN_FRAME_SIZE + payload_length);

    response[0] = PROTOCOL_HEADER_1;
    response[1] = PROTOCOL_HEADER_2;
    response[2] = (uint8_t)(length >> 8U);
    response[3] = (uint8_t)length;
    response[4] = command;
    response[5] = (uint8_t)(sequence >> 8U);
    response[6] = (uint8_t)sequence;

    if (payload_length > 0U)
    {
        memcpy(&response[7], payload, payload_length);
    }

    uint16_t crc = Protocol_Crc16(response, (uint16_t)(length - 2U));
    response[length - 2U] = (uint8_t)crc;
    response[length - 1U] = (uint8_t)(crc >> 8U);

    HAL_UART_Transmit(&huart1, response, length, 100U);
}

static void Protocol_HandleFrame(const uint8_t* frame, uint16_t length)
{
    if (length < PROTOCOL_MIN_FRAME_SIZE)
    {
        return;
    }

    uint16_t received_crc = (uint16_t)(frame[length - 2U] |
                                       ((uint16_t)frame[length - 1U] << 8U));
    uint16_t calculated_crc = Protocol_Crc16(frame, (uint16_t)(length - 2U));
    if (received_crc != calculated_crc)
    {
        return;
    }

    uint8_t command = frame[4];
    uint16_t sequence = (uint16_t)(((uint16_t)frame[5] << 8U) | frame[6]);
    uint8_t payload_length = (uint8_t)(length - PROTOCOL_MIN_FRAME_SIZE);
    const uint8_t* payload = &frame[7];

    if (command == CMD_HEARTBEAT)
    {
        if (payload_length == 0U)
        {
            Protocol_BuildResponse(CMD_HEARTBEAT, sequence, 0, 0U);
        }
        else
        {
            uint8_t error = ERROR_INVALID_PAYLOAD;
            Protocol_BuildResponse((uint8_t)(CMD_HEARTBEAT | 0x80U), sequence, &error, 1U);
        }
        return;
    }

    if (command == CMD_SET_LED)
    {
        if (payload_length != 2U)
        {
            uint8_t error = ERROR_INVALID_PAYLOAD;
            Protocol_BuildResponse((uint8_t)(command | 0x80U), sequence, &error, 1U);
            return;
        }

        if (payload[0] != LED_ID_PC13)
        {
            uint8_t error = ERROR_INVALID_LED;
            Protocol_BuildResponse((uint8_t)(command | 0x80U), sequence, &error, 1U);
            return;
        }

        if (payload[1] > 1U)
        {
            uint8_t error = ERROR_INVALID_PAYLOAD;
            Protocol_BuildResponse((uint8_t)(command | 0x80U), sequence, &error, 1U);
            return;
        }

        led_state = payload[1];
        HAL_GPIO_WritePin(
            GPIOC,
            GPIO_PIN_13,
            led_state != 0U ? GPIO_PIN_RESET : GPIO_PIN_SET);
        Protocol_BuildResponse(command, sequence, payload, 2U);
        return;
    }

    if (command == CMD_GET_LED)
    {
        if (payload_length != 1U || payload[0] != LED_ID_PC13)
        {
            uint8_t error = payload_length == 1U ? ERROR_INVALID_LED : ERROR_INVALID_PAYLOAD;
            Protocol_BuildResponse((uint8_t)(command | 0x80U), sequence, &error, 1U);
            return;
        }

        uint8_t response_payload[2] = { LED_ID_PC13, led_state };
        Protocol_BuildResponse(command, sequence, response_payload, 2U);
        return;
    }

    uint8_t error = ERROR_INVALID_COMMAND;
    Protocol_BuildResponse((uint8_t)(command | 0x80U), sequence, &error, 1U);
}

void Protocol_Process(void)
{
    if (pending_frame_ready == 0U)
    {
        return;
    }

    uint8_t frame[PROTOCOL_MAX_FRAME_SIZE];
    uint16_t length = pending_length;
    memcpy(frame, pending_frame, length);
    pending_frame_ready = 0U;

    Protocol_HandleFrame(frame, length);
}
