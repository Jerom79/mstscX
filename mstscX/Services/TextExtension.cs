namespace mstscX.Services;

[AcceptEmptyServiceProvider]
public sealed class TextExtension : IMarkupExtension<string>
{
    public string Key { get; set; } = "";
    public string ProvideValue(IServiceProvider serviceProvider) => L.FromKey(Key);
    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
