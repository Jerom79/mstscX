using Microsoft.Extensions.DependencyInjection;

namespace mstscX
{
    public partial class App : Application
    {
        private readonly IServiceProvider services;

        public App(IServiceProvider services)
        {
            InitializeComponent();
            Services.ThemeSettings.Apply(this, Services.ThemeSettings.SelectedIndex);
            this.services = services;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var shell = services.GetRequiredService<AppShell>();
            var window = new Window(shell) { Title = "mstscX", TitleBar = shell.CreateTitleBar() };
            shell.WatchMainWindowClosing(window);
            return window;
        }
    }
}
