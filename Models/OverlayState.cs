using System;
using System.Runtime.Serialization;

namespace PixelOverlay.Models
{
    [DataContract]
    public sealed class OverlayState : BindableBase
    {
        [DataMember] private string _selectedImageId;
        [DataMember] private double _opacity;
        [DataMember] private double _scale;
        [DataMember] private double _positionX;
        [DataMember] private double _positionY;
        [DataMember] private double _rotation;
        [DataMember] private double _gifSpeed;
        [DataMember] private bool _gifPlaying;
        [DataMember] private bool _ignoreGameBarOpacity;
        [DataMember] private bool _visibilityHotkeyEnabled;

        public OverlayState() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) { SetDefaults(); }

        private void SetDefaults()
        {
            _opacity = 100;
            _scale = 100;
            _positionX = 0;
            _positionY = 0;
            _rotation = 0;
            _gifSpeed = 1;
            _gifPlaying = true;
            _ignoreGameBarOpacity = false;
            _visibilityHotkeyEnabled = false;
        }

        public string SelectedImageId { get => _selectedImageId; set => Set(ref _selectedImageId, value); }
        public double Opacity { get => _opacity; set { if (Set(ref _opacity, ClampFinite(value, 0, 100, _opacity))) Raise(nameof(OpacityText)); } }
        public double Scale { get => _scale; set { if (Set(ref _scale, ClampFinite(value, 10, 500, _scale))) Raise(nameof(ScaleText)); } }
        public double PositionX { get => _positionX; set => Set(ref _positionX, ClampFinite(value, -100000, 100000, _positionX)); }
        public double PositionY { get => _positionY; set => Set(ref _positionY, ClampFinite(value, -100000, 100000, _positionY)); }
        public double Rotation { get => _rotation; set { if (Set(ref _rotation, ClampFinite(value, 0, 360, _rotation))) Raise(nameof(RotationText)); } }
        public double GifSpeed { get => _gifSpeed; set { if (Set(ref _gifSpeed, ClampFinite(value, .25, 4, _gifSpeed))) Raise(nameof(GifSpeedText)); } }
        public bool GifPlaying { get => _gifPlaying; set => Set(ref _gifPlaying, value); }
        public bool IgnoreGameBarOpacity { get => _ignoreGameBarOpacity; set => Set(ref _ignoreGameBarOpacity, value); }
        public bool VisibilityHotkeyEnabled { get => _visibilityHotkeyEnabled; set => Set(ref _visibilityHotkeyEnabled, value); }

        public string OpacityText => Opacity.ToString("0") + "%";
        public string ScaleText => Scale.ToString("0") + "%";
        public string RotationText => Rotation.ToString("0") + "°";
        public string GifSpeedText => GifSpeed.ToString("0.00") + "×";

        private static double ClampFinite(double value, double minimum, double maximum, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public void ResetTransform()
        {
            Opacity = 100;
            Scale = 100;
            PositionX = 0;
            PositionY = 0;
            Rotation = 0;
        }

        public void Center()
        {
            PositionX = 0;
            PositionY = 0;
        }

        public OverlayProfile Snapshot(string name)
        {
            return new OverlayProfile
            {
                Name = name,
                Opacity = Opacity,
                Scale = Scale,
                PositionX = PositionX,
                PositionY = PositionY,
                Rotation = Rotation,
                GifSpeed = GifSpeed,
                GifPlaying = GifPlaying
            };
        }

        public void Apply(OverlayProfile profile)
        {
            if (profile == null) return;
            Opacity = profile.Opacity;
            Scale = profile.Scale;
            PositionX = profile.PositionX;
            PositionY = profile.PositionY;
            Rotation = profile.Rotation;
            GifSpeed = profile.GifSpeed;
            GifPlaying = profile.GifPlaying;
        }

        public OverlayState Clone()
        {
            var clone = new OverlayState();
            clone.CopyFrom(this);
            return clone;
        }

        public void CopyFrom(OverlayState other)
        {
            if (other == null) other = new OverlayState();
            SelectedImageId = other.SelectedImageId;
            Opacity = other.Opacity;
            Scale = other.Scale;
            PositionX = other.PositionX;
            PositionY = other.PositionY;
            Rotation = other.Rotation;
            GifSpeed = other.GifSpeed;
            GifPlaying = other.GifPlaying;
            IgnoreGameBarOpacity = other.IgnoreGameBarOpacity;
            VisibilityHotkeyEnabled = other.VisibilityHotkeyEnabled;
        }
    }

    [DataContract]
    public sealed class ImageRecord
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string StoredName { get; set; }
        [DataMember] public string DisplayName { get; set; }
        [DataMember] public string Extension { get; set; }
        [DataMember] public DateTimeOffset AddedAt { get; set; }
        public bool IsGif => string.Equals(Extension, ".gif", StringComparison.OrdinalIgnoreCase);

        public ImageRecord Clone()
        {
            return new ImageRecord
            {
                Id = Id,
                StoredName = StoredName,
                DisplayName = DisplayName,
                Extension = Extension,
                AddedAt = AddedAt
            };
        }
    }

    [DataContract]
    public sealed class OverlayProfile
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public double Opacity { get; set; }
        [DataMember] public double Scale { get; set; }
        [DataMember] public double PositionX { get; set; }
        [DataMember] public double PositionY { get; set; }
        [DataMember] public double Rotation { get; set; }
        [DataMember] public double GifSpeed { get; set; }
        [DataMember] public bool GifPlaying { get; set; }

        public OverlayProfile Clone()
        {
            return new OverlayProfile
            {
                Name = Name,
                Opacity = Opacity,
                Scale = Scale,
                PositionX = PositionX,
                PositionY = PositionY,
                Rotation = Rotation,
                GifSpeed = GifSpeed,
                GifPlaying = GifPlaying
            };
        }
    }

    [DataContract]
    public sealed class PersistedSettings
    {
        [DataMember] public OverlayState State { get; set; } = new OverlayState();
        [DataMember] public System.Collections.Generic.List<ImageRecord> Library { get; set; } = new System.Collections.Generic.List<ImageRecord>();
        [DataMember] public System.Collections.Generic.List<OverlayProfile> Profiles { get; set; } = new System.Collections.Generic.List<OverlayProfile>();
    }
}
