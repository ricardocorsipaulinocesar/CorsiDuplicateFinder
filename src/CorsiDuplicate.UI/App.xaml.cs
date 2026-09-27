using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using CorsiDuplicate.Infrastructure.Logging;

namespace CorsiDuplicate.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// WPF's default behavior for ANY unhandled exception on the UI thread — from a
    /// binding converter, an event handler, a command, anywhere — is to silently
    /// terminate the entire process with no dialog and no log, which is exactly the
    /// "the program closes by itself" symptom. Hooking every exception surface (UI
    /// thread, background threads, unobserved Task exceptions) and logging + containing
    /// them here is what keeps a single bug in one area from taking down the whole app.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLogger.Info(nameof(App), nameof(OnStartup), "Application starting.");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Fatal(nameof(App), nameof(OnDispatcherUnhandledException),
            "Unhandled exception on the UI thread.", e.Exception);

        MessageBox.Show(
            $"An unexpected error occurred and was logged:\n\n{e.Exception.Message}\n\nThe application will keep running.",
            "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);

        // Without this, WPF terminates the whole process for any unhandled UI-thread
        // exception — this is what actually stops that from happening.
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // A non-UI-thread exception that reaches here is fatal regardless (the CLR
        // terminates the process right after this event) — still log it so there's a
        // record of what happened, even though it can't be contained.
        AppLogger.Fatal(nameof(App), nameof(OnAppDomainUnhandledException),
            "Unhandled exception outside the UI thread (process is terminating).",
            e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error(nameof(App), nameof(OnUnobservedTaskException),
            "An async Task's exception was never observed (fire-and-forget work failed silently).",
            e.Exception);
        e.SetObserved();
    }
}

