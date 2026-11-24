package com.ryujinx.android;

import android.content.Context;
import android.util.AttributeSet;
import android.view.SurfaceHolder;
import android.view.SurfaceView;

/**
 * Owns one retained ANativeWindow reference for the lifetime of an Android Surface.
 *
 * Listener callbacks run on the Android UI thread. The listener must stop and
 * dispose every renderer using the handle before onNativeWindowDestroyed returns.
 * A handle is valid only between the created and destroyed callbacks.
 */
public final class RyujinxSurfaceView extends SurfaceView
        implements SurfaceHolder.Callback {
    public interface Listener {
        void onNativeWindowCreated(long nativeWindow, int width, int height);
        void onNativeWindowChanged(long nativeWindow, int width, int height);
        void onNativeWindowDestroyed(long nativeWindow);
    }

    private Listener listener;
    private long nativeWindow;

    public RyujinxSurfaceView(Context context) {
        super(context);
        initialize();
    }

    public RyujinxSurfaceView(Context context, AttributeSet attrs) {
        super(context, attrs);
        initialize();
    }

    public RyujinxSurfaceView(
            Context context, AttributeSet attrs, int defStyleAttr) {
        super(context, attrs, defStyleAttr);
        initialize();
    }

    private void initialize() {
        getHolder().addCallback(this);
        setFocusable(true);
        setFocusableInTouchMode(true);
    }

    /**
     * Replacing a listener first gives the previous listener a synchronous
     * chance to dispose its renderer before the new listener receives the handle.
     */
    public void setListener(Listener newListener) {
        if (listener == newListener) {
            return;
        }

        if (nativeWindow != 0 && listener != null) {
            listener.onNativeWindowDestroyed(nativeWindow);
        }

        listener = newListener;

        if (nativeWindow != 0 && listener != null) {
            listener.onNativeWindowCreated(
                    nativeWindow, getWidth(), getHeight());
        }
    }

    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        releaseWindow();
        nativeWindow = NativeWindowBridge.fromSurface(holder.getSurface());

        if (nativeWindow != 0 && listener != null) {
            listener.onNativeWindowCreated(
                    nativeWindow, getWidth(), getHeight());
        }
    }

    @Override
    public void surfaceChanged(
            SurfaceHolder holder, int format, int width, int height) {
        if (nativeWindow != 0 && listener != null) {
            listener.onNativeWindowChanged(nativeWindow, width, height);
        }
    }

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        releaseWindow();
    }

    private void releaseWindow() {
        long oldWindow = nativeWindow;
        nativeWindow = 0;

        if (oldWindow == 0) {
            return;
        }

        try {
            if (listener != null) {
                listener.onNativeWindowDestroyed(oldWindow);
            }
        } finally {
            NativeWindowBridge.release(oldWindow);
        }
    }
}
