#include <jni.h>
#include <android/native_window.h>
#include <android/native_window_jni.h>
#include <cstdint>

extern "C" JNIEXPORT jlong JNICALL
Java_com_ryujinx_android_NativeWindowBridge_fromSurface(
    JNIEnv* env, jclass, jobject surface) {
    if (surface == nullptr) {
        return 0;
    }

    ANativeWindow* window = ANativeWindow_fromSurface(env, surface);
    return static_cast<jlong>(reinterpret_cast<intptr_t>(window));
}

extern "C" JNIEXPORT void JNICALL
Java_com_ryujinx_android_NativeWindowBridge_release(
    JNIEnv*, jclass, jlong handle) {
    auto* window = reinterpret_cast<ANativeWindow*>(static_cast<intptr_t>(handle));
    if (window != nullptr) {
        ANativeWindow_release(window);
    }
}
