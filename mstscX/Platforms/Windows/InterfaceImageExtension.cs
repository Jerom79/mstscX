namespace mstscX.WinUI;

[AcceptEmptyServiceProvider]
public sealed class InterfaceImageExtension : IMarkupExtension<ImageSource>
{
    private static readonly int[] Scales = [100, 125, 150, 200, 400];

    public string Name { get; set; } = "";

    public ImageSource ProvideValue(IServiceProvider serviceProvider) => GetSource(Name);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);

    internal static ImageSource GetSource(string name)
    {
        var displayScale = DeviceDisplay.Current.MainDisplayInfo.Density * 100;
        var scale = Scales.FirstOrDefault(candidate => candidate >= displayScale, Scales[^1]);
        var path = Path.Combine(AppContext.BaseDirectory, "Images", $"{name}.scale-{scale}.png");
        return ImageSource.FromStream(() => File.OpenRead(path));
    }
}
