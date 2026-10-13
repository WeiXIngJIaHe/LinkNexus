#pragma once

#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"

namespace LinkNexus {

/**
 * @brief FreeRTOS 互斥锁 RAII 包装器
 * 确保 ESP32-S3 双核（Core 0 与 Core 1）同时访问外设/状态机时避免指针异常与数据竞争
 */
class Mutex {
public:
    Mutex() {
        m_handle = xSemaphoreCreateMutex();
    }

    ~Mutex() {
        if (m_handle != nullptr) {
            vSemaphoreDelete(m_handle);
            m_handle = nullptr;
        }
    }

    // 禁止拷贝与赋值
    Mutex(const Mutex&) = delete;
    Mutex& operator=(const Mutex&) = delete;

    /**
     * @brief 获取互斥锁
     * @param timeout_ticks 等待超时 Tick
     * @return true 成功获取，false 超时失败
     */
    bool Lock(TickType_t timeout_ticks = portMAX_DELAY) {
        if (m_handle == nullptr) return false;
        return xSemaphoreTake(m_handle, timeout_ticks) == pdTRUE;
    }

    /**
     * @brief 释放互斥锁
     */
    void Unlock() {
        if (m_handle != nullptr) {
            xSemaphoreGive(m_handle);
        }
    }

    SemaphoreHandle_t GetHandle() const {
        return m_handle;
    }

private:
    SemaphoreHandle_t m_handle{nullptr};
};

/**
 * @brief 自动作用域锁守卫 (RAII LockGuard)
 */
class LockGuard {
public:
    explicit LockGuard(Mutex& mutex, TickType_t timeout_ticks = portMAX_DELAY)
        : m_mutex(mutex), m_locked(mutex.Lock(timeout_ticks)) {}

    ~LockGuard() {
        if (m_locked) {
            m_mutex.Unlock();
        }
    }

    // 禁止拷贝
    LockGuard(const LockGuard&) = delete;
    LockGuard& operator=(const LockGuard&) = delete;

    bool IsLocked() const {
        return m_locked;
    }

private:
    Mutex& m_mutex;
    bool m_locked{false};
};

} // namespace LinkNexus

