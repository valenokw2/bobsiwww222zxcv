using System;
using System.Runtime.Serialization;
using PixelOverlay.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace PixelOverlay.Models
{
    [DataContract]
    public sealed class OverlayLayer : BindableBase
    {
        [DataMember] private string _layerId;
        [DataMember] private string _imageId;
        [DataMember] private bool _isVisible;
        [DataMember] private int _zIndex;
        [DataMember] private double _width;
        [DataMember] private double _height;
        [DataMember] private double _opacity;
        [DataMember] private double _scale;
        [DataMember] private double _positionX;
        [DataMember] private double _positionY;
        [DataMember] private double _rotation;
        [DataMember] private double _gifSpeed;
        [DataMember] private bool _gifPlaying;

        // Runtime-only values. DataContractJsonSerializer ignores members without DataMember.
        private ImageSource _imageSource;
        private string _displayName;
        private double _canvasLeft;
        private double _canvasTop;
        private double _surfaceWidth;
        private double _surfaceHeight;

        public OverlayLayer() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) { SetDefaults(); }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context) { Normalize(); }

        private void SetDefaults()
        {
            _layerId = Guid.NewGuid().ToString("N");
            _isVisible = true;
            _zIndex = 0;
            _width = 0;
            _height = 0;
            _opacity = 100;
            _scale = 100;
            _positionX = 0;
            _positionY = 0;
            _rotation = 0;
            _gifSpeed = 1;
            _gifPlaying = true;
        }

        public string LayerId { get => _layerId; set => Set(ref _layerId, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value); }
        public string ImageId { get => _imageId; set => Set(ref _imageId, value); }
        public bool IsVisible { get => _isVisible; set { if (Set(ref _isVisible, value)) Raise(nameof(LayerVisibility)); } }
        public int ZIndex { get => _zIndex; set => Set(ref _zIndex, Math.Max(0, Math.Min(29, value))); }
        public double Width { get => _width; set { if (Set(ref _width, Clamp(value, 0, 8192, 0))) { Raise(nameof(TransformCenterX)); Reposition(_surfaceWidth / 2d + PositionX, _surfaceHeight / 2d + PositionY); } } }
        public double Height { get => _height; set { if (Set(ref _height, Clamp(value, 0, 8192, 0))) { Raise(nameof(TransformCenterY)); Reposition(_surfaceWidth / 2d + PositionX, _surfaceHeight / 2d + PositionY); } } }
        public double Opacity { get => _opacity; set { if (Set(ref _opacity, Clamp(value, 0, 100, 100))) Raise(nameof(OpacityFactor)); } }
        public double Scale { get => _scale; set { if (Set(ref _scale, Clamp(value, 10, 500, 100))) Raise(nameof(ScaleFactor)); } }
        public double PositionX { get => _positionX; set { if (Set(ref _positionX, Clamp(value, -100000, 100000, 0))) Raise(nameof(PositionX)); } }
        public double PositionY { get => _positionY; set { if (Set(ref _positionY, Clamp(value, -100000, 100000, 0))) Raise(nameof(PositionY)); } }
        public double Rotation { get => _rotation; set => Set(ref _rotation, Clamp(value, 0, 360, 0)); }
        public double GifSpeed { get => _gifSpeed; set => Set(ref _gifSpeed, Clamp(value, .25, 4, 1)); }
        public bool GifPlaying { get => _gifPlaying; set => Set(ref _gifPlaying, value); }

        public ImageSource ImageSource
        {
            get => _imageSource;
            set => Set(ref _imageSource, value);
        }

        public string DisplayName
        {
            get => _displayName;
            set => Set(ref _displayName, value);
        }

        public Visibility LayerVisibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;
        public double CanvasLeft => _canvasLeft;
        public double CanvasTop => _canvasTop;
        public double TransformCenterX => Width / 2d;
        public double TransformCenterY => Height / 2d;
        public double OpacityFactor => Opacity / 100d;
        public double ScaleFactor => Scale / 100d;

        public void UpdateCanvasPosition(double surfaceWidth, double surfaceHeight)
        {
            _surfaceWidth = Math.Max(0, surfaceWidth);
            _surfaceHeight = Math.Max(0, surfaceHeight);
            Reposition(_surfaceWidth / 2d + PositionX, _surfaceHeight / 2d + PositionY);
        }

        private void Reposition(double centerX, double centerY)
        {
            var nextLeft = centerX - Width / 2d;
            var nextTop = centerY - Height / 2d;
            if (Math.Abs(_canvasLeft - nextLeft) > .01)
            {
                _canvasLeft = nextLeft;
                Raise(nameof(CanvasLeft));
            }
            if (Math.Abs(_canvasTop - nextTop) > .01)
            {
                _canvasTop = nextTop;
                Raise(nameof(CanvasTop));
            }
        }

        public void CopyFrom(OverlayLayer other)
        {
            if (other == null) return;
            LayerId = other.LayerId;
            ImageId = other.ImageId;
            IsVisible = other.IsVisible;
            ZIndex = other.ZIndex;
            Width = other.Width;
            Height = other.Height;
            Opacity = other.Opacity;
            Scale = other.Scale;
            PositionX = other.PositionX;
            PositionY = other.PositionY;
            Rotation = other.Rotation;
            GifSpeed = other.GifSpeed;
            GifPlaying = other.GifPlaying;
        }

        public OverlayLayer Clone()
        {
            var clone = new OverlayLayer();
            clone.CopyFrom(this);
            return clone;
        }

        private void Normalize()
        {
            if (string.IsNullOrWhiteSpace(_layerId)) _layerId = Guid.NewGuid().ToString("N");
            _zIndex = Math.Max(0, Math.Min(29, _zIndex));
            _width = Clamp(_width, 0, 8192, 0);
            _height = Clamp(_height, 0, 8192, 0);
            _opacity = Clamp(_opacity, 0, 100, 100);
            _scale = Clamp(_scale, 10, 500, 100);
            _positionX = Clamp(_positionX, -100000, 100000, 0);
            _positionY = Clamp(_positionY, -100000, 100000, 0);
            _rotation = Clamp(_rotation, 0, 360, 0);
            _gifSpeed = Clamp(_gifSpeed, .25, 4, 1);
        }

        private static double Clamp(double value, double minimum, double maximum, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
