using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// Owns an initialized Ryujinx Vulkan renderer bound to an Android ANativeWindow.
    /// The Android host must retain the ANativeWindow until this session is disposed.
    /// </summary>
    public sealed class AndroidVulkanRendererSession : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private bool _disposed;

        public IRenderer Renderer => _renderer;

        public IWindow Window => _renderer.Window;

        private AndroidVulkanRendererSession(VulkanRenderer renderer)
        {
            _renderer = renderer;
        }

        /// <summary>
        /// Creates and initializes the original Ryujinx Vulkan renderer against
        /// Android's VK_KHR_android_surface and VK_KHR_surface extensions.
        /// </summary>
        public static AndroidVulkanRendererSession Create(
            IntPtr nativeWindow,
            string preferredGpuId = "",
            GraphicsDebugLevel logLevel = GraphicsDebugLevel.None)
        {
            if (nativeWindow == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "A valid Android ANativeWindow is required.",
                    nameof(nativeWindow));
            }

            VulkanRenderer renderer = new(Vk.GetApi(), nativeWindow, preferredGpuId);

            try
            {
                renderer.Initialize(logLevel);
                return new AndroidVulkanRendererSession(renderer);
            }
            catch
            {
                renderer.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Notifies the Vulkan window that Android's surface dimensions changed.
        /// The swapchain is recreated on the next present.
        /// </summary>
        public void OnSurfaceChanged(int width, int height)
        {
            ThrowIfDisposed();

            if (width <= 0 || height <= 0)
            {
                return;
            }

            _renderer.Window.SetSize(width, height);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renderer.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AndroidVulkanRendererSession));
            }
        }
    }
}
