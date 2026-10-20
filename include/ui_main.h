#pragma once

#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * @brief LinkNexus 1.9 寸 (320x170) ST7789 屏幕核心 UI 框架
 * 严格按照硬件 DESIGN 规格构建：
 * 1. 顶部常驻菜单栏：展示系统标识、当前功能页指示与 Wi-Fi 状态（固定不随页面改变）
 * 2. 中部功能区：支持 CAP1208 电容滑条或鼠标滑动在三大功能间切换
 *    - 功能页 0: 6 通道限流芯片开关控制 (高低电平控制)
 *    - 功能页 1: 三排遥测 (总路 0x40、XDS110 0x41、DAP 0x42) 与实时动态曲线图
 *    - 功能页 2: Wi-Fi 图形化 GUI (信道扫描、连接 AP、从 AP 切换，CLI 优先兼容)
 * 3. 底部常驻进度栏：展示系统全局就绪/操作进度与状态
 */

#define UI_SCREEN_WIDTH        320
#define UI_SCREEN_HEIGHT       170
#define UI_TOP_BAR_HEIGHT      26
#define UI_BOTTOM_BAR_HEIGHT   22
#define UI_MIDDLE_ZONE_HEIGHT  (UI_SCREEN_HEIGHT - UI_TOP_BAR_HEIGHT - UI_BOTTOM_BAR_HEIGHT) // 122px

typedef enum {
    UI_PAGE_POWER_SWITCH = 0, // 6 路限流芯片开关控制
    UI_PAGE_TELEMETRY    = 1, // 三排遥测数据与动态波形曲线
    UI_PAGE_WIFI_GUI     = 2, // Wi-Fi 图形化配置视窗
    UI_PAGE_COUNT        = 3
} ui_page_t;

/**
 * @brief 初始化 UI 模型与布局
 */
void ui_main_init(void);

/**
 * @brief 周期性刷新渲染循环 (在 Core 0 渲染任务中执行)
 */
void ui_main_process(uint32_t delta_ms);

/**
 * @brief 切换中部功能区显示页面 (支持 CAP1208 滑条驱动或鼠标手势调用)
 * @param page 目标页面 0~2
 */
void ui_switch_page(ui_page_t page);

/**
 * @brief 获取当前中部展示的页面
 */
ui_page_t ui_get_current_page(void);

/**
 * @brief 响应滑条向左滑动 (切换至上一页)
 */
void ui_slide_previous(void);

/**
 * @brief 响应滑条向右滑动 (切换至下一页)
 */
void ui_slide_next(void);

/**
 * @brief 更新顶部 Wi-Fi 状态与底部进度状态文案
 */
void ui_update_status_bar(const char *wifi_mode, const char *status_text, uint8_t progress_pct);

/**
 * @brief 更新 6 通道开关状态在 UI 上的呈现
 */
void ui_update_power_switch_state(uint8_t channel_mask);

/**
 * @brief 更新三排遥测数值与曲线波形采样点
 */
void ui_update_telemetry_data(float bus_v, float bus_ma, float bus_w,
                              float xds_v, float xds_ma, float xds_w,
                              float dap_v, float dap_ma, float dap_w);

/**
 * @brief 更新 Wi-Fi GUI 扫描结果列表或连接状态
 */
void ui_update_wifi_gui_info(const char *ssid, const char *ip_str, const char *scan_summary);

#ifdef __cplusplus
}
#endif

