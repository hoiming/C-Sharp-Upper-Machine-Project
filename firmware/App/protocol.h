#ifndef ENVMONITOR_PROTOCOL_H
#define ENVMONITOR_PROTOCOL_H

#include <stdint.h>

#define PROTOCOL_HEADER_1        0xAAU
#define PROTOCOL_HEADER_2        0x55U
#define PROTOCOL_MIN_FRAME_SIZE  9U
#define PROTOCOL_MAX_FRAME_SIZE  128U

#define CMD_HEARTBEAT            0x03U
#define CMD_SET_LED              0x10U
#define CMD_GET_LED              0x11U

#define ERROR_INVALID_COMMAND    0x01U
#define ERROR_INVALID_PAYLOAD    0x02U
#define ERROR_INVALID_LED        0x03U

void Protocol_Init(void);
void Protocol_InputByte(uint8_t byte);
void Protocol_Process(void);
uint16_t Protocol_Crc16(const uint8_t* data, uint16_t length);

#endif
