#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dynex.Android;

// Independent probes: no network, VPN, limiter, or Task-based watchdog.
internal static class DebugRuntimeDiagnostics
{
    public static Task RunAsync(Action<string> report)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayDone = 0; var timerDone = 0; var cancelDone = 0; var poolDone = 0; var jniDone = 0;
        var watch = Stopwatch.StartNew();
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
        report($"BASELINE_BEGIN runtime={RuntimeInformation.FrameworkDescription} processors={Environment.ProcessorCount} min={minWorkers}/{minIo} available={availableWorkers}/{availableIo} frequency={Stopwatch.Frequency}");
        new Thread(() =>
        {
            try
            {
                report("BASELINE_NATIVE_THREAD_ENTER");
                Thread.Sleep(100);
                report("BASELINE_NATIVE_THREAD_SLEEP_RETURN");
                for (var index = 0; index < 10; index++)
                {
                    Thread.Sleep(500);
                    report($"BASELINE_WATCH {index} elapsed={watch.ElapsedMilliseconds} tick={Environment.TickCount64} delay={Volatile.Read(ref delayDone)} timer={Volatile.Read(ref timerDone)} cancel={Volatile.Read(ref cancelDone)} pool={Volatile.Read(ref poolDone)} jni={Volatile.Read(ref jniDone)}");
                    if (Volatile.Read(ref delayDone) + Volatile.Read(ref timerDone) + Volatile.Read(ref cancelDone) + Volatile.Read(ref poolDone) + Volatile.Read(ref jniDone) == 5)
                    {
                        report("BASELINE_PASS"); complete.TrySetResult(); return;
                    }
                }
                complete.TrySetException(new TimeoutException("Independent Android Task.Delay/Timer/Cancellation/ThreadPool/JNI baseline did not complete in five seconds."));
            }
            catch (Exception error) { complete.TrySetException(error); }
        }) { IsBackground = true, Name = "Dynex baseline watchdog" }.Start();
        new Thread(() =>
        {
            try
            {
                report("BASELINE_JNI_ENTER");
                global::Android.Util.Log.Info("DYNEX-TEST", "Independent JNI worker baseline");
                Interlocked.Exchange(ref jniDone, 1); report("BASELINE_JNI_RETURN");
            }
            catch (Exception error) { report("BASELINE_JNI_FAIL " + error); }
        }) { IsBackground = true, Name = "Dynex baseline JNI" }.Start();
        new Thread(() => ThreadPool.QueueUserWorkItem(_ =>
        {
            Interlocked.Exchange(ref poolDone, 1); report("BASELINE_POOL_QUEUED_RETURN");
        })) { IsBackground = true, Name = "Dynex baseline pool" }.Start();
        _ = ProbeTimersAsync();
        return complete.Task;

        async Task ProbeTimersAsync()
        {
            report("BASELINE_TIMERS_ENTER");
            using var timer = new Timer(_ => { Interlocked.Exchange(ref timerDone, 1); report("BASELINE_TIMER_RETURN"); }, null, 100, Timeout.Infinite);
            using var cancellation = new CancellationTokenSource();
            using var registration = cancellation.Token.Register(() => { Interlocked.Exchange(ref cancelDone, 1); report("BASELINE_CANCEL_RETURN"); });
            cancellation.CancelAfter(150);
            await Task.Delay(200).ConfigureAwait(false);
            Interlocked.Exchange(ref delayDone, 1); report("BASELINE_DELAY_RETURN");
            await complete.Task.ConfigureAwait(false);
        }
    }
}
#endif
