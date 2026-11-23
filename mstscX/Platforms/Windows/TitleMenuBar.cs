using Microsoft.Maui.Handlers;
using MenuBar = Microsoft.UI.Xaml.Controls.MenuBar;
using MenuBarItem = Microsoft.UI.Xaml.Controls.MenuBarItem;

namespace mstscX.WinUI;

internal sealed class TitleNavigationButton(Action clicked, Func<bool> collapsed) : View
{
    internal Action Clicked { get; } = clicked;
    internal Func<bool> Collapsed { get; } = collapsed;

    internal static Microsoft.UI.Xaml.Controls.Button Create(Action clicked, Func<bool> collapsed)
    {
        var button = new Microsoft.UI.Xaml.Controls.Button
        {
            Width = 36, Height = 32,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Content = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE700", FontSize = 18 }
        };
        void UpdateHint()
        {
            string hint = collapsed() ? global::mstscX.Services.L.T("Развернуть боковое меню") : global::mstscX.Services.L.T("Свернуть боковое меню");
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, hint);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, hint);
        }
        button.Click += (_, _) => { clicked(); UpdateHint(); };
        button.PointerEntered += (_, _) => UpdateHint();
        UpdateHint();
        return button;
    }
}

internal sealed class TitleNavigationButtonHandler() : ViewHandler<TitleNavigationButton, Microsoft.UI.Xaml.Controls.Button>(NavigationMapper)
{
    private static readonly PropertyMapper<TitleNavigationButton, TitleNavigationButtonHandler> NavigationMapper = new(ViewMapper);
    protected override Microsoft.UI.Xaml.Controls.Button CreatePlatformView() =>
        TitleNavigationButton.Create(VirtualView.Clicked, VirtualView.Collapsed);
}

internal sealed class TitleMenuBar(params MenuBarItem[] items) : View
{
    internal IReadOnlyList<MenuBarItem> Items { get; } = items;
}

internal sealed class TitleMenuBarHandler() : ViewHandler<TitleMenuBar, MenuBar>(MenuMapper)
{
    private static readonly PropertyMapper<TitleMenuBar, TitleMenuBarHandler> MenuMapper = new(ViewMapper);

    protected override MenuBar CreatePlatformView()
    {
        var menu = new MenuBar
        {
            Height = 38,
            MinHeight = 0,
            VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Padding = new Microsoft.UI.Xaml.Thickness(0)
        };
        foreach (var item in VirtualView.Items)
        {
            item.Height = 32;
            item.MinHeight = 0;
            item.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
            item.VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
            item.Padding = new Microsoft.UI.Xaml.Thickness(12, 0, 12, 0);
            menu.Items.Add(item);
        }
        return menu;
    }
}

internal sealed class TitleFullScreenButton(Action clicked) : View
{
    internal Action Clicked { get; } = clicked;
}

internal sealed class TitleFullScreenButtonHandler() : ViewHandler<TitleFullScreenButton, Microsoft.UI.Xaml.Controls.Button>(ButtonMapper)
{
    private static readonly PropertyMapper<TitleFullScreenButton, TitleFullScreenButtonHandler> ButtonMapper = new(ViewMapper);
    protected override Microsoft.UI.Xaml.Controls.Button CreatePlatformView()
    {
        var button = new Microsoft.UI.Xaml.Controls.Button
        {
            Width = 46, Height = 32,
            Content = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE740", FontSize = 16 }
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, global::mstscX.Services.L.T("Полноэкранный режим"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, global::mstscX.Services.L.T("Полноэкранный режим (F11)"));
        button.Click += (_, _) => VirtualView.Clicked();
        return button;
    }
}
