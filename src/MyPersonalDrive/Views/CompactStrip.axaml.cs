using Avalonia.Controls;

namespace MyPersonalDrive.Views;

/// <summary>The one-row strip the window collapses to (docs/PLAN-COMPACT-VIEW.md). Pure markup: the window chrome it implies is applied by <see cref="MainWindow"/>.</summary>
public partial class CompactStrip : UserControl
{
    public CompactStrip()
    {
        InitializeComponent();
    }
}
