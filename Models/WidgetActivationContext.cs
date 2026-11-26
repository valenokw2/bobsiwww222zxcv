using Microsoft.Gaming.XboxGameBar;

namespace PixelOverlay.Models
{
    public sealed class WidgetActivationContext
    {
        public XboxGameBarWidget Widget { get; private set; }
        public string WindowId { get; private set; }

        public WidgetActivationContext(XboxGameBarWidget widget, string windowId)
        {
            Widget = widget;
            WindowId = WindowSlotIds.Normalize(windowId);
        }
    }
}
