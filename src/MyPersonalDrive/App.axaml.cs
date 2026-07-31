using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyPersonalDrive.Services;
using MyPersonalDrive.ViewModels;
using MyPersonalDrive.Views;

namespace MyPersonalDrive;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new AppSettingsService();
            var locator = new ProtonDriveCliLocator(settings);
            var executor = new ProtonDriveCliExecutor(locator);
            var service = new ProtonDriveService(executor, settings.Load().StrictListingParsing);
            var cacheService = new DriveCacheService(Path.Combine(settings.BaseFolder, "cache.db"));

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(service, cacheService, settings)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
