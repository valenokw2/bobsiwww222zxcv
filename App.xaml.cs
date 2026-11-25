using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using PixelOverlay.Services;
using PixelOverlay.Views;

namespace PixelOverlay
{
    sealed partial class App : Application
    {
        // Game Bar requires this object to stay alive for the lifetime of the widget's CoreWindow.
        private readonly List<XboxGameBarWidget> _widgets = new List<XboxGameBarWidget>();
        public App() { InitializeComponent(); UnhandledException += OnUnhandledException; Suspending += OnSuspending; }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            try
            {
                var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
                var protocol = args as IProtocolActivatedEventArgs;
                if (args.Kind != ActivationKind.Protocol || widgetArgs == null || protocol == null || protocol.Uri.Scheme != "ms-gamebarwidget") return;
                if (!widgetArgs.IsLaunchActivation) return; // The SDK forbids recreating XboxGameBarWidget on repeat activation.

                await SettingsStore.Current.LoadAsync();
                var frame = new Frame(); frame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = frame;
                var widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, frame);
                _widgets.Add(widget);
                if (widgetArgs.AppExtensionId == "PixelOverlay") frame.Navigate(typeof(OverlayPage), widget);
                else if (widgetArgs.AppExtensionId == "PixelOverlaySettings") frame.Navigate(typeof(SettingsPage), widget);
                else return;
                Window.Current.Activate();
            }
            catch (Exception ex) { AppDiagnostics.Log("Widget activation", ex); }
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                // A normal launch is useful while designing the settings UI; Game Bar activation is used in production.
                await SettingsStore.Current.LoadAsync();
                var frame = Window.Current.Content as Frame ?? new Frame();
                frame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = frame;
                if (frame.Content == null) frame.Navigate(typeof(SettingsPage), null);
                Window.Current.Activate();
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
            try { await SettingsStore.Current.FlushAsync(); }
            catch (Exception ex) { AppDiagnostics.Log("Suspend settings flush", ex); }
            finally { deferral.Complete(); }
        }

        private static void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            AppDiagnostics.Log("Navigation failed", new InvalidOperationException("Failed to load " + e.SourcePageType.FullName));
            Window.Current.Content = new TextBlock { Text = "Pixel Overlay could not open this page. See pixel-overlay-diagnostics.log." };
        }
    }
}
