using System;
using System.Collections.Generic;
using PixelOverlay.Services;
using PixelOverlay.Views;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace PixelOverlay
{
    sealed partial class App : Application
    {
        // Keep widget IPC objects alive until the owning CoreWindow closes.
        private readonly object _windowsSync = new object();
        private readonly List<WindowEntry> _windows = new List<WindowEntry>();

        private sealed class WindowEntry
        {
            public Window Window;
            public XboxGameBarWidget Widget;
        }

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
                if (args.Kind != ActivationKind.Protocol || widgetArgs == null || protocol == null ||
                    protocol.Uri.Scheme != "ms-gamebarwidget")
                    return;
                if (!widgetArgs.IsLaunchActivation)
                    return; // The SDK forbids recreating XboxGameBarWidget on repeat activation.

                var extensionId = widgetArgs.AppExtensionId;
                if (extensionId != "PixelOverlay" && extensionId != "PixelOverlaySettings")
                    return;

                await SettingsStore.Current.LoadAsync();

                var window = Window.Current;
                RegisterWindow(window);
                var frame = new Frame();
                frame.NavigationFailed += OnNavigationFailed;
                window.Content = frame;

                var widget = new XboxGameBarWidget(widgetArgs, window.CoreWindow, frame);
                lock (_windowsSync)
                {
                    var entry = _windows.Find(x => ReferenceEquals(x.Window, window));
                    if (entry == null)
                    {
                        entry = new WindowEntry { Window = window };
                        _windows.Add(entry);
                    }
                    entry.Widget = widget;
                }

                if (extensionId == "PixelOverlay")
                    frame.Navigate(typeof(OverlayPage), widget);
                else
                    frame.Navigate(typeof(SettingsPage), widget);

                window.Activate();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Widget activation", ex);
            }
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                await SettingsStore.Current.LoadAsync();
                var window = Window.Current;
                RegisterWindow(window);
                var frame = window.Content as Frame ?? new Frame();
                frame.NavigationFailed += OnNavigationFailed;
                window.Content = frame;
                if (frame.Content == null) frame.Navigate(typeof(SettingsPage), null);
                window.Activate();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Normal launch", ex);
            }
        }

        private void RegisterWindow(Window window)
        {
            if (window == null) return;
            lock (_windowsSync)
            {
                if (_windows.Exists(x => ReferenceEquals(x.Window, window))) return;
                _windows.Add(new WindowEntry { Window = window });
                window.Closed += OnWindowClosed;
            }
        }

        private void OnWindowClosed(object sender, CoreWindowEventArgs e)
        {
            var window = sender as Window;
            if (window == null) return;

            try
            {
                var frame = window.Content as Frame;
                var page = frame?.Content;
                var overlayPage = page as OverlayPage;
                if (overlayPage != null) overlayPage.Cleanup();
                var settingsPage = page as SettingsPage;
                if (settingsPage != null) settingsPage.Cleanup();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Window cleanup", ex);
            }
            finally
            {
                lock (_windowsSync)
                    _windows.RemoveAll(x => ReferenceEquals(x.Window, window));
            }
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
                await SettingsStore.Current.FlushAsync();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Suspend settings flush", ex);
            }
            finally
            {
                deferral.Complete();
            }
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