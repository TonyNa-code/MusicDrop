using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MFlacDrop;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput.Trim(), StandardError.Trim() }.Where(x => x.Length > 0));
}

/// <summary>
/// The process could not be brought to a normally verified stop.  When
/// <see cref="CleanupSafe"/> is true, the private Windows Job reported zero
/// active processes after shutdown, so callers may remove files that had been
/// exposed to the contained process tree.  Process.Start cannot eliminate the
/// very small start-to-Job-assignment window; this runner is therefore for the
/// application's trusted/configured local tools, not hostile executables.
/// </summary>
internal sealed class ProcessCleanupException : Exception
{
    public ProcessCleanupException(string message, int processId, bool cleanupSafe, Exception? innerException = null)
        : base(message, innerException)
    {
        ProcessId = processId;
        CleanupSafe = cleanupSafe;
    }

    public int ProcessId { get; }
    public bool CleanupSafe { get; }
}

internal static class ProcessRunner
{
    private static readonly TimeSpan CooperativeExitTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProcessTreeKillTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan JobTerminationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NormalDescendantExitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FinalRootWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(2);

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        string? standardInput,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ProcessRunner requires Windows Job Objects for reliable process-tree cleanup.");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in arguments) psi.ArgumentList.Add(arg);

        using var job = WindowsJob.CreateKillOnClose();
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), cancellationSignal);

        Task stdoutTask = Task.CompletedTask;
        Task stderrTask = Task.CompletedTask;
        Task exitTask = Task.CompletedTask;
        Task? inputTask = null;
        bool started = false;
        bool assignedToJob = false;

        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Failed to start process: {fileName}");
            started = true;
            exitTask = process.WaitForExitAsync(CancellationToken.None);

            try
            {
                // Process.Start cannot attach a Job at creation time.  If the
                // executable creates and detaches a child before this call,
                // that child is not retroactively captured by the Job.  This
                // runner is used only for the application's trusted/configured
                // local tools; shutdown is still reported safe only after the
                // Job itself reports zero active processes.
                job.Assign(process);
                assignedToJob = true;
            }
            catch (Exception assignError)
            {
                CleanupOutcome uncontained = await StopUncontainedProcessAsync(process, exitTask).ConfigureAwait(false);
                throw new ProcessCleanupException(
                    $"Process {process.Id} could not be assigned to a private Windows Job. " +
                    $"Execution was aborted before standard input was supplied. {uncontained.Details}",
                    process.Id, uncontained.CleanupSafe, assignError);
            }

            // Start draining immediately.  Dedicated readers avoid the subtle
            // BeginOutputReadLine/WaitForExit synchronous drain dependency.
            stdoutTask = ReadLinesAsync(process.StandardOutput, stdout, onLine);
            stderrTask = ReadLinesAsync(process.StandardError, stderr, onLine);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (standardInput is not null)
                {
                    // Do not pass the token directly to WriteAsync.  Racing the
                    // write against a cancellation signal guarantees that every
                    // cancellation goes through StopContainedTreeAsync instead of
                    // escaping from this await before the child has been reaped.
                    inputTask = WriteStandardInputAsync(process, standardInput);
                    await AwaitOrCancelAsync(inputTask, cancellationSignal.Task, cancellationToken).ConfigureAwait(false);
                }

                await AwaitOrCancelAsync(exitTask, cancellationSignal.Task, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception operationError)
            {
                CleanupOutcome cleanup = await StopContainedTreeAsync(process, job, exitTask).ConfigureAwait(false);
                Exception? cancellationStreamError = await FinishStreamsAsync(process, inputTask, stdoutTask, stderrTask).ConfigureAwait(false);
                if (!cleanup.Confirmed)
                    throw CreateCleanupException(process.Id, cleanup, cancellationStreamError ?? operationError);
                if (cancellationToken.IsCancellationRequested || operationError is OperationCanceledException)
                    throw new OperationCanceledException(
                        $"Process {process.Id} was cancelled after its process tree was stopped and reaped.",
                        operationError, cancellationToken);
                ExceptionDispatchInfo.Capture(operationError).Throw();
                throw;
            }

            int exitCode = process.ExitCode;

            // A root process can exit while one of its descendants remains
            // alive.  The Job must be empty before the caller may clean up its
            // temporary plaintext directory.
            CleanupOutcome completedTree = await FinishContainedTreeAsync(job).ConfigureAwait(false);
            Exception? streamError = await FinishStreamsAsync(process, inputTask, stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!completedTree.Confirmed)
                throw CreateCleanupException(process.Id, completedTree, streamError);
            if (streamError is not null) ExceptionDispatchInfo.Capture(streamError).Throw();

            return new(exitCode, stdout.ToString(), stderr.ToString());
        }
        catch (ProcessCleanupException)
        {
            throw;
        }
        catch (Exception operationError)
        {
            CleanupOutcome cleanup = !started
                ? CleanupOutcome.AlreadyStopped
                : assignedToJob
                    ? await StopContainedTreeAsync(process, job, exitTask).ConfigureAwait(false)
                    : await StopUncontainedProcessAsync(process, exitTask).ConfigureAwait(false);

            Exception? streamError = started
                ? await FinishStreamsAsync(process, inputTask, stdoutTask, stderrTask).ConfigureAwait(false)
                : null;

            if (!cleanup.Confirmed || !cleanup.CleanupSafe)
                throw CreateCleanupException(process.Id, cleanup, streamError ?? operationError);

            if (cancellationToken.IsCancellationRequested || operationError is OperationCanceledException)
                throw new OperationCanceledException(
                    $"Process {process.Id} was cancelled after its process tree was stopped and reaped.",
                    operationError, cancellationToken);

            ExceptionDispatchInfo.Capture(operationError).Throw();
            throw; // Unreachable; required by definite-assignment analysis.
        }
    }

    private static async Task WriteStandardInputAsync(Process process, string standardInput)
    {
        await process.StandardInput.WriteAsync(standardInput.AsMemory(), CancellationToken.None).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private static async Task ReadLinesAsync(StreamReader reader, StringBuilder destination, Action<string>? onLine)
    {
        ExceptionDispatchInfo? callbackError = null;
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            destination.AppendLine(line);
            if (onLine is null) continue;
            try { onLine(line); }
            catch (Exception ex) { callbackError ??= ExceptionDispatchInfo.Capture(ex); }
        }
        callbackError?.Throw();
    }

    private static async Task AwaitOrCancelAsync(Task operation, Task cancellationSignal, CancellationToken token)
    {
        Task completed = await Task.WhenAny(operation, cancellationSignal).ConfigureAwait(false);
        if (completed == cancellationSignal)
            throw new OperationCanceledException(token);
        await operation.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private static async Task<CleanupOutcome> StopContainedTreeAsync(
        Process process,
        WindowsJob job,
        Task exitTask)
    {
        var diagnostics = new List<string>();
        TryCloseInputWithoutFlush(process, diagnostics);

        JobWaitResult cooperative = await job.WaitUntilEmptyAsync(CooperativeExitTimeout).ConfigureAwait(false);
        if (cooperative == JobWaitResult.Signaled)
            return new(true, true, "Windows Job reported that the process tree is empty.");
        if (cooperative == JobWaitResult.Failed)
            diagnostics.Add("The initial Windows Job wait failed: " + job.LastWaitError);

        if (!HasExited(process))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { diagnostics.Add("Process.Kill(entireProcessTree) failed: " + ex.Message); }

            JobWaitResult killed = await job.WaitUntilEmptyAsync(ProcessTreeKillTimeout).ConfigureAwait(false);
            if (killed == JobWaitResult.Signaled)
                return new(true, true, JoinDiagnostics(diagnostics, "The Job became empty after process-tree termination."));
            if (killed == JobWaitResult.Failed)
                diagnostics.Add("The post-Kill Windows Job wait failed: " + job.LastWaitError);
        }

        if (!job.Terminate())
            diagnostics.Add("TerminateJobObject failed: " + job.LastNativeError);

        JobWaitResult terminated = await job.WaitUntilEmptyAsync(JobTerminationTimeout).ConfigureAwait(false);
        if (terminated == JobWaitResult.Signaled)
            return new(true, true, JoinDiagnostics(diagnostics, "The Job became empty after TerminateJobObject."));
        if (terminated == JobWaitResult.Failed)
            diagnostics.Add("The post-termination Windows Job wait failed: " + job.LastWaitError);
        else
            diagnostics.Add($"The Job did not report empty within {JobTerminationTimeout.TotalSeconds:0} seconds.");

        // Final bounded escalation.  KILL_ON_JOB_CLOSE is enforced by the
        // kernel for every process still associated with this Job.  Closing
        // this handle is therefore the safety boundary that prevents the
        // caller from racing deletion against a surviving contained child.
        job.CloseForKill();
        bool rootExited = await CompletesWithinAsync(exitTask, FinalRootWaitTimeout).ConfigureAwait(false);
        if (!rootExited)
            diagnostics.Add($"The root process did not signal exit within the final {FinalRootWaitTimeout.TotalSeconds:0} seconds.");

        return new(false, false, JoinDiagnostics(diagnostics,
            "KILL_ON_JOB_CLOSE was issued, but process-tree shutdown could not be independently verified; temporary-file cleanup is not confirmed safe."));
    }

    private static async Task<CleanupOutcome> FinishContainedTreeAsync(WindowsJob job)
    {
        JobWaitResult completed = await job.WaitUntilEmptyAsync(NormalDescendantExitTimeout).ConfigureAwait(false);
        if (completed == JobWaitResult.Signaled)
            return new(true, true, "Windows Job reported that the completed process tree is empty.");

        var diagnostics = new List<string>();
        if (completed == JobWaitResult.Failed)
            diagnostics.Add("The completed Windows Job wait failed: " + job.LastWaitError);
        else
            diagnostics.Add($"Descendants remained after the root exited for {NormalDescendantExitTimeout.TotalSeconds:0} seconds.");

        if (!job.Terminate())
            diagnostics.Add("TerminateJobObject failed: " + job.LastNativeError);
        JobWaitResult terminated = await job.WaitUntilEmptyAsync(JobTerminationTimeout).ConfigureAwait(false);
        if (terminated == JobWaitResult.Signaled)
            return new(false, true, JoinDiagnostics(diagnostics,
                "The lingering process tree was stopped; normal completion was rejected."));
        if (terminated == JobWaitResult.Failed)
            diagnostics.Add("The post-termination Windows Job wait failed: " + job.LastWaitError);
        else
            diagnostics.Add($"The Job did not report empty within the final {JobTerminationTimeout.TotalSeconds:0} seconds.");

        job.CloseForKill();
        return new(false, false, JoinDiagnostics(diagnostics,
            "KILL_ON_JOB_CLOSE was issued, but process-tree shutdown could not be independently verified; temporary-file cleanup is not confirmed safe."));
    }

    private static async Task<CleanupOutcome> StopUncontainedProcessAsync(Process process, Task exitTask)
    {
        var diagnostics = new List<string>();
        TryCloseInputWithoutFlush(process, diagnostics);
        if (!HasExited(process))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { diagnostics.Add("Process.Kill(entireProcessTree) failed: " + ex.Message); }
        }

        bool exited = await CompletesWithinAsync(exitTask, ProcessTreeKillTimeout).ConfigureAwait(false);
        if (!exited && !HasExited(process))
        {
            try { process.Kill(); }
            catch (Exception ex) { diagnostics.Add("Final root-process Kill failed: " + ex.Message); }
            exited = await CompletesWithinAsync(exitTask, FinalRootWaitTimeout).ConfigureAwait(false);
        }

        if (!exited)
            diagnostics.Add("The uncontained root process could not be confirmed stopped within the bounded wait.");
        // Killing/reaping the root cannot prove that a child which escaped in
        // the narrow Process.Start-to-AssignProcessToJobObject window has also
        // stopped.  Mark this path unsafe even when the root is confirmed so
        // the caller retains any temporary plaintext rather than deleting it
        // under a potentially surviving process.
        return new(exited, false, JoinDiagnostics(diagnostics,
            exited
                ? "The uncontained root process was stopped, but escaped descendants cannot be ruled out; temporary-file cleanup is not confirmed safe."
                : "Temporary-file cleanup is not confirmed safe."));
    }

    private static async Task<Exception?> FinishStreamsAsync(
        Process process,
        Task? inputTask,
        Task stdoutTask,
        Task stderrTask)
    {
        Task all = Task.WhenAll(new[] { inputTask ?? Task.CompletedTask, stdoutTask, stderrTask });
        if (!await CompletesWithinAsync(all, StreamDrainTimeout).ConfigureAwait(false))
        {
            // The process tree is already stopped at every call site.  Closing
            // our pipe endpoints cannot affect plaintext safety and prevents a
            // defective redirected stream from causing an unbounded wait.
            if (process.StartInfo.RedirectStandardInput) TryDispose(process.StandardInput);
            TryDispose(process.StandardOutput);
            TryDispose(process.StandardError);
            if (!await CompletesWithinAsync(all, StreamDrainTimeout).ConfigureAwait(false))
                return new TimeoutException("Redirected process streams did not finish within the bounded drain period.");
        }

        try { await all.ConfigureAwait(false); return null; }
        catch (Exception ex) { return ex; }
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            try { await task.ConfigureAwait(false); } catch { }
            return true;
        }

        Task completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task) return false;
        try { await task.ConfigureAwait(false); } catch { }
        return true;
    }

    private static void TryCloseInputWithoutFlush(Process process, List<string> diagnostics)
    {
        if (!process.StartInfo.RedirectStandardInput) return;
        try { process.StandardInput.BaseStream.Dispose(); }
        catch (Exception ex) { diagnostics.Add("Closing redirected standard input failed: " + ex.Message); }
    }

    private static void TryDispose(IDisposable disposable)
    {
        try { disposable.Dispose(); } catch { }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return false; }
    }

    private static ProcessCleanupException CreateCleanupException(
        int processId,
        CleanupOutcome cleanup,
        Exception? innerException)
    {
        string safety = cleanup.CleanupSafe
            ? "The private Job reported zero active processes, so temporary-file deletion is safe."
            : "Temporary-file deletion is NOT confirmed safe.";
        return new ProcessCleanupException(
            $"Failed to verify shutdown of process tree {processId}. {safety} {cleanup.Details}",
            processId, cleanup.CleanupSafe, innerException);
    }

    private static string JoinDiagnostics(IEnumerable<string> diagnostics, string conclusion)
    {
        string detail = string.Join(" ", diagnostics.Where(x => !string.IsNullOrWhiteSpace(x)));
        return detail.Length == 0 ? conclusion : detail + " " + conclusion;
    }

    private readonly record struct CleanupOutcome(bool Confirmed, bool CleanupSafe, string Details)
    {
        public static CleanupOutcome AlreadyStopped => new(true, true, "No process was started.");
    }

    private enum JobWaitResult
    {
        Signaled,
        TimedOut,
        Failed,
    }

    private sealed class WindowsJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectBasicAccountingInformationClass = 1;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint CancellationExitCode = 0xC000013A;

        private readonly SafeJobHandle handle;

        private WindowsJob(SafeJobHandle handle) => this.handle = handle;

        public int LastNativeError { get; private set; }
        public int LastWaitError { get; private set; }

        public static WindowsJob CreateKillOnClose()
        {
            SafeJobHandle handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, ref limits, (uint)size))
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Could not enable KILL_ON_JOB_CLOSE on the Windows Job.");
            }
            return new WindowsJob(handle);
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"AssignProcessToJobObject failed for process {process.Id}.");
        }

        public bool Terminate()
        {
            if (handle.IsClosed) return true;
            bool success = TerminateJobObject(handle, CancellationExitCode);
            if (!success) LastNativeError = Marshal.GetLastWin32Error();
            return success;
        }

        public async Task<JobWaitResult> WaitUntilEmptyAsync(TimeSpan timeout)
        {
            if (handle.IsClosed) return JobWaitResult.Failed;
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                if (!TryGetActiveProcessCount(out uint activeProcesses))
                {
                    LastWaitError = Marshal.GetLastWin32Error();
                    return JobWaitResult.Failed;
                }
                if (activeProcesses == 0) return JobWaitResult.Signaled;
                if (stopwatch.Elapsed >= timeout) return JobWaitResult.TimedOut;
                TimeSpan remaining = timeout - stopwatch.Elapsed;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(50)
                    ? remaining
                    : TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            }
        }

        private bool TryGetActiveProcessCount(out uint activeProcesses)
        {
            activeProcesses = 0;
            if (!QueryInformationJobObject(handle, JobObjectBasicAccountingInformationClass,
                    out JobObjectBasicAccountingInformation accounting,
                    (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(), out _))
                return false;
            activeProcesses = accounting.ActiveProcesses;
            return true;
        }

        public void CloseForKill() => handle.Dispose();
        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeJobHandle hJob,
            int jobObjectInfoClass,
            ref JobObjectExtendedLimitInformation lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(
            SafeJobHandle hJob,
            int jobObjectInfoClass,
            out JobObjectBasicAccountingInformation lpJobObjectInfo,
            uint cbJobObjectInfoLength,
            out uint lpReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicAccountingInformation
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeJobHandle() : base(ownsHandle: true) { }

            protected override bool ReleaseHandle() => CloseHandle(handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr hObject);
        }
    }
}
