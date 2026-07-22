using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HeavyJobQueue.App;

internal sealed class SystemPerformanceSampler
{
    private const int SystemProcessorPerformanceInformation = 8;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private ProcessorTimes[]? _previousProcessorTimes;

    public SystemPerformanceSample Sample()
    {
        var processorTimes = ReadProcessorTimes();
        var utilization = new double[processorTimes.Length];

        if (_previousProcessorTimes?.Length == processorTimes.Length)
        {
            for (var index = 0; index < processorTimes.Length; index++)
            {
                var idle = processorTimes[index].IdleTime -
                    _previousProcessorTimes[index].IdleTime;
                var total = processorTimes[index].KernelTime -
                    _previousProcessorTimes[index].KernelTime +
                    processorTimes[index].UserTime -
                    _previousProcessorTimes[index].UserTime;
                utilization[index] = total <= 0
                    ? 0
                    : Math.Clamp(1d - ((double)idle / total), 0d, 1d);
            }
        }

        _previousProcessorTimes = processorTimes;

        var memory = new MemoryStatus
        {
            Length = (uint)Marshal.SizeOf<MemoryStatus>()
        };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read physical memory utilization.");
        }

        var usedMemory = memory.TotalPhysical - memory.AvailablePhysical;
        return new SystemPerformanceSample(
            utilization,
            usedMemory,
            memory.TotalPhysical);
    }

    private static ProcessorTimes[] ReadProcessorTimes()
    {
        var itemSize = Marshal.SizeOf<ProcessorTimes>();
        var bufferLength = Math.Max(itemSize * Environment.ProcessorCount, itemSize);

        while (true)
        {
            var buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                var status = NtQuerySystemInformation(
                    SystemProcessorPerformanceInformation,
                    buffer,
                    bufferLength,
                    out var returnLength);
                if (status == StatusInfoLengthMismatch && returnLength > bufferLength)
                {
                    bufferLength = returnLength;
                    continue;
                }

                if (status != 0)
                {
                    throw new InvalidOperationException(
                        $"NtQuerySystemInformation failed with NTSTATUS 0x{status:X8}.");
                }

                var count = returnLength / itemSize;
                if (count <= 0)
                {
                    throw new InvalidOperationException(
                        "Windows returned no processor performance records.");
                }

                var result = new ProcessorTimes[count];
                for (var index = 0; index < count; index++)
                {
                    result[index] = Marshal.PtrToStructure<ProcessorTimes>(
                        IntPtr.Add(buffer, index * itemSize));
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorTimes
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}

internal sealed record SystemPerformanceSample(
    IReadOnlyList<double> ProcessorUtilization,
    ulong UsedPhysicalMemory,
    ulong TotalPhysicalMemory);
