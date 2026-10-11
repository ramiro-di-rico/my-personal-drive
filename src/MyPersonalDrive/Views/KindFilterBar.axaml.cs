using Avalonia.Controls;

namespace MyPersonalDrive.Views;

/// <summary>The type-filter chips under the search row. They come from the metrics histogram, so they only offer kinds the folder contains.</summary>
public partial class KindFilterBar : UserControl
{
    public KindFilterBar()
    {
        InitializeComponent();
    }
}
