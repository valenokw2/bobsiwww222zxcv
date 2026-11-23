using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using PixelOverlay.Diagnostics;
using PixelOverlay.Services;
using PixelOverlay.Views;

namespace PixelOverlay
{
    sealed partial class App : Application
    {
        // Game Bar requires this object to stay alive for the lifetime of the widget's CoreWindow.
        private readonly List<XboxGameBarWidget> _widgets = new List<XboxGameBarWidget>();

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            UnhandledException += OnUnhandledException;
            _ = WidgetDiagnostics.LogAsync("Application constructed.");
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
            var protocol = args as IProtocolActivatedEventArgs;
            await WidgetDiagnostics.LogAsync(
                "Activation received. Kind=" + args.Kind +
                "; IsGameBarWidgetArgs=" + (widgetArgs != null) +
                "; Protocol=" + (protocol == null ? "<null>" : protocol.Uri.ToString()));

            if (args.Kind != ActivationKind.Protocol || widgetArgs == null || protocol == null ||
                protocol.Uri.Scheme != "ms-gamebarwidget")
            {
                await WidgetDiagnostics.LogAsync("Activation ignored: not a supported Game Bar protocol activation.");
                return;
            }

            await WidgetDiagnostics.LogAsync(
                "Game Bar activation. ExtensionId=" + widgetArgs.AppExtensionId +
                "; IsLaunchActivation=" + widgetArgs.IsLaunchActivation);

            if (!widgetArgs.IsLaunchActivation)
            {
                await WidgetDiagnostics.LogAsync("Activation ignored: SDK reports this is not a launch activation.");
                return; // The SDK forbids recreating XboxGameBarWidget on repeat activation.
            }

            try
            {
                await SettingsStore.Current.LoadAsync();
                var frame = new Frame();
                frame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = frame;

                await WidgetDiagnostics.LogAsync(
                    "Creating XboxGameBarWidget. WindowBounds=" + Window.Current.Bounds);
                var widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, frame);
                _widgets.Add(widget);

                if (widgetArgs.AppExtensionId == "PixelOverlay")
                {
                    await WidgetDiagnostics.LogAsync("Navigating to OverlayPage.");
                    frame.Navigate(typeof(OverlayPage), widget);
                }
                else if (widgetArgs.AppExtensionId == "PixelOverlaySettings")
                {
                    await WidgetDiagnostics.LogAsync("Navigating to SettingsPage.");
                    frame.Navigate(typeof(SettingsPage), widget);
                }
                else
                {
                    await WidgetDiagnostics.LogAsync("Unknown extension ID; navigation skipped.");
                    return;
                }

                Window.Current.Activate();
                await WidgetDiagnostics.LogAsync("Window activated. Content=" +
                    (frame.Content == null ? "<null>" : frame.Content.GetType().FullName) +
                    "; Bounds=" + Window.Current.Bounds);
            }
            catch (Exception ex)
            {
                await WidgetDiagnostics.LogAsync("Fatal exception during Game Bar activation.", ex);
                throw;
            }
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            await WidgetDiagnostics.LogAsync("Normal app launch. PreviousExecutionState=" + e.PreviousExecutionState);
            try
            {
                // A normal launch is useful while designing the settings UI; Game Bar activation is used in production.
                await SettingsStore.Current.LoadAsync();
                var frame = Window.Current.Content as Frame ?? new Frame();
                Window.Current.Content = frame;
                if (frame.Content == null) frame.Navigate(typeof(SettingsPage), null);
                Window.Current.Activate();
                await WidgetDiagnostics.LogAsync("Normal launch window activated. Bounds=" + Window.Current.Bounds);
            }
            catch (Exception ex)
            {
                await WidgetDiagnostics.LogAsync("Exception during normal launch.", ex);
                throw;
            }
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                await WidgetDiagnostics.LogAsync("Application suspending.");
                await SettingsStore.Current.FlushAsync();
            }
            catch (Exception ex) { await WidgetDiagnostics.LogAsync("Exception while suspending.", ex); }
            finally { deferral.Complete(); }
        }

        private async void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            await WidgetDiagnostics.LogAsync("Unhandled XAML exception. Message=" + e.Message, e.Exception);
        }

        private static async void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            await WidgetDiagnostics.LogAsync("Frame navigation failed. Target=" + e.SourcePageType.FullName, e.Exception);
        }
    }
}
