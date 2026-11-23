using Microsoft.Extensions.Logging;

namespace mstscX
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            builder.Services.AddSingleton<IPreferences>(Preferences.Default);
            builder.Services.AddSingleton<Services.ServerProfileStore>();
            builder.Services.AddTransient<ViewModels.MainViewModel>();
            builder.Services.AddTransient<MainPage>();
            builder.Services.AddTransient<AppShell>();
            builder.ConfigureMauiHandlers(handlers =>
            {
                handlers.AddHandler<WinUI.TitleMenuBar, WinUI.TitleMenuBarHandler>();
                handlers.AddHandler<WinUI.TitleNavigationButton, WinUI.TitleNavigationButtonHandler>();
                handlers.AddHandler<WinUI.TitleFullScreenButton, WinUI.TitleFullScreenButtonHandler>();
                handlers.AddHandler<WinUI.ServerPanelDivider, WinUI.ServerPanelDividerHandler>();
            });
            return builder.Build();
        }
    }
}
