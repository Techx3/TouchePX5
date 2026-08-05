// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// A native child surface owned by a host UI. The presenter consumes this
/// directly, avoiding a second top-level GLFW window when the GUI is active.
/// </summary>
public enum VulkanHostSurfaceKind
{
    Win32,
    Xlib,
    Metal,
}

/// <summary>
/// Platform-native handles required to create a Vulkan presentation surface.
/// The GUI owns their lifetime and updates the physical pixel size on resize.
/// </summary>
public sealed class VulkanHostSurface : IDisposable
{
    private const int MaximumPixelDimension = 32768;
    private long _packedPixelSize;
    private int _resizeGeneration;
    private int _disposed;
    private readonly bool _ownsDisplay;
    private readonly bool _pollNativeSize;
    private long _nextNativeSizePoll;

    public VulkanHostSurface(
        VulkanHostSurfaceKind kind,
        nint windowHandle,
        nint displayHandle = 0,
        nint instanceHandle = 0,
        nint metalLayerHandle = 0,
        bool ownsDisplay = false,
        bool pollNativeSize = false)
    {
        Kind = kind;
        WindowHandle = windowHandle;
        DisplayHandle = displayHandle;
        InstanceHandle = instanceHandle;
        MetalLayerHandle = metalLayerHandle;
        _ownsDisplay = ownsDisplay;
        _pollNativeSize = pollNativeSize;
    }

    public VulkanHostSurfaceKind Kind { get; }

    public nint WindowHandle { get; }

    /// <summary>X11 Display* when <see cref="Kind"/> is <see cref="VulkanHostSurfaceKind.Xlib"/>.</summary>
    public nint DisplayHandle { get; }

    /// <summary>Win32 HINSTANCE when <see cref="Kind"/> is <see cref="VulkanHostSurfaceKind.Win32"/>.</summary>
    public nint InstanceHandle { get; }

    /// <summary>CAMetalLayer* when <see cref="Kind"/> is <see cref="VulkanHostSurfaceKind.Metal"/>.</summary>
    public nint MetalLayerHandle { get; }

    public int PixelWidth => UnpackWidth(Volatile.Read(ref _packedPixelSize));

    public int PixelHeight => UnpackHeight(Volatile.Read(ref _packedPixelSize));

    internal int ResizeGeneration => Volatile.Read(ref _resizeGeneration);

    public void UpdatePixelSize(int width, int height)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        width = Math.Clamp(width, 1, MaximumPixelDimension);
        height = Math.Clamp(height, 1, MaximumPixelDimension);
        var packed = PackPixelSize(width, height);
        if (Interlocked.Exchange(ref _packedPixelSize, packed) == packed)
        {
            return;
        }

