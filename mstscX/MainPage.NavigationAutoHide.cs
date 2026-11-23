namespace mstscX;

public partial class MainPage
{
    private const string AutoHideNavigationKey = "View.AutoHideNavigation";
    private bool navigationAutoHide, navigationHidden;
    private bool navigationPointerInside;
    private IDispatcherTimer? navigationHideTimer;

    private void InitializeNavigationAutoHide()
    {
        navigationAutoHide = Preferences.Default.Get(AutoHideNavigationKey, false);
        AutoHideNavigationSwitch.IsToggled = navigationAutoHide;
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) =>
        {
            navigationPointerInside = true;
            navigationHideTimer?.Stop();
            SetNavigationHidden(false);
        };
        pointer.PointerExited += (_, _) => { navigationPointerInside = false; ScheduleNavigationHide(); };
        NavigationHost.GestureRecognizers.Add(pointer);
        Loaded += (_, _) => ScheduleNavigationHide();
        Unloaded += (_, _) =>
        {
            navigationHideTimer?.Stop();
            this.AbortAnimation("NavigationSlide");
        };
    }

    private void OnAutoHideNavigationToggled(object? sender, ToggledEventArgs e)
    {
        navigationAutoHide = e.Value;
        Preferences.Default.Set(AutoHideNavigationKey, e.Value);
        if (!e.Value) SetNavigationHidden(false);
        ScheduleNavigationHide();
    }

    private void ScheduleNavigationHide()
    {
        navigationHideTimer?.Stop();
        if (!navigationAutoHide || navigationPointerInside) return;
        if (navigationHideTimer is null)
        {
            navigationHideTimer = Dispatcher.CreateTimer();
            navigationHideTimer.Interval = TimeSpan.FromSeconds(3);
            navigationHideTimer.IsRepeating = false;
            navigationHideTimer.Tick += (_, _) => SetNavigationHidden(true);
        }
        navigationHideTimer.Start();
    }

    private void UpdateNavigationAutoHideLayout()
    {
        this.AbortAnimation("NavigationSlide");
        double width = NavigationPane.WidthRequest;
        NavigationHost.WidthRequest = navigationHidden ? 3 : width;
        NavigationPane.TranslationX = navigationHidden ? 3 - width : 0;
    }

    private void SetNavigationHidden(bool hidden)
    {
        hidden &= navigationAutoHide;
        if (navigationHidden == hidden) return;
        navigationHidden = hidden;
        this.AbortAnimation("NavigationSlide");
        double width = NavigationPane.WidthRequest;
        double from = NavigationHost.WidthRequest;
        double to = hidden ? 3 : width;
        if (!systemColors.AnimationsEnabled)
        {
            UpdateNavigationAutoHideLayout();
            return;
        }
        new Animation(value =>
        {
            NavigationHost.WidthRequest = value;
            NavigationPane.TranslationX = value - width;
        }, from, to).Commit(this, "NavigationSlide", 16, 170,
            hidden ? new Easing(t => t * t) : Easing.CubicOut);
    }
}
