using Wpf.Ui.Tray;

namespace AudioBit.App.Infrastructure;

internal sealed class AudioBitNotifyIconService : NotifyIconService
{
    public event EventHandler? LeftDoubleClickReceived;

    protected override void OnLeftDoubleClick()
    {
        base.OnLeftDoubleClick();
        LeftDoubleClickReceived?.Invoke(this, EventArgs.Empty);
    }
}
