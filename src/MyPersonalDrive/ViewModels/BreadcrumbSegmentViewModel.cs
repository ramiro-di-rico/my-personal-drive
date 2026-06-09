namespace MyPersonalDrive.ViewModels;

public sealed class BreadcrumbSegmentViewModel
{
    public BreadcrumbSegmentViewModel(string label, string path, bool isCurrent, Func<string, Task> navigateAsync)
    {
        Label = label;
        Path = path;
        IsCurrent = isCurrent;
        OpenCommand = new AsyncCommand(() => navigateAsync(path), () => !isCurrent);
    }

    public string Label { get; }

    public string Path { get; }

    public bool IsCurrent { get; }

    public bool CanNavigate => !IsCurrent;

    public AsyncCommand OpenCommand { get; }
}
