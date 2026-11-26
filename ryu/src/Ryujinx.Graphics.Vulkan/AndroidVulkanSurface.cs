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

            var createInfo = new AndroidSurfaceCreateInfoKHR
            {
                SType = StructureType.AndroidSurfaceCreateInfoKhr,
                // VkAndroidSurfaceCreateInfoKHR.window is ANativeWindow*, but Silk.NET
                // represents it as nint*. The field must point to a local nint containing
                // the ANativeWindow address; casting the address itself to nint* makes
                // Vulkan dereference ANativeWindow memory as though it were a pointer slot.
                nint window = nativeWindow;
                Window = &window,
            };

            androidSurface.CreateAndroidSurface(instance, &createInfo, null, out SurfaceKHR surface).ThrowOnError();
            return surface;
        }
    }
}
