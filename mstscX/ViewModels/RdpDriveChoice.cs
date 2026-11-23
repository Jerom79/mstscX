namespace mstscX.ViewModels;

public sealed class RdpDriveChoice(string name, bool selected, Action<string, bool> changeSelection)
{
    public string Name { get; } = name;

    public bool IsSelected
    {
        get => selected;
        set
        {
            if (selected == value)
                return;

            selected = value;
            changeSelection(Name, value);
        }
    }
}
