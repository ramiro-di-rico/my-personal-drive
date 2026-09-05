namespace MyPersonalDrive.Services.Localization;

/// <summary>
/// Every localizable key, as a constant. The values live in <c>Locales/*.json</c>; a test asserts
/// the two sets are exactly equal, so a key cannot be added to one and forgotten in the other.
///
/// Grouped by surface, not by control type — <c>settings.*</c>, <c>nav.*</c>, <c>common.*</c> —
/// following the naming convention in docs/PLAN-I18N.md §8. Keys are stable identifiers: renaming
/// one is a change to every locale file at once.
/// </summary>
public static class StringKeys
{
    public static class Common
    {
        public const string Account = "common.account";
        public const string Refresh = "common.refresh";
        public const string SignOut = "common.signout";
        public const string Cancel = "common.cancel";
        public const string Loading = "common.loading";

        // The column/field vocabulary. Shared deliberately between the sort menu and the details
        // sidebar: they name the same properties of the same item.
        public const string Name = "common.name";
        public const string Size = "common.size";
        public const string Modified = "common.modified";
        public const string Type = "common.type";
        public const string Path = "common.path";
        public const string Owner = "common.owner";
        public const string Shared = "common.shared";
    }

    public static class Header
    {
        public const string ProviderTooltip = "header.provider.tooltip";
        public const string SessionTooltip = "header.session.tooltip";
        public const string ThemeSystemTooltip = "header.theme.system.tooltip";
        public const string ThemeLightTooltip = "header.theme.light.tooltip";
        public const string ThemeDarkTooltip = "header.theme.dark.tooltip";
        public const string LocalPaneTooltip = "header.localpane.tooltip";
        public const string SettingsTooltip = "header.settings.tooltip";
    }

    public static class Nav
    {
        public const string Explorer = "nav.explorer";
        public const string Viewer = "nav.viewer";
        public const string Sync = "nav.sync";
        public const string Settings = "nav.settings";
        public const string ViewerTooltip = "nav.viewer.tooltip";
        public const string SyncTooltip = "nav.sync.tooltip";
        public const string SyncFailuresTooltip = "nav.sync.failures.tooltip";
    }

    public static class Explorer
    {
        public const string BackTooltip = "explorer.back.tooltip";
        public const string NewFolderTooltip = "explorer.newfolder.tooltip";
        public const string UploadTooltip = "explorer.upload.tooltip";
        public const string ViewListTooltip = "explorer.view.list.tooltip";
        public const string ViewIconsTooltip = "explorer.view.icons.tooltip";
        public const string ViewGalleryTooltip = "explorer.view.gallery.tooltip";
        public const string SearchPlaceholder = "explorer.search.placeholder";
        public const string SearchClearTooltip = "explorer.search.clear.tooltip";
        public const string SortLabel = "explorer.sort.label";
        public const string DownloadSelected = "explorer.download.selected";
        public const string KindFilterTooltip = "explorer.kindfilter.tooltip";
        public const string SplitterTooltip = "explorer.splitter.tooltip";
        public const string RowOpenTooltip = "explorer.row.open.tooltip";
        public const string RowRenameTooltip = "explorer.row.rename.tooltip";
        public const string RowSyncActiveTooltip = "explorer.row.syncactive.tooltip";
        public const string RowSyncPausedTooltip = "explorer.row.syncpaused.tooltip";
    }

    /// <summary>
    /// The item context menu. One vocabulary shared by the remote pane's row menu, its
    /// empty-space menu, the tile menu and the local pane's menu — those four used to carry four
    /// copies of the same nine literals.
    /// </summary>
    public static class Menu
    {
        public const string Open = "menu.open";
        public const string Preview = "menu.preview";
        public const string Copy = "menu.copy";
        public const string Rename = "menu.rename";
        public const string Download = "menu.download";
        public const string DownloadHere = "menu.downloadhere";
        public const string UploadHere = "menu.uploadhere";
        public const string CopyPath = "menu.copypath";
        public const string CopyShareLink = "menu.copysharelink";
        public const string SyncPath = "menu.syncpath";
        public const string SyncNow = "menu.syncnow";
        public const string SyncPause = "menu.syncpause";
        public const string SyncResume = "menu.syncresume";
        public const string Trash = "menu.trash";
        public const string Delete = "menu.delete";
        public const string Properties = "menu.properties";
    }

    public static class Auth
    {
        public const string RequiredTitle = "auth.required.title";
        public const string SignIn = "auth.signin";
    }

    public static class Local
    {
        public const string HomeTooltip = "local.home.tooltip";
        public const string HiddenTooltip = "local.hidden.tooltip";
        public const string DeleteSelected = "local.delete.selected";

        /// <summary>Takes an already-formatted byte size.</summary>
        public const string FreeSpace = "local.freespace";
    }