        Interlocked.Increment(ref _resizeGeneration);
    }

    public (int Width, int Height) GetPixelSize()
    {
        var packed = Volatile.Read(ref _packedPixelSize);
        return (UnpackWidth(packed), UnpackHeight(packed));
    }

    /// <summary>
    /// The child emulator cannot receive Avalonia resize notifications. Poll
    /// the native host at a bounded rate so embedded child swapchains still
    /// follow normal resize and F11 transitions.
    /// </summary>
    internal void RefreshChildProcessPixelSize()
    {
        if (!_pollNativeSize || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var due = Volatile.Read(ref _nextNativeSizePoll);
        if (now < due || Interlocked.CompareExchange(
                ref _nextNativeSizePoll,
                now + (System.Diagnostics.Stopwatch.Frequency / 8),
                due) != due)
        {
            return;
        }

        if (Kind == VulkanHostSurfaceKind.Win32 && GetClientRect(WindowHandle, out var rect))
        {
            UpdatePixelSize(rect.Right - rect.Left, rect.Bottom - rect.Top);
            return;
        }

        if (Kind == VulkanHostSurfaceKind.Xlib && DisplayHandle != 0 &&
            XGetGeometry(
                DisplayHandle,
                WindowHandle,
                out _,
                out _,
                out _,
                out var width,
                out var height,
                out _,
                out _) != 0)
        {
            UpdatePixelSize(unchecked((int)width), unchecked((int)height));
        }
    }

    /// <summary>
    /// Serializes a native child handle for a separately hosted emulator
    /// process. Metal object pointers are process-local, so macOS falls back
    /// to a standalone child window until an IPC Metal host is implemented.
    /// </summary>
    public bool TryGetChildProcessDescriptor(out string descriptor)
    {
        descriptor = string.Empty;
        if (Kind == VulkanHostSurfaceKind.Metal || WindowHandle == 0)
        {
            return false;
        }

        var kind = Kind == VulkanHostSurfaceKind.Win32 ? "win32" : "xlib";
        var (width, height) = GetPixelSize();
        descriptor = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{kind}:{unchecked((ulong)WindowHandle):X}:{Math.Max(width, 1)}:{Math.Max(height, 1)}:0");
        return true;
    }

    /// <summary>
    /// Reconstructs a surface in the isolated emulator process. X11 clients
    /// must open their own Display connection; Display* values cannot cross a
    /// process boundary.
    /// </summary>
    public static bool TryCreateChildProcessSurface(
        string descriptor,
        out VulkanHostSurface? surface,
        out string? error)
    {
        surface = null;
        error = null;
        var parts = descriptor.Split(':');
        if (parts.Length != 5 ||
            !ulong.TryParse(parts[1], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var window) ||
            !int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var height) ||
            !ulong.TryParse(parts[4], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var nativeDisplay))
        {
            error = "invalid host-surface descriptor";
            return false;
        }

        if (window == 0 || width <= 0 || height <= 0 ||
            width > MaximumPixelDimension || height > MaximumPixelDimension)
        {
            error = "host-surface descriptor has an invalid size or handle";
            return false;
        }

        if (string.Equals(parts[0], "win32", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
            {
                error = "a Win32 host surface is only valid on Windows";
                return false;
            }

            surface = new VulkanHostSurface(
                VulkanHostSurfaceKind.Win32,
                unchecked((nint)window),
                pollNativeSize: true);
        }
        else if (string.Equals(parts[0], "xlib", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsLinux())
            {
                error = "an Xlib host surface is only valid on Linux";
                return false;
            }

            var display = XOpenDisplay(0);
            if (display == 0)
            {
                error = "could not open an X11 display for the host surface";
                return false;
            }

            surface = new VulkanHostSurface(
                VulkanHostSurfaceKind.Xlib,
                unchecked((nint)window),
                display,
                ownsDisplay: true,
                pollNativeSize: true);
        }
        else
        {
            error = $"unsupported host-surface kind '{parts[0]}'";
            return false;
        }

        surface.UpdatePixelSize(width, height);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsDisplay && DisplayHandle != 0 && OperatingSystem.IsLinux())
        {
            _ = XCloseDisplay(DisplayHandle);
        }
    }

    private static long PackPixelSize(int width, int height) =>
        unchecked(((long)(uint)width << 32) | (uint)height);

    private static int UnpackWidth(long packed) => unchecked((int)(uint)(packed >> 32));

    private static int UnpackHeight(long packed) => unchecked((int)(uint)packed);

    [System.Runtime.InteropServices.DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern nint XOpenDisplay(nint displayName);

    [System.Runtime.InteropServices.DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static extern int XCloseDisplay(nint display);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect rect);

    [System.Runtime.InteropServices.DllImport("libX11.so.6", EntryPoint = "XGetGeometry")]
    private static extern int XGetGeometry(
        nint display,
        nint drawable,
        out nint root,
        out int x,
        out int y,
        out uint width,
        out uint height,
        out uint borderWidth,
        out uint depth);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

/// <summary>
/// Small public bridge between a desktop UI and the internal Vulkan
/// presenter. Launchers can host a surface without depending on renderer
/// submission internals.
/// </summary>
public static class VulkanVideoHost
{
    /// <summary>
    /// Raised after the first successful Vulkan present to an embedded host
    /// surface. UI hosts use this to retire their launch affordance only once
    /// a real frame can be seen.
    /// </summary>
    public static event Action<VulkanHostSurface>? FirstFramePresented
    {
        add => VulkanVideoPresenter.FirstHostFramePresented += value;
        remove => VulkanVideoPresenter.FirstHostFramePresented -= value;
    }

    public static bool TryAttachSurface(VulkanHostSurface surface) =>
        VulkanVideoPresenter.TryAttachHostSurface(surface);

    public static bool DetachSurface(VulkanHostSurface surface, TimeSpan timeout) =>
        VulkanVideoPresenter.DetachHostSurface(surface, timeout);

    public static void RequestClose() => VulkanVideoPresenter.RequestClose();

    public static bool IsEmbedded => VulkanVideoPresenter.UsesHostSurface;
}
