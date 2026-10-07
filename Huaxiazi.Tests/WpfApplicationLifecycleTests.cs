using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class WpfApplicationLifecycleTests
{
    [Fact]
    public void ShutdownCurrentDispatcher_ClosesStaOwnedDispatcherWithoutAnApplication()
    {
        Exception? failure = null;
        var dispatcherShutdown = false;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                TestHelpers.ShutdownCurrentWpfDispatcher();
                dispatcherShutdown = dispatcher.HasShutdownStarted;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(dispatcherShutdown, "A WPF Dispatcher created by an STA test worker must stop before the thread exits.");
    }

    [Fact]
    public void ResetWpfApplication_ShutsDownDispatcherBeforeStaThreadExits()
    {
        Exception? failure = null;
        var dispatcherShutdown = false;
        var applicationReleased = false;
        var thread = new Thread(() =>
        {
            try
            {
                var application = TestHelpers.EnsureWpfApplication();
                var dispatcher = application.Dispatcher;
                Assert.False(dispatcher.HasShutdownStarted);

                TestHelpers.ResetWpfApplication();

                dispatcherShutdown = dispatcher.HasShutdownStarted;
                applicationReleased = Application.Current is null;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(dispatcherShutdown, "The WPF Dispatcher must stop before its owning STA thread exits.");
        Assert.True(applicationReleased, "The WPF Application static state must be cleared before a later STA test creates another application.");
    }

}

