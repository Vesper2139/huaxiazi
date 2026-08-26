using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Threading;

namespace Huaxiazi.Services;

/// <summary>
/// One CompositionTarget.Rendering subscription per WPF Dispatcher. All companion instances
/// on the same UI thread share it, while designer/test dispatchers are isolated and detached
/// before shutdown.
/// </summary>
public static class CompanionFrameClock
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Dispatcher, ClockGroup> Groups = [];

    public static IDisposable Subscribe(Action<TimeSpan> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var dispatcher = Dispatcher.CurrentDispatcher;
        ClockGroup group;
        lock (Gate)
        {
            if (!Groups.TryGetValue(dispatcher, out group!))
            {
                group = new ClockGroup(dispatcher, RemoveGroup);
                Groups.Add(dispatcher, group);
            }
        }
        return group.Add(callback);
    }

    private static void RemoveGroup(ClockGroup group)
    {
        lock (Gate)
        {
            if (Groups.TryGetValue(group.Dispatcher, out var current) && ReferenceEquals(current, group))
                Groups.Remove(group.Dispatcher);
        }
    }

    private sealed class ClockGroup
    {
        private readonly List<Action<TimeSpan>> _callbacks = [];
        private readonly Action<ClockGroup> _onEmpty;
        private TimeSpan _lastRenderingTime;
        private bool _disposed;

        public ClockGroup(Dispatcher dispatcher, Action<ClockGroup> onEmpty)
        {
            Dispatcher = dispatcher;
            _onEmpty = onEmpty;
            CompositionTarget.Rendering += OnRendering;
            Dispatcher.ShutdownStarted += OnDispatcherShutdown;
        }

        public Dispatcher Dispatcher { get; }

        public IDisposable Add(Action<TimeSpan> callback)
        {
            if (_disposed) return EmptySubscription.Instance;
            _callbacks.Add(callback);
            return new Subscription(this, callback);
        }

        private void OnRendering(object? sender, EventArgs args)
        {
            if (_disposed || !Dispatcher.CheckAccess() || args is not RenderingEventArgs rendering) return;
            var delta = _lastRenderingTime == TimeSpan.Zero
                ? TimeSpan.FromSeconds(1d / 60)
                : rendering.RenderingTime - _lastRenderingTime;
            _lastRenderingTime = rendering.RenderingTime;
            if (delta <= TimeSpan.Zero || delta > TimeSpan.FromMilliseconds(80))
                delta = TimeSpan.FromSeconds(1d / 60);
            foreach (var callback in _callbacks.ToArray()) callback(delta);
        }

        private void Remove(Action<TimeSpan> callback)
        {
            if (_disposed) return;
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    _ = Dispatcher.BeginInvoke(new Action(() => Remove(callback)));
                return;
            }
            _callbacks.Remove(callback);
            if (_callbacks.Count == 0) DisposeCore();
        }

        private void OnDispatcherShutdown(object? sender, EventArgs e) => DisposeCore();

        private void DisposeCore()
        {
            if (_disposed) return;
            _disposed = true;
            _callbacks.Clear();
            CompositionTarget.Rendering -= OnRendering;
            Dispatcher.ShutdownStarted -= OnDispatcherShutdown;
            _onEmpty(this);
        }

        private sealed class Subscription(ClockGroup owner, Action<TimeSpan> callback) : IDisposable
        {
            private ClockGroup? _owner = owner;
            public void Dispose()
            {
                _owner?.Remove(callback);
                _owner = null;
            }
        }

        private sealed class EmptySubscription : IDisposable
        {
            public static EmptySubscription Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
