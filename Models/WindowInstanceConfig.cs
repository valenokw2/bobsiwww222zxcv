using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PixelOverlay.Models
{
    public static class WindowSlotIds
    {
        public const int MaxOverlayWindows = 32;
        public const string FirstWindowId = "overlay-01";

        public static string ForIndex(int index)
        {
            if (index < 1 || index > MaxOverlayWindows) throw new ArgumentOutOfRangeException(nameof(index));
            return "overlay-" + index.ToString("00");
        }

        public static int GetIndex(string windowId)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !windowId.StartsWith("overlay-", StringComparison.Ordinal))
                return 0;
            int index;
            return int.TryParse(windowId.Substring(8), out index) && index >= 1 && index <= MaxOverlayWindows ? index : 0;
        }

        public static string Normalize(string windowId)
        {
            int index = GetIndex(windowId);
            return index == 0 ? FirstWindowId : ForIndex(index);
        }

        public static string GetExtensionId(string windowId)
        {
            int index = GetIndex(windowId);
            if (index == 0) index = 1;
            return index == 1 ? "PixelOverlay" : "PixelOverlay" + index.ToString("00");
        }

        public static bool TryGetWindowId(string appExtensionId, out string windowId)
        {
            windowId = null;
            if (string.Equals(appExtensionId, "PixelOverlay", StringComparison.Ordinal))
            {
                windowId = FirstWindowId;
                return true;
            }
            if (string.IsNullOrWhiteSpace(appExtensionId) ||
                !appExtensionId.StartsWith("PixelOverlay", StringComparison.Ordinal))
                return false;

            var suffix = appExtensionId.Substring("PixelOverlay".Length);
            int index;
            if (suffix.Length != 2 || !int.TryParse(suffix, out index) || index < 2 || index > MaxOverlayWindows)
                return false;
            windowId = ForIndex(index);
            return true;
        }

        public static bool IsValid(string windowId) => GetIndex(windowId) > 0;
    }

    [DataContract]
    public sealed class WindowInstanceConfig
    {
        [DataMember(Order = 1)] public string SelectedImageId { get; set; }
        [DataMember(Order = 2)] public double Opacity { get; set; }
        [DataMember(Order = 3)] public bool IgnoreGameBarOpacity { get; set; }
        [DataMember(Order = 4)] public double Scale { get; set; }
        [DataMember(Order = 5)] public double PositionX { get; set; }
        [DataMember(Order = 6)] public double PositionY { get; set; }
        [DataMember(Order = 7)] public double Rotation { get; set; }
        [DataMember(Order = 8)] public double GifSpeed { get; set; }
        [DataMember(Order = 9)] public List<string> FavoriteImageIds { get; set; }
        [DataMember(Order = 10)] public bool IsHidden { get; set; }
        [DataMember(Order = 11)] public bool GifPlaying { get; set; }
        [DataMember(Order = 12)] public bool VisibilityHotkeyEnabled { get; set; }
        [DataMember(Order = 13)] public bool ClickThroughEnabled { get; set; }

        public WindowInstanceConfig() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) { SetDefaults(); }

        private void SetDefaults()
        {
            Opacity = 100;
            Scale = 100;
            PositionX = 0;
            PositionY = 0;
            Rotation = 0;
            GifSpeed = 1;
            FavoriteImageIds = new List<string>();
            GifPlaying = true;
            IgnoreGameBarOpacity = false;
            IsHidden = false;
            VisibilityHotkeyEnabled = false;
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            if (FavoriteImageIds == null) FavoriteImageIds = new List<string>();
            Opacity = Clamp(Opacity, 0, 100, 100);
            Scale = Clamp(Scale, 10, 500, 100);
            PositionX = Clamp(PositionX, -100000, 100000, 0);
            PositionY = Clamp(PositionY, -100000, 100000, 0);
            Rotation = Clamp(Rotation, 0, 360, 0);
            GifSpeed = Clamp(GifSpeed, .25, 4, 1);
            FavoriteImageIds = FavoriteImageIds.Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).ToList();
        }

        private static double Clamp(double value, double minimum, double maximum, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public WindowInstanceConfig Clone()
        {
            return new WindowInstanceConfig
            {
                SelectedImageId = SelectedImageId,
                Opacity = Opacity,
                IgnoreGameBarOpacity = IgnoreGameBarOpacity,
                Scale = Scale,
                PositionX = PositionX,
                PositionY = PositionY,
                Rotation = Rotation,
                GifSpeed = GifSpeed,
                FavoriteImageIds = FavoriteImageIds == null ? new List<string>() : new List<string>(FavoriteImageIds),
                IsHidden = IsHidden,
                GifPlaying = GifPlaying,
                VisibilityHotkeyEnabled = VisibilityHotkeyEnabled,
                ClickThroughEnabled = ClickThroughEnabled
            };
        }

        public OverlayState ToOverlayState()
        {
            return new OverlayState
            {
                SelectedImageId = SelectedImageId,
                Opacity = Opacity,
                IgnoreGameBarOpacity = IgnoreGameBarOpacity,
                Scale = Scale,
                PositionX = PositionX,
                PositionY = PositionY,
                Rotation = Rotation,
                GifSpeed = GifSpeed,
                GifPlaying = GifPlaying,
                VisibilityHotkeyEnabled = VisibilityHotkeyEnabled
            };
        }

        public void Apply(OverlayState state)
        {
            if (state == null) return;
            SelectedImageId = state.SelectedImageId;
            Opacity = state.Opacity;
            IgnoreGameBarOpacity = state.IgnoreGameBarOpacity;
            Scale = state.Scale;
            PositionX = state.PositionX;
            PositionY = state.PositionY;
            Rotation = state.Rotation;
            GifSpeed = state.GifSpeed;
            GifPlaying = state.GifPlaying;
            VisibilityHotkeyEnabled = state.VisibilityHotkeyEnabled;
        }

        public static WindowInstanceConfig FromOverlayState(OverlayState state)
        {
            var config = new WindowInstanceConfig();
            config.Apply(state ?? new OverlayState());
            return config;
        }
    }
}
