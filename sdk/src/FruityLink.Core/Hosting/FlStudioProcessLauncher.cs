using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace FruityLink.Core.Hosting;

/// <summary>Starts owned Windows GUI processes suitable for scriptable FL Studio jobs.</summary>
public static class FlStudioProcessLauncher
{
    /// <summary>Starts a process, assigns it to a kill-on-close job before its first instruction, and returns its lease.</summary>
    public static FlStudioProcessLease Start(FlStudioLaunchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.Mode == FlStudioLaunchMode.Interactive)
            return StartCore(options, null, null);

        FlStudioProcessLease? lease = null;
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var startup = options.SerializeStartup ? new StartupGate(options.ExecutablePath) : null;
        var broker = new Thread(() =>
        {
            try
            {
                startup?.Acquire(options.StartupTimeout, cancellationToken);
                var desktopName = $"FruityLink-{Environment.ProcessId}-{Guid.NewGuid():N}";
                lease = StartCore(options, desktopName, startup);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                completed.Set();
                try { if (lease is not null) startup?.HoldUntilReadyOrExit(lease); }
                finally { startup?.Release(); }
            }
        }) { IsBackground = true, Name = "FruityLink private desktop broker" };
        broker.SetApartmentState(ApartmentState.STA);
        broker.Start();
        completed.Wait();
        if (failure is not null)
        {
            startup?.WaitReleased();
            startup?.Dispose();
            if (failure is OperationCanceledException) throw failure;
            if (failure is TimeoutException) throw failure;
            throw new InvalidOperationException($"Could not launch the process on a private desktop: {failure.Message}", failure);
        }
        return lease!;
    }

    private static FlStudioProcessLease StartCore(FlStudioLaunchOptions options, string? desktopName, StartupGate? startup)
    {
        SafeDesktopHandle? desktop = null;
        SafeJobHandle? job = null;
        SafeKernelHandle? process = null;
        SafeKernelHandle? thread = null;
        try
        {
            if (desktopName is not null)
            {
                desktop = Native.CreateDesktopW(desktopName, 0, 0, 0, Native.DesktopAccess, 0);
                if (desktop.IsInvalid) throw Native.Error("CreateDesktopW");
            }

            job = Native.CreateJobObjectW(0, null);
            if (job.IsInvalid) throw Native.Error("CreateJobObjectW");
            var limits = new Native.JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new() { LimitFlags = Native.JobObjectLimitKillOnJobClose }
            };
            if (!Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.JobObjectExtendedLimitInformation>()))
                throw Native.Error("SetInformationJobObject");

            var startupInfo = new Native.StartupInfo { Cb = (uint)Marshal.SizeOf<Native.StartupInfo>(), Desktop = desktopName };
            var commandLine = new StringBuilder(BuildCommandLine(options.ExecutablePath, options.Arguments));
            var environment = BuildLaunchEnvironment(options.Environment, startup);
            var workingDirectory = options.WorkingDirectory ?? Path.GetDirectoryName(options.ExecutablePath)!;
            if (!Native.CreateProcessW(options.ExecutablePath, commandLine, 0, 0, false,
                    Native.CreateSuspended | Native.CreateUnicodeEnvironment, environment, workingDirectory,
                    ref startupInfo, out var info))
                throw Native.Error("CreateProcessW");
            process = new(info.Process, true);
            thread = new(info.Thread, true);
            if (!Native.AssignProcessToJobObject(job, process)) throw Native.Error("AssignProcessToJobObject");
            if (Native.ResumeThread(thread) == uint.MaxValue) throw Native.Error("ResumeThread");
            thread.Dispose();
            thread = null;
            var result = new FlStudioProcessLease(process, job, desktop, info.ProcessId, desktopName,
                options.Mode, options.ShutdownTimeout, startup);
            process = null;
            job = null;
            desktop = null;
            return result;
        }
        finally
        {
            if (process is not null && !process.IsInvalid)
            {
                Native.TerminateProcess(process, 1);
                Native.WaitForSingleObject(process, 5000);
            }
            thread?.Dispose();
            process?.Dispose();
            job?.Dispose();
            desktop?.Dispose();
        }
    }

    private static void Validate(FlStudioLaunchOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FL Studio process launching requires Windows.");
        if (!IsAbsoluteValue(options.ExecutablePath))
            throw new ArgumentException("ExecutablePath must be an absolute path.", nameof(options));
        if (!File.Exists(options.ExecutablePath)) throw new FileNotFoundException("The executable does not exist.", options.ExecutablePath);
        if (options.WorkingDirectory is { } directory && !IsExistingAbsoluteDirectory(directory))
            throw new DirectoryNotFoundException("WorkingDirectory must be an existing absolute directory.");
        if (!IsValidTimeout(options.ShutdownTimeout))
            throw new ArgumentOutOfRangeException(nameof(options), "ShutdownTimeout must be between zero and one minute.");
        if (!IsValidStartupTimeout(options.StartupTimeout))
            throw new ArgumentOutOfRangeException(nameof(options), "StartupTimeout must be between zero and thirty minutes.");
        foreach (var pair in options.Environment)
            if (!IsValidEnvironmentEntry(pair))
                throw new ArgumentException("Environment names and values must be valid Windows environment strings.", nameof(options));
    }

    private static bool IsAbsoluteValue(string value) => !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value);
    private static bool IsExistingAbsoluteDirectory(string value) => Path.IsPathFullyQualified(value) && Directory.Exists(value);
    private static bool IsValidTimeout(TimeSpan value) => value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(1);
    private static bool IsValidStartupTimeout(TimeSpan value) => value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(30);
    private static bool IsValidEnvironmentEntry(KeyValuePair<string, string?> pair) =>
        pair.Key.Length > 0 && !pair.Key.Contains('=') && !pair.Key.Contains('\0') && pair.Value?.Contains('\0') != true;

    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments) =>
        string.Join(" ", new[] { Quote(executable) }.Concat(arguments.Select(Quote)));

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var result = new StringBuilder(value.Length + 2).Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append(character);
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        var values = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var pair in overrides)
            if (pair.Value is null) values.Remove(pair.Key); else values[pair.Key] = pair.Value;
        return string.Concat(values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}\0")) + "\0";
    }

    private static string BuildLaunchEnvironment(IReadOnlyDictionary<string, string?> source, StartupGate? startup)
    {
        if (startup is null) return BuildEnvironmentBlock(source);
        var overrides = source.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        overrides[StartupGate.EventVariable] = startup.EventName;
        return BuildEnvironmentBlock(overrides);
    }

    internal static class Native
    {
        internal const uint CreateSuspended = 0x4, CreateUnicodeEnvironment = 0x400, JobObjectLimitKillOnJobClose = 0x2000;
        internal const uint DesktopAccess = 0x01FF;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
        {
            internal uint Cb; internal string? Reserved; internal string? Desktop; internal string? Title;
            internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal ushort ShowWindow, Reserved2Count; internal nint Reserved2, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal nint Process, Thread; internal int ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimitInformation
        { internal long PerProcessUserTimeLimit, PerJobUserTimeLimit; internal uint LimitFlags; internal nuint MinimumWorkingSetSize, MaximumWorkingSetSize; internal uint ActiveProcessLimit; internal nuint Affinity; internal uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] internal struct JobObjectExtendedLimitInformation
        { internal BasicLimitInformation BasicLimitInformation; internal IoCounters IoInfo; internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeDesktopHandle CreateDesktopW(string name, nint device, nint devmode, uint flags, uint access, nint attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeJobHandle CreateJobObjectW(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, string environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeKernelHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeKernelHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeKernelHandle process, uint exitCode);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);
        internal static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation + " failed.");
    }
}

