# The Saturn's long-coat Richter as a PS1 player overlay (Richter's slot):
# PS1 Richter's decompiled C unchanged, his art converted from your own Saturn
# disc by tools/richter2-convert.py (src/gen/richter2_gfx.h).
ROOT_DIR = ../..
SRC_DIR = src
VERSION   ?= us
BUILD_DIR = build/$(VERSION)
TARGET = $(BUILD_DIR)/richter2.elf
MAP = $(BUILD_DIR)/richter2.map
OVERLAY = $(BUILD_DIR)/RICHTER2.BIN

CC = mipsel-linux-gnu-gcc
CFLAGS = -mno-abicalls -G0 -fno-pic -g -O2 -I$(ROOT_DIR)/include -I$(ROOT_DIR)/include/psxsdk -I$(SRC_DIR) -msoft-float -march=3000
CFLAGS += -funsigned-char -fpeephole -ffunction-cse -fpcc-struct-return -fcommon -fverbose-asm -fno-stack-protector
CFLAGS += -DRICHTER2=1 -D_internal_version_us
LDFLAGS = -nostdlib -Wl,-Map=$(MAP) -Wl,--build-id=none -T player.ld -T $(ROOT_DIR)/config/undefined_syms.$(VERSION).txt -T $(BUILD_DIR)/symbols.txt

SRCS = $(wildcard $(SRC_DIR)/*.c)
# the header object goes first: PlayerOvl at 0x8013C000, sprite table at 0x8013C020
OBJS = $(BUILD_DIR)/pl_header.o $(patsubst $(SRC_DIR)/%.c, $(BUILD_DIR)/%.o, $(SRCS))

.PHONY: all build clean
all: build
build: $(OVERLAY)

$(BUILD_DIR):
	mkdir -p $@
$(BUILD_DIR)/%.o: $(SRC_DIR)/%.c $(wildcard $(SRC_DIR)/*.h) $(wildcard $(SRC_DIR)/gen/*.h) | $(BUILD_DIR)
	$(CC) $(CFLAGS) -c $< -o $@
$(BUILD_DIR)/pl_header.o: header/pl_header.c $(wildcard $(SRC_DIR)/*.h) $(wildcard $(SRC_DIR)/gen/*.h) | $(BUILD_DIR)
	$(CC) $(CFLAGS) -fno-toplevel-reorder -c $< -o $@
$(TARGET): $(OBJS) | $(BUILD_DIR)
	sed 's;//.*;;' $(ROOT_DIR)/config/symbols.$(VERSION).txt > $(BUILD_DIR)/symbols.txt
	echo 'g_SfxData = 0x800BF554;' >> $(BUILD_DIR)/symbols.txt
	$(CC) $(LDFLAGS) -o $@ $^
$(OVERLAY): $(TARGET)
	mipsel-linux-gnu-objcopy -O binary $< $@
clean:
	rm -rf $(BUILD_DIR)