    public static class Status
    {
        public const string Title = "status.title";
        public const string ActionTooltip = "status.action.tooltip";
        public const string EmptyDetails = "status.empty.details";
        public const string EmptyHint = "status.empty.hint";
        public const string SelectedTitle = "status.selected.title";
    }

    public static class Metrics
    {
        public const string Title = "metrics.title";
        public const string Scan = "metrics.scan";
        public const string ScanHint = "metrics.scan.hint";
        public const string ScanCancel = "metrics.scan.cancel";
        public const string Largest = "metrics.largest";
        public const string Newest = "metrics.newest";
        public const string Oldest = "metrics.oldest";
        public const string ScopeCurrent = "metrics.scope.current";
        public const string ScopeRoot = "metrics.scope.root";
    }

    public static class Console
    {
        public const string Title = "console.title";
        public const string SearchPlaceholder = "console.search.placeholder";
        public const string WarningsOnlyTooltip = "console.warningsonly.tooltip";
        public const string DownloadTooltip = "console.download.tooltip";
        public const string ClearTooltip = "console.clear.tooltip";

        /// <summary>Plural prefix — resolve through <c>Localizer.Plural</c>, never directly.</summary>
        public const string ActiveOperations = "console.activeoperations";
    }

    public static class Viewer
    {
        public const string ZoomLabel = "viewer.zoom.label";
        public const string CloseTooltip = "viewer.close.tooltip";
        public const string Loading = "viewer.loading";
    }

    public static class Settings
    {
        public const string GeneralTitle = "settings.general.title";

        public const string LanguageLabel = "settings.language.label";
        public const string LanguageTooltip = "settings.language.tooltip";

        public const string ThemeLabel = "settings.theme.label";
        public const string ThemeSystem = "settings.theme.system";
        public const string ThemeLight = "settings.theme.light";
        public const string ThemeDark = "settings.theme.dark";

        public const string SyncFolderLabel = "settings.syncfolder.label";
        public const string SyncFolderPlaceholder = "settings.syncfolder.placeholder";
        public const string SyncFolderBrowseTooltip = "settings.syncfolder.browse.tooltip";

        public const string BandwidthLabel = "settings.bandwidth.label";

        public const string PanelTitle = "settings.panel.title";
        public const string PanelShowStatus = "settings.panel.showstatus";

        public const string ConnectionTitle = "settings.connection.title";
        public const string ProviderSignedInTooltip = "settings.provider.signedin.tooltip";

        /// <summary>Takes the active provider's display name — one key instead of one per provider.</summary>
        public const string SignInTooltip = "settings.signin.tooltip";

        /// <summary>Takes the active provider's display name.</summary>
        public const string SignOutTooltip = "settings.signout.tooltip";

        public const string RefreshTooltip = "settings.refresh.tooltip";

        public const string ProtonCliPathLabel = "settings.proton.clipath.label";
        public const string ProtonCliPathPlaceholder = "settings.proton.clipath.placeholder";
        public const string ProtonCliPathBrowseTooltip = "settings.proton.clipath.browse.tooltip";
        public const string ProtonCliVersionLabel = "settings.proton.cliversion.label";
        public const string ProtonCliVersionRecheckTooltip = "settings.proton.cliversion.recheck.tooltip";
        public const string ProtonUpdateLabel = "settings.proton.update.label";
        public const string ProtonUpdateCheckTooltip = "settings.proton.update.check.tooltip";
        public const string ProtonUpdateInstall = "settings.proton.update.install";
        public const string ProtonUpdateInstallTooltip = "settings.proton.update.install.tooltip";

        public const string OneDriveClientIdLabel = "settings.onedrive.clientid.label";
        public const string OneDriveClientIdHint = "settings.onedrive.clientid.hint";
        public const string OneDriveClientIdPlaceholder = "settings.onedrive.clientid.placeholder";

        public const string GoogleDriveTitle = "settings.googledrive.title";
        public const string GoogleDriveHint = "settings.googledrive.hint";
        public const string GoogleDriveClientIdLabel = "settings.googledrive.clientid.label";
        public const string GoogleDriveClientIdPlaceholder = "settings.googledrive.clientid.placeholder";
        public const string GoogleDriveClientSecretLabel = "settings.googledrive.clientsecret.label";
        public const string GoogleDriveClientSecretPlaceholder = "settings.googledrive.clientsecret.placeholder";

        public const string NextcloudTitle = "settings.nextcloud.title";
        public const string NextcloudHint = "settings.nextcloud.hint";
        public const string NextcloudConnect = "settings.nextcloud.connect";

        public const string S3Title = "settings.s3.title";
        public const string S3Hint = "settings.s3.hint";
        public const string S3Connect = "settings.s3.connect";
    }
}
