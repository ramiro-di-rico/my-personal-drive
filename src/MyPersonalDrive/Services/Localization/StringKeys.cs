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

        // Dialog buttons. One vocabulary for all of them — the code-built dialogs each used to
        // spell their own.
        public const string Ok = "common.ok";
        public const string Continue = "common.continue";
        public const string Apply = "common.apply";
        public const string Save = "common.save";
        public const string Create = "common.create";
        public const string Copy = "common.copy";
        public const string Copied = "common.copied";
        public const string Close = "common.close";
        public const string Browse = "common.browse";
        public const string Add = "common.add";

        /// <summary>Plural prefix — the "… and N more" tail on a truncated list.</summary>
        public const string More = "common.more";
    }

    /// <summary>The drag-and-drop overlays (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 5).</summary>
    public static class Drop
    {
        public const string UploadTo = "drop.uploadto";
        public const string DownloadTo = "drop.downloadto";
        public const string CurrentFolder = "drop.currentfolder";
        public const string Error = "drop.error";
    }

    /// <summary>Titles of the platform file/folder pickers.</summary>
    public static class Picker
    {
        public const string CliPathTitle = "picker.clipath.title";
        public const string DefaultSyncFolderTitle = "picker.defaultsyncfolder.title";
        public const string UploadTitle = "picker.upload.title";
        public const string DownloadFolderTitle = "picker.downloadfolder.title";
        public const string SaveLogTitle = "picker.savelog.title";
        public const string LocalSyncFolderTitle = "picker.localsyncfolder.title";
    }

    /// <summary>The dialogs built in <c>MainWindow.axaml.cs</c> rather than in markup.</summary>
    public static class Dialog
    {
        public const string RenameTitle = "dialog.rename.title";
        public const string RenamePrompt = "dialog.rename.prompt";

        public const string NewFolderTitle = "dialog.newfolder.title";
        public const string NewFolderPrompt = "dialog.newfolder.prompt";
        public const string NewFolderPlaceholder = "dialog.newfolder.placeholder";

        public const string CopyPrompt = "dialog.copy.prompt";
        public const string CopyPlaceholder = "dialog.copy.placeholder";

        public const string UploadConflictTitle = "dialog.uploadconflict.title";
        public const string UploadConflictIntro = "dialog.uploadconflict.intro";
        public const string UploadConflictQuestion = "dialog.uploadconflict.question";
        public const string UploadConflictKeepBoth = "dialog.uploadconflict.keepboth";
        public const string UploadConflictReplace = "dialog.uploadconflict.replace";
        public const string UploadConflictSkip = "dialog.uploadconflict.skip";

        public const string PairAddTitle = "dialog.pair.add.title";
        public const string PairEditTitle = "dialog.pair.edit.title";
        public const string PairRemotePathLabel = "dialog.pair.remotepath.label";
        public const string PairLocalFolderLabel = "dialog.pair.localfolder.label";
        public const string PairLocalFolderPlaceholder = "dialog.pair.localfolder.placeholder";
        public const string PairDirectionLabel = "dialog.pair.direction.label";
        public const string PairDirectionDownload = "dialog.pair.direction.download";
        public const string PairDirectionUpload = "dialog.pair.direction.upload";
        public const string PairDirectionTwoWay = "dialog.pair.direction.twoway";
        public const string PairPolicyLabel = "dialog.pair.policy.label";
        public const string PairPolicyAsk = "dialog.pair.policy.ask";
        public const string PairPolicyKeepBoth = "dialog.pair.policy.keepboth";
        public const string PairPolicyPreferLocal = "dialog.pair.policy.preferlocal";
        public const string PairPolicyPreferRemote = "dialog.pair.policy.preferremote";
        public const string PairMirrorDeletes = "dialog.pair.mirrordeletes";

        public const string RemoteBrowserTitle = "dialog.remotebrowser.title";
        public const string RemoteBrowserUp = "dialog.remotebrowser.up";
        public const string RemoteBrowserSelect = "dialog.remotebrowser.select";
        public const string RemoteBrowserBack = "dialog.remotebrowser.back";
        public const string RemoteBrowserEmpty = "dialog.remotebrowser.empty";
        public const string RemoteBrowserError = "dialog.remotebrowser.error";
        public const string RemoteBrowserFolder = "dialog.remotebrowser.folder";

        public const string PreviewTitle = "dialog.preview.title";

        // Plural prefixes. The summary used to be three sentences with "archivo(s)"/"carpeta(s)"
        // baked in — a hack no other language can reproduce. Each count is now its own clause so
        // each can agree on its own; the clauses are joined in code (docs/PLAN-I18N.md §6.3).
        public const string PreviewDownloadFiles = "dialog.preview.download.files";
        public const string PreviewDownloadFolders = "dialog.preview.download.folders";
        public const string PreviewUploadFiles = "dialog.preview.upload.files";
        public const string PreviewUploadFolders = "dialog.preview.upload.folders";
        public const string PreviewTrashLocal = "dialog.preview.trash.local";
        public const string PreviewTrashRemote = "dialog.preview.trash.remote";
        public const string PreviewMovedLocally = "dialog.preview.movedlocally";
        public const string PreviewMovedRemotely = "dialog.preview.movedremotely";
        public const string PreviewConflicts = "dialog.preview.conflicts";

        public const string PreviewWarning = "dialog.preview.warning";
        public const string PreviewNoActionsConflicts = "dialog.preview.noactions.conflicts";
        public const string PreviewNoActionsUpToDate = "dialog.preview.noactions.uptodate";
        public const string PreviewAction = "dialog.preview.action";

        /// <summary>Plural prefix; the one-form deliberately ignores the count.</summary>
        public const string ConflictsTitle = "dialog.conflicts.title";
        public const string ConflictsIntro = "dialog.conflicts.intro";
        public const string ConflictsChoiceLater = "dialog.conflicts.choice.later";
        public const string ConflictsChoiceKeepBoth = "dialog.conflicts.choice.keepboth";
        public const string ConflictsChoiceKeepLocal = "dialog.conflicts.choice.keeplocal";
        public const string ConflictsChoiceKeepRemote = "dialog.conflicts.choice.keepremote";
        public const string ConflictsReasonBothChanged = "dialog.conflicts.reason.bothchanged";
        public const string ConflictsReasonBothAppeared = "dialog.conflicts.reason.bothappeared";
        public const string ConflictsReasonRemoteDeleted = "dialog.conflicts.reason.remotedeleted";
        public const string ConflictsReasonLocalDeleted = "dialog.conflicts.reason.localdeleted";
        public const string ConflictsReasonDefault = "dialog.conflicts.reason.default";

        /// <summary>Plural prefix; the one-form deliberately ignores the count.</summary>
        public const string FailuresTitle = "dialog.failures.title";
        public const string FailuresIntro = "dialog.failures.intro";
        public const string FailuresChoiceLeave = "dialog.failures.choice.leave";
        public const string FailuresChoiceRetry = "dialog.failures.choice.retry";
        public const string FailuresChoiceDiscard = "dialog.failures.choice.discard";
        public const string FailuresRetryAll = "dialog.failures.retryall";

        public const string PropertiesTitle = "dialog.properties.title";
        public const string PropertiesField = "dialog.properties.field";

        public const string ConfirmTitle = "dialog.confirm.title";
        public const string AlertTitle = "dialog.alert.title";
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
