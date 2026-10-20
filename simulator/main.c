#include <stdio.h>
#include "ui_main.h"
#include "cap1208.h"
#include "power_limiter.h"
#include "ina236_telemetry.h"

int main(int argc, char **argv)
{
    (void)argc;
    (void)argv;

    printf("==========================================================\n");
    printf("  LinkNexus LVGL 1.9-inch (320x170) ST7789 Simulator\n");
    printf("==========================================================\n");

    power_limiter_init();
    ina236_telemetry_init(0);
    cap1208_init(0, CAP1208_I2C_ADDR_DEFAULT);
    ui_main_init();

    printf("UI Simulator initialized.\n");
    printf("Page 0: 6-Channel Limiter Switch\n");
    printf("Page 1: Telemetry (Bus, XDS, DAP) & Live Chart\n");
    printf("Page 2: Wi-Fi Graphical GUI (AP/STA)\n");

    // 模拟测试切页
    ui_slide_next();
    printf("Switched to page %d\n", (int)ui_get_current_page());
    ui_slide_next();
    printf("Switched to page %d\n", (int)ui_get_current_page());

    return 0;
}

