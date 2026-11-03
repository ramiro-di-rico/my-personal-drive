namespace MyPersonalDrive.ViewModels;

/// <summary>One in-flight provider call in the operations window — see <see cref="ActivityConsoleViewModel.ActiveOperations"/>.</summary>
public sealed class ActiveOperationViewModel
{
    public ActiveOperationViewModel(string account, string label)
    {
        Account = account;
        Label = label;
    }

    public string Account { get; }

    public string Label { get; }

    public string Display => $"[{Account}] {Label}";
}
