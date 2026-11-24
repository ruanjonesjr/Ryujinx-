package com.ryujinx.android;

import android.view.Surface;

/**
 * Converts an Android Surface into a retained NDK ANativeWindow.
 * The caller must pair every successful fromSurface call with release.
 */
public final class NativeWindowBridge {
    static {
        System.loadLibrary("ryujinx_android");
    }

    private NativeWindowBridge() {}

    public static native long fromSurface(Surface surface);
    public static native void release(long nativeWindow);
}
