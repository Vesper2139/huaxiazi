using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFloat.Services;

public enum SingleInstanceAcquireStatus { Acquired, AlreadyRunning, Unavailable }

public sealed record SingleInstanceAcquireResult(
    SingleInstanceAcquireStatus Status,
    SingleInstanceGuard? Guard,
    string Message);

internal sealed record NamedMutexCreation(Mutex Mutex, bool CreatedNew);

/// <summary>One instance per interactive session, with a signal that surfaces the existing UI.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly CancellationTokenSource _waitCancellation = new();
    private readonly Task? _waitTask;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, string eventName)
    {
        _mutex = mutex;
        _ownsMutex = true;
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            _waitTask = Task.Run(WaitForActivation);
        }
        catch
        {
            // Mutex ownership remains valid even if activation signalling is unavailable.
        }
    }

    public event EventHandler? ActivationRequested;

    public static string BuildSessionScopedName(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var id = applicationId.Trim();
        if (id.StartsWith("Global\\", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("Local\\", StringComparison.OrdinalIgnoreCase))
            id = id[(id.IndexOf('\\') + 1)..];
        return "Local\\" + id;
    }

    public static SingleInstanceAcquireResult Acquire(string applicationId) =>
        Acquire(applicationId, name =>
        {
            var mutex = new Mutex(true, name, out var createdNew);
            return new NamedMutexCreation(mutex, createdNew);
        });

    internal static SingleInstanceAcquireResult Acquire(
        string applicationId,
        Func<string, NamedMutexCreation> mutexFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentNullException.ThrowIfNull(mutexFactory);
        var mutexName = BuildSessionScopedName(applicationId);
        var eventName = mutexName + ".Activate";
        try
        {
            var creation = mutexFactory(mutexName);
            if (!creation.CreatedNew)
            {
                creation.Mutex.Dispose();
                SignalExisting(eventName);
                return new(SingleInstanceAcquireStatus.AlreadyRunning, null, "Vesper 已在当前桌面会话运行。");
            }

            return new(SingleInstanceAcquireStatus.Acquired,
                new SingleInstanceGuard(creation.Mutex, eventName), "已取得单实例所有权。");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.IO.IOException or WaitHandleCannotBeOpenedException)
        {
            return new(SingleInstanceAcquireStatus.Unavailable, null,
                "无法启用单实例保护，应用将继续启动：" + exception.Message);
        }
    }

    public static bool TryAcquire(string name, out SingleInstanceGuard? guard)
    {
        var result = Acquire(name);
        guard = result.Guard;
        return result.Status == SingleInstanceAcquireStatus.Acquired;
    }

    private static void SignalExisting(string eventName)
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(eventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void WaitForActivation()
    {
        if (_activationEvent is null) return;
        var handles = new WaitHandle[] { _activationEvent, _waitCancellation.Token.WaitHandle };
        while (!_waitCancellation.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(handles) != 0) return;
            try { ActivationRequested?.Invoke(this, EventArgs.Empty); }
            catch { }
        }
    }

    public void Dispose()
    {
        if (!_ownsMutex) return;
        _ownsMutex = false;
        _waitCancellation.Cancel();
        try { _activationEvent?.Set(); } catch { }
        try { _waitTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _activationEvent?.Dispose();
        _waitCancellation.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
