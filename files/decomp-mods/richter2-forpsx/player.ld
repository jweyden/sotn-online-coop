EXTERN(RIC_player);
SECTIONS {
    . = 0x8013C000;
    .data : {
        KEEP(*(.header.0))
        KEEP(*(.header.1))
        *(.data*)
    }
    .rodata : {
        *(.rodata*)
    }
    .text : {
        *(.text*)
    }
    end = .;
}
