using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MyPersonalDrive.Views.Dialogs;

/// <summary>The background-operations window: running transfers (cancellable) and in-flight provider requests.</summary>
public partial class OperationsWindow : Window
{
    public OperationsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }

    public static Task ShowAsync(Window owner, ViewModels.MainWindowViewModel viewModel)
        => new OperationsWindow { DataContext = viewModel }.ShowDialog(owner);
}
