FILE(GLOB_RECURSE app_sources ${CMAKE_SOURCE_DIR}/src/*.*)

idf_component_register(SRCS ${app_sources}
                       INCLUDE_DIRS "${CMAKE_SOURCE_DIR}/include" "."
                       REQUIRES nvs_flash esp_wifi esp_netif esp_event lwip driver)
