using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using PixelOverlay.Services;
using PixelOverlay.Views;

namespace PixelOverlay
{
    sealed partial class App : Application
    {
        private sealed class WidgetRegistration
        {
            public XboxGameBarWidget Widget { get; set; }
            public CoreDispatcher Dispatcher { get; set; }
            public Action Cleanup { get; set; }
        }

        private readonly object _widgetsSync = new object();
        // Each Game Bar widget owns a separate CoreWindow and dispatcher.
        private readonly List<WidgetRegistration> _widgets = new List<WidgetRegistration>();

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
            Suspending += OnSuspending;
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            try
            {
                var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
                var protocol = args as IProtocolActivatedEventArgs;
                if (args.Kind != ActivationKind.Protocol || widgetArgs == null ||
                    protocol == null || protocol.Uri.Scheme != "ms-gamebarwidget")
                    return;

                // Re-activation belongs to an existing widget. Keep its host object alive;
                // never create a second XboxGameBarWidget for the same activation.
                if (!widgetArgs.IsLaunchActivation) return;

                Type pageType;
                if (widgetArgs.AppExtensionId == "PixelOverlay") pageType = typeof(OverlayPage);
                else if (widgetArgs.AppExtensionId == "PixelOverlaySettings") pageType = typeof(SettingsPage);
                else return;

                await SettingsStore.Current.LoadAsync();

                var window = Window.Current;
                var frame = new Frame();
                frame.NavigationFailed += OnNavigationFailed;
                window.Content = frame;

                var widget = new XboxGameBarWidget(widgetArgs, window.CoreWindow, frame);
                if (!frame.Navigate(pageType, widget))
                    throw new InvalidOperationException("Game Bar widget page navigation was rejected.");

                var page = frame.Content as Page;
                Action cleanup = page is OverlayPage overlay
                    ? (Action)overlay.OnWindowClosed
                    : page is SettingsPage settings
                        ? (Action)settings.OnWindowClosed
                        : delegate { };

                var registration = new WidgetRegistration
                {
                    Widget = widget,
                    Dispatcher = window.Dispatcher,
                    Cleanup = cleanup
                };

                lock (_widgetsSync) _widgets.Add(registration);
                window.Closed += (sender, e) =>
                {
                    try { registration.Cleanup(); }
                    catch (Exception ex) { AppDiagnostics.Log("Widget window cleanup", ex); }
                    finally
                    {
                        lock (_widgetsSync) _widgets.Remove(registration);
                    }
                };

                window.Activate();
            }
            catch (Exception ex) { AppDiagnostics.Log("Widget activation", ex); }
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                await SettingsStore.Current.LoadAsync();
                var window = Window.Current;
                var frame = window.Content as Frame ?? new Frame();
                frame.NavigationFailed += OnNavigationFailed;
                window.Content = frame;
                if (frame.Content == null)
                    frame.Navigate(typeof(SettingsPage), null);

                window.Closed += (sender, args) =>
                {
                    try
                    {
                        if (frame.Content is SettingsPage settings)
                            settings.OnWindowClosed();
                    }
                    catch (Exception ex) { AppDiagnostics.Log("Normal window cleanup", ex); }
                };
                window.Activate();
            }
            catch (Exception ex) { AppDiagnostics.Log("Normal launch", ex); }
        }

        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            AppDiagnostics.Log("Unhandled XAML/WinRT exception", e.Exception ?? new Exception(e.Message));
            // Do not mark this handled: the framework may be in an inconsistent state.
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                WidgetRegistration[] registrations;
                lock (_widgetsSync) registrations = _widgets.ToArray();

                // The last CoreWindow does not raise Window.Closed. Suspension is the fallback.
                foreach (var registration in registrations)
                {
                    try
                    {
                        if (registration.Dispatcher.HasThreadAccess)
                            registration.Cleanup();
                        else
                            await registration.Dispatcher.RunAsync(CoreDispatcherPriority.High,
                                () => registration.Cleanup());
                    }
                    catch (Exception ex) { AppDiagnostics.Log("Suspending widget cleanup", ex); }
                }

                lock (_widgetsSync) _widgets.Clear();
                await SettingsStore.Current.FlushAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Suspend settings flush", ex); }
            finally { deferral.Complete(); }
        }

        private static void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            AppDiagnostics.Log("Navigation failed", new InvalidOperationException(
                "Failed to load " + e.SourcePageType.FullName));
            Window.Current.Content = new TextBlock
            {
                Text = "Pixel Overlay could not open this page. See pixel-overlay-diagnostics.log."
            };
        }
    }
}
