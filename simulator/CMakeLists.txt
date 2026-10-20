cmake_minimum_required(VERSION 3.12)
project(lvgl_simulator C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

# 包含核心 UI 头文件与源码
include_directories(
    ${CMAKE_CURRENT_SOURCE_DIR}/../include
    ${CMAKE_CURRENT_SOURCE_DIR}
)

set(SOURCES
    main.c
    ../src/ui_main.cpp
    ../src/cap1208.c
    ../src/power_limiter.c
    ../src/ina236_telemetry.c
)

add_executable(lvgl_sim ${SOURCES})

