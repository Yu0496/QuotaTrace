using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace UsageTray.Services;

public static class MemoryOptimizer
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    public static void TrimMemory()
    {
        try
        {
            // 1. 触发大对象堆（LOH）单次压缩与强制垃圾回收
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // 2. 在 Windows 平台上将闲置物理工作集归还给操作系统
            if (OperatingSystem.IsWindows())
            {
                using var currentProcess = Process.GetCurrentProcess();
                SetProcessWorkingSetSize(currentProcess.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
        }
        catch
        {
            // 最佳努力内存修剪，忽略系统级句柄异常
        }
    }
}
