using System.Runtime.InteropServices;

namespace ReachyMini.Sdk.Internal;

internal static class GStreamerInterop
{
    private const string GStreamerLibrary = "libgstreamer-1.0.so.0";
    private const string GStreamerAppLibrary = "libgstapp-1.0.so.0";
    private const string GLibLibrary = "libglib-2.0.so.0";

    [Flags]
    internal enum GstMapFlags
    {
        Read = 1
    }

    internal enum GstState
    {
        VoidPending = 0,
        Null = 1,
        Ready = 2,
        Paused = 3,
        Playing = 4
    }

    internal enum GstStateChangeReturn
    {
        Failure = 0,
        Success = 1,
        Async = 2,
        NoPreroll = 3
    }

    [Flags]
    internal enum GstMessageType : uint
    {
        Error = 1u << 1
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GstMapInfo
    {
        public IntPtr Memory;
        public GstMapFlags Flags;
        public IntPtr Data;
        public nuint Size;
        public nuint MaxSize;
        public IntPtr UserData0;
        public IntPtr UserData1;
        public IntPtr UserData2;
        public IntPtr UserData3;
        public IntPtr Reserved0;
        public IntPtr Reserved1;
        public IntPtr Reserved2;
        public IntPtr Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public IntPtr Message;
    }

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int gst_init_check(IntPtr argc, IntPtr argv, out IntPtr error);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr gst_parse_launch(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pipelineDescription,
        out IntPtr error);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr gst_bin_get_by_name(
        IntPtr bin,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern GstStateChangeReturn gst_element_set_state(IntPtr element, GstState state);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern GstStateChangeReturn gst_element_get_state(
        IntPtr element,
        out GstState state,
        out GstState pending,
        ulong timeout);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr gst_element_get_bus(IntPtr element);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr gst_bus_timed_pop_filtered(
        IntPtr bus,
        ulong timeout,
        GstMessageType messageTypes);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gst_message_unref(IntPtr message);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr gst_sample_get_buffer(IntPtr sample);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gst_sample_unref(IntPtr sample);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int gst_buffer_map(IntPtr buffer, out GstMapInfo info, GstMapFlags flags);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gst_buffer_unmap(IntPtr buffer, ref GstMapInfo info);

    [DllImport(GStreamerLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gst_object_unref(IntPtr obj);

    [DllImport(GStreamerAppLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr gst_app_sink_try_pull_sample(IntPtr appSink, ulong timeout);

    [DllImport(GLibLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_error_free(IntPtr error);

    [DllImport(GLibLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_free(IntPtr memory);

    [DllImport(GLibLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr g_main_loop_new(IntPtr context, [MarshalAs(UnmanagedType.I1)] bool isRunning);

    [DllImport(GLibLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void g_main_loop_run(IntPtr loop);

    [DllImport(GLibLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void g_main_loop_quit(IntPtr loop);

    [DllImport(GLibLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void g_main_loop_unref(IntPtr loop);

    internal static string TakeErrorMessage(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return "<empty>";
        }

        try
        {
            var gError = Marshal.PtrToStructure<GError>(error);
            return Marshal.PtrToStringUTF8(gError.Message) ?? "<empty>";
        }
        finally
        {
            g_error_free(error);
        }
    }

    internal static string TakeUtf8StringAndFree(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUTF8(value) ?? string.Empty;
        }
        finally
        {
            g_free(value);
        }
    }
}
