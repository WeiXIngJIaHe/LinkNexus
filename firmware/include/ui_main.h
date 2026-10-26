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
 *    - 功能页 0: 6 通道限流芯片开关控制 (高低电平控制，支持点击卡片展开/收纳通道抽屉与一键切换)
 *    - 功能页 1: 三排遥测 (总路 0x40、XDS110 0x41、DAP 0x42) 与实时动态曲线图
 *    - 功能页 2: Wi-Fi 图形化 GUI (信道扫描、连接 AP、从 AP 切换，CLI 优先兼容)
 * 3. 底部常驻进度栏与上下拉抽屉把手：展示系统全局就绪/操作进度，支持上下拉按钮与卡片点击联动收纳/拉出详情抽屉
 */

#define UI_SCREEN_WIDTH        320
#define UI_SCREEN_HEIGHT       170
#define UI_TOP_BAR_HEIGHT      26
#define UI_BOTTOM_BAR_HEIGHT   22
#define UI_MIDDLE_ZONE_HEIGHT  (UI_SCREEN_HEIGHT - UI_TOP_BAR_HEIGHT - UI_BOTTOM_BAR_HEIGHT) // 122px

/**
 * @brief 工业级柔化深色模式与高对比度文字色板 (RGB888, WCAG AAA >= 7.5:1)
 * 摒弃死黑压抑背景，采用经过调淡的工业岩石灰蓝基底与高亮度纯白/银白文字
 */
#define UI_COLOR_DARK_BG_MAIN       0x1E2430U  // 调淡后的主背景色 (Slate Charcoal)
#define UI_COLOR_DARK_BG_LCD        0x181E29U  // 1.9" 视口柔化基底色
#define UI_COLOR_DARK_SURFACE_CARD  0x252E3EU  // 工业卡片表层色
#define UI_COLOR_DARK_SURFACE_HOVER 0x2E394DU  // 卡片悬停/选中高亮层
#define UI_COLOR_DARK_BORDER        0x3D4B63U  // 高辨识度结构分割边框
#define UI_COLOR_TEXT_PRIMARY       0xFFFFFFU  // 纯白主文字 (最高对比度)
#define UI_COLOR_TEXT_SECONDARY     0xF1F5F9U  // 强化副文字 (亮银白)
#define UI_COLOR_TEXT_MUTED         0xCBD5E1U  // 工业标签与单位辅助字 (高可读性浅灰蓝)
#define UI_COLOR_ACCENT_CYAN        0x38BDF8U  // 工业遥测青
#define UI_COLOR_ACCENT_GREEN       0x10B981U  // 导通/在线翡翠绿
#define UI_COLOR_ACCENT_AMBER       0xF59E0BU  // 告警琥珀黄

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
 * @brief 抽屉式布局控制：获取当前抽屉是否处于收纳状态
 */
bool ui_is_drawer_collapsed(void);

/**
 * @brief 抽屉式布局控制：设置抽屉收纳或拉出状态
 * @param collapsed true=收纳抽屉，false=拉出抽屉
 */
void ui_set_drawer_collapsed(bool collapsed);

/**
 * @brief 抽屉式布局控制：响应上下拉按钮点击，切换抽屉收纳/拉出状态
 * @return 切换后的状态 (true=收纳, false=拉出)
 */
bool ui_toggle_drawer(void);

/**
 * @brief 响应卡片点击交互：联动卡片选中、抽屉拉出/收纳与状态切换
 * @param card_index 卡片索引 (0~5)
 */
void ui_on_card_click(uint8_t card_index);

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
