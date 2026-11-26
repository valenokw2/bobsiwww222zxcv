using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PixelOverlay.Models
{
    [DataContract]
    public sealed class OverlayState : BindableBase
    {
        public const int MaxLayers = 30;

        // These legacy members remain in the file format so older installed versions can
        // still read the selected layer's settings during a one-version rollback.
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
        [DataMember] private List<OverlayLayer> _layers;
        [DataMember] private string _selectedLayerId;

        public OverlayState() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) { SetDefaults(); }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context) { NormalizeLayers(true); }

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
            _layers = new List<OverlayLayer>();
            _selectedLayerId = null;
        }

        public IList<OverlayLayer> Layers => _layers;
        public string SelectedLayerId
        {
            get => _selectedLayerId;
            set
            {
                if (string.Equals(_selectedLayerId, value, StringComparison.Ordinal)) return;
                _selectedLayerId = value;
                SyncLegacyFieldsFromSelected();
                Raise();
                Raise(nameof(SelectedLayer));
            }
        }

        public OverlayLayer SelectedLayer => _layers?.FirstOrDefault(x =>
            string.Equals(x.LayerId, _selectedLayerId, StringComparison.Ordinal));

        public string SelectedImageId
        {
            get => SelectedLayer?.ImageId ?? _selectedImageId;
            set
            {
                _selectedImageId = value;
                var layer = SelectedLayer;
                if (layer != null) layer.ImageId = value;
                Raise();
            }
        }

        public double Opacity
        {
            get => SelectedLayer?.Opacity ?? _opacity;
            set
            {
                var next = ClampFinite(value, 0, 100, _opacity);
                _opacity = next;
                if (SelectedLayer != null) SelectedLayer.Opacity = next;
                Raise();
                Raise(nameof(OpacityText));
            }
        }

        public double Scale
        {
            get => SelectedLayer?.Scale ?? _scale;
            set
            {
                var next = ClampFinite(value, 10, 500, _scale);
                _scale = next;
                if (SelectedLayer != null) SelectedLayer.Scale = next;
                Raise();
                Raise(nameof(ScaleText));
            }
        }

        public double PositionX
        {
            get => SelectedLayer?.PositionX ?? _positionX;
            set
            {
                _positionX = ClampFinite(value, -100000, 100000, _positionX);
                if (SelectedLayer != null) SelectedLayer.PositionX = _positionX;
                Raise();
            }
        }

        public double PositionY
        {
            get => SelectedLayer?.PositionY ?? _positionY;
            set
            {
                _positionY = ClampFinite(value, -100000, 100000, _positionY);
                if (SelectedLayer != null) SelectedLayer.PositionY = _positionY;
                Raise();
            }
        }

        public double Rotation
        {
            get => SelectedLayer?.Rotation ?? _rotation;
            set
            {
                var next = ClampFinite(value, 0, 360, _rotation);
                _rotation = next;
                if (SelectedLayer != null) SelectedLayer.Rotation = next;
                Raise();
                Raise(nameof(RotationText));
            }
        }

        public double GifSpeed
        {
            get => SelectedLayer?.GifSpeed ?? _gifSpeed;
            set
            {
                var next = ClampFinite(value, .25, 4, _gifSpeed);
                _gifSpeed = next;
                if (SelectedLayer != null) SelectedLayer.GifSpeed = next;
                Raise();
                Raise(nameof(GifSpeedText));
            }
        }

        public bool GifPlaying
        {
            get => SelectedLayer?.GifPlaying ?? _gifPlaying;
            set
            {
                _gifPlaying = value;
                if (SelectedLayer != null) SelectedLayer.GifPlaying = value;
                Raise();
            }
        }

        public bool IgnoreGameBarOpacity
        {
            get => _ignoreGameBarOpacity;
            set => Set(ref _ignoreGameBarOpacity, value);
        }

        public bool VisibilityHotkeyEnabled
        {
            get => _visibilityHotkeyEnabled;
            set => Set(ref _visibilityHotkeyEnabled, value);
        }

        public string OpacityText => Opacity.ToString("0") + "%";
        public string ScaleText => Scale.ToString("0") + "%";
        public string RotationText => Rotation.ToString("0") + "°";
        public string GifSpeedText => GifSpeed.ToString("0.00") + "×";

        public bool AddLayer(OverlayLayer layer)
        {
            if (layer == null || _layers == null || _layers.Count >= MaxLayers) return false;
            var copy = layer.Clone();
            if (_layers.Any(x => string.Equals(x.LayerId, copy.LayerId, StringComparison.Ordinal)))
                copy.LayerId = Guid.NewGuid().ToString("N");
            copy.ZIndex = _layers.Count == 0 ? 0 : _layers.Max(x => x.ZIndex) + 1;
            _layers.Add(copy);
            SelectedLayerId = copy.LayerId;
            NormalizeZOrder();
            return true;
        }

        public bool RemoveLayer(string layerId)
        {
            if (_layers == null) return false;
            var removed = _layers.RemoveAll(x => string.Equals(x.LayerId, layerId, StringComparison.Ordinal)) > 0;
            if (!removed) return false;
            NormalizeZOrder();
            if (string.Equals(_selectedLayerId, layerId, StringComparison.Ordinal))
                SelectedLayerId = _layers.OrderByDescending(x => x.ZIndex).FirstOrDefault()?.LayerId;
            else
                SyncLegacyFieldsFromSelected();
            Raise(nameof(Layers));
            return true;
        }

        public bool MoveLayer(string layerId, int direction)
        {
            if (_layers == null || direction == 0) return false;
            var ordered = _layers.OrderBy(x => x.ZIndex).ToList();
            var index = ordered.FindIndex(x => string.Equals(x.LayerId, layerId, StringComparison.Ordinal));
            var target = index + Math.Sign(direction);
            if (index < 0 || target < 0 || target >= ordered.Count) return false;
            var currentZ = ordered[index].ZIndex;
            ordered[index].ZIndex = ordered[target].ZIndex;
            ordered[target].ZIndex = currentZ;
            NormalizeZOrder();
            Raise(nameof(Layers));
            return true;
        }

        public void RemoveLayersForImage(string imageId)
        {
            if (string.IsNullOrWhiteSpace(imageId) || _layers == null) return;
            var selected = SelectedLayerId;
            _layers.RemoveAll(x => string.Equals(x.ImageId, imageId, StringComparison.Ordinal));
            NormalizeZOrder();
            if (!_layers.Any(x => string.Equals(x.LayerId, selected, StringComparison.Ordinal)))
                SelectedLayerId = _layers.OrderByDescending(x => x.ZIndex).FirstOrDefault()?.LayerId;
            else
                SyncLegacyFieldsFromSelected();
            Raise(nameof(Layers));
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
                GifPlaying = GifPlaying,
                SelectedLayerId = SelectedLayerId,
                HasLayerSnapshot = true,
                Layers = _layers.Select(x => x.Clone()).ToList()
            };
        }

        public void Apply(OverlayProfile profile)
        {
            if (profile == null) return;
            if (profile.HasLayerSnapshot || (profile.Layers != null && profile.Layers.Count > 0))
            {
                _layers = profile.Layers.Where(x => x != null).Take(MaxLayers).Select(x => x.Clone()).ToList();
                _selectedLayerId = profile.SelectedLayerId;
                NormalizeLayers(false);
                Raise(nameof(Layers));
                Raise(nameof(SelectedLayer));
                Raise(nameof(SelectedImageId));
                RaiseLayerProperties();
                return;
            }

            // Compatibility path for profiles created before the Layers Edition.
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
            _layers = other._layers == null
                ? new List<OverlayLayer>()
                : other._layers.Where(x => x != null).Take(MaxLayers).Select(x => x.Clone()).ToList();
            _selectedLayerId = other._selectedLayerId;
            _opacity = other._opacity;
            _scale = other._scale;
            _positionX = other._positionX;
            _positionY = other._positionY;
            _rotation = other._rotation;
            _gifSpeed = other._gifSpeed;
            _gifPlaying = other._gifPlaying;
            _ignoreGameBarOpacity = other._ignoreGameBarOpacity;
            _visibilityHotkeyEnabled = other._visibilityHotkeyEnabled;
            _selectedImageId = other._selectedImageId;
            NormalizeLayers(true);
            RaiseLayerProperties();
            Raise(nameof(Layers));
            Raise(nameof(SelectedLayer));
        }

        private void NormalizeLayers(bool migrateLegacy)
        {
            if (_layers == null) _layers = new List<OverlayLayer>();
            _layers = _layers.Where(x => x != null).Take(MaxLayers).ToList();
            var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var layer in _layers)
            {
                if (string.IsNullOrWhiteSpace(layer.LayerId) || !uniqueIds.Add(layer.LayerId))
                {
                    layer.LayerId = Guid.NewGuid().ToString("N");
                    uniqueIds.Add(layer.LayerId);
                }
            }

            if (_layers.Count == 0 && migrateLegacy && !string.IsNullOrWhiteSpace(_selectedImageId))
            {
                var legacy = new OverlayLayer
                {
                    ImageId = _selectedImageId,
                    Opacity = _opacity,
                    Scale = _scale,
                    PositionX = _positionX,
                    PositionY = _positionY,
                    Rotation = _rotation,
                    GifSpeed = _gifSpeed,
                    GifPlaying = _gifPlaying,
                    IsVisible = true,
                    ZIndex = 0
                };
                _layers.Add(legacy);
            }

            NormalizeZOrder();
            if (!_layers.Any(x => string.Equals(x.LayerId, _selectedLayerId, StringComparison.Ordinal)))
                _selectedLayerId = _layers.OrderByDescending(x => x.ZIndex).FirstOrDefault()?.LayerId;
            SyncLegacyFieldsFromSelected();
        }

        private void NormalizeZOrder()
        {
            if (_layers == null) return;
            int index = 0;
            foreach (var layer in _layers.OrderBy(x => x.ZIndex).ThenBy(x => x.LayerId, StringComparer.Ordinal))
                layer.ZIndex = index++;
        }

        private void SyncLegacyFieldsFromSelected()
        {
            var layer = SelectedLayer;
            if (layer != null)
            {
                _selectedImageId = layer.ImageId;
                _opacity = layer.Opacity;
                _scale = layer.Scale;
                _positionX = layer.PositionX;
                _positionY = layer.PositionY;
                _rotation = layer.Rotation;
                _gifSpeed = layer.GifSpeed;
                _gifPlaying = layer.GifPlaying;
            }
            else
            {
                _selectedImageId = null;
                _opacity = 100;
                _scale = 100;
                _positionX = 0;
                _positionY = 0;
                _rotation = 0;
                _gifSpeed = 1;
                _gifPlaying = true;
            }
            RaiseLayerProperties();
        }

        private void RaiseLayerProperties()
        {
            Raise(nameof(SelectedImageId));
            Raise(nameof(Opacity));
            Raise(nameof(OpacityText));
            Raise(nameof(Scale));
            Raise(nameof(ScaleText));
            Raise(nameof(PositionX));
            Raise(nameof(PositionY));
            Raise(nameof(Rotation));
            Raise(nameof(RotationText));
            Raise(nameof(GifSpeed));
            Raise(nameof(GifSpeedText));
            Raise(nameof(GifPlaying));
        }

        private static double ClampFinite(double value, double minimum, double maximum, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Max(minimum, Math.Min(maximum, value));
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
        [DataMember] public bool IsFavorite { get; set; }
        public bool IsGif => string.Equals(Extension, ".gif", StringComparison.OrdinalIgnoreCase);
        public string FavoriteGlyph => IsFavorite ? "★" : "☆";

        public ImageRecord Clone()
        {
            return new ImageRecord
            {
                Id = Id,
                StoredName = StoredName,
                DisplayName = DisplayName,
                Extension = Extension,
                AddedAt = AddedAt,
                IsFavorite = IsFavorite
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
        [DataMember] public string SelectedLayerId { get; set; }
        [DataMember] public bool HasLayerSnapshot { get; set; }
        [DataMember] public List<OverlayLayer> Layers { get; set; }

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
                GifPlaying = GifPlaying,
                SelectedLayerId = SelectedLayerId,
                HasLayerSnapshot = HasLayerSnapshot,
                Layers = Layers == null ? null : Layers.Where(x => x != null).Take(OverlayState.MaxLayers).Select(x => x.Clone()).ToList()
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
