using Microsoft.Maui.Handlers;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using NativeContentControl = Microsoft.UI.Xaml.Controls.ContentControl;

namespace mstscX.WinUI;

public sealed class ServerPanelDivider : View
{
    public event EventHandler? DragStarted;
    public event EventHandler<double>? DragDelta;
    public event EventHandler? DragCompleted;

    internal void StartDrag() => DragStarted?.Invoke(this, EventArgs.Empty);
    internal void MoveDrag(double delta) => DragDelta?.Invoke(this, delta);
    internal void CompleteDrag() => DragCompleted?.Invoke(this, EventArgs.Empty);
}

internal sealed class ServerPanelDividerHandler() : ViewHandler<ServerPanelDivider, NativeContentControl>(DividerMapper)
{
    private static readonly PropertyMapper<ServerPanelDivider, ServerPanelDividerHandler> DividerMapper = new(ViewMapper);

    protected override NativeContentControl CreatePlatformView() => new ResizeCursorHost
    {
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
        VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
        Content = new Thumb
        {
            Template = (Microsoft.UI.Xaml.Controls.ControlTemplate)XamlReader.Load("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Thumb">
                    <Grid Background="Transparent" />
                </ControlTemplate>
                """)
        },
        Template = (Microsoft.UI.Xaml.Controls.ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             TargetType="ContentControl">
                <Grid>
                    <Border Width="1" HorizontalAlignment="Center" Background="{TemplateBinding Background}"
                            IsHitTestVisible="False" />
                    <ContentPresenter Content="{TemplateBinding Content}" HorizontalAlignment="Stretch" VerticalAlignment="Stretch" />
                </Grid>
            </ControlTemplate>
            """)
    };

    private sealed class ResizeCursorHost : NativeContentControl
    {
        public ResizeCursorHost()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }
    }

    protected override void ConnectHandler(NativeContentControl platformView)
    {
        base.ConnectHandler(platformView);
        var thumb = (Thumb)platformView.Content;
        thumb.DragStarted += OnDragStarted;
        thumb.DragDelta += OnDragDelta;
        thumb.DragCompleted += OnDragCompleted;
    }

    protected override void DisconnectHandler(NativeContentControl platformView)
    {
        var thumb = (Thumb)platformView.Content;
        thumb.DragStarted -= OnDragStarted;
        thumb.DragDelta -= OnDragDelta;
        thumb.DragCompleted -= OnDragCompleted;
        base.DisconnectHandler(platformView);
    }

    private void OnDragStarted(object sender, DragStartedEventArgs e) => VirtualView.StartDrag();
    private void OnDragDelta(object sender, DragDeltaEventArgs e) => VirtualView.MoveDrag(e.HorizontalChange);
    private void OnDragCompleted(object sender, DragCompletedEventArgs e) => VirtualView.CompleteDrag();
}
