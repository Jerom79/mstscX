namespace mstscX
{
    public partial class AppShell : Shell
    {
        public AppShell(MainPage page)
        {
            InitializeComponent();
            HomeContent.Content = page;
        }

        internal TitleBar CreateTitleBar() => ((MainPage)HomeContent.Content).CreateTitleBar();
        internal void WatchMainWindowClosing(Window window) => ((MainPage)HomeContent.Content).WatchMainWindowClosing(window);
    }
}