internal sealed class StartupGate : IDisposable
{
    internal const string EventVariable = "FRUITYLINK_STARTUP_EVENT";
    private readonly Mutex mutex;
    private readonly EventWaitHandle ready;
    private readonly ManualResetEventSlim released = new();
    private int held;
    private int disposed;

    internal StartupGate(string executable)
    {
        var normalized = Path.GetFullPath(executable).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        mutex = new(false, @"Local\FruityLink-Startup-" + hash);
        EventName = @"Local\FruityLink-Ready-" + Guid.NewGuid().ToString("N");
        ready = new(false, EventResetMode.ManualReset, EventName);
    }

    internal string EventName { get; }
    internal bool Held => Volatile.Read(ref held) != 0;
    internal void Acquire(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var index = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], timeout);
            if (index == WaitHandle.WaitTimeout) throw new TimeoutException($"Another FL Studio process did not finish cold startup within {timeout}.");
            if (index == 1) throw new OperationCanceledException(cancellationToken);
        }
        catch (AbandonedMutexException) { }
        Volatile.Write(ref held, 1);
    }

    internal void HoldUntilReadyOrExit(FlStudioProcessLease lease)
    {
        while (!ready.WaitOne(100) && !lease.RootHasExited) { }
    }

    internal void SignalReady() => ready.Set();
    internal void Release()
    {
        if (Interlocked.Exchange(ref held, 0) != 0) mutex.ReleaseMutex();
        released.Set();
    }
    internal void WaitReleased() => released.Wait();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ready.Set();
        ready.Dispose();
        mutex.Dispose();
        released.Dispose();
    }
}

internal sealed class SafeDesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeDesktopHandle() : base(true) { }
    protected override bool ReleaseHandle() => CloseDesktop(handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
}

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeJobHandle() : base(true) { }
    protected override bool ReleaseHandle() => CloseHandle(handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint value);
}

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeKernelHandle(nint handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);
    protected override bool ReleaseHandle() => CloseHandle(handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint value);
}
