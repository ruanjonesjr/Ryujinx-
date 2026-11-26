using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// Creates a Vulkan surface from Android's ANativeWindow.
    /// The Android host must keep the native window alive until the renderer is disposed.
    /// </summary>
    public static unsafe class AndroidVulkanSurface
    {
        public static string[] RequiredInstanceExtensions()
        {
            return new[]
            {
                KhrSurface.ExtensionName,
                KhrAndroidSurface.ExtensionName,
            };
        }

        public static SurfaceKHR Create(Instance instance, Vk api, IntPtr nativeWindow)
        {
            if (nativeWindow == IntPtr.Zero)
            {
                throw new ArgumentException("ANativeWindow must not be null.", nameof(nativeWindow));
            }

            if (!api.TryGetInstanceExtension(instance, out KhrAndroidSurface androidSurface))
            {
                throw new VulkanException("VK_KHR_android_surface is not available.");
            }

            // VkAndroidSurfaceCreateInfoKHR.window is ANativeWindow*.
            // Silk.NET represents that native pointer as nint*, so pass the
            // ANativeWindow handle itself (not the address of a local pointer).
            var createInfo = new AndroidSurfaceCreateInfoKHR
            {
                SType = StructureType.AndroidSurfaceCreateInfoKhr,
                Window = (nint*)nativeWindow,
            };

            androidSurface.CreateAndroidSurface(instance, &createInfo, null, out SurfaceKHR surface).ThrowOnError();
            return surface;
        }
    }
}
