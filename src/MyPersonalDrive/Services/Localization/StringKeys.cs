namespace MyPersonalDrive.Services.Localization;

/// <summary>
/// Every localizable key, as a constant. The values live in <c>Locales/*.json</c>; a test asserts
/// the two sets are exactly equal, so a key cannot be added to one and forgotten in the other.
///
/// Grouped by surface, not by control type, following the naming convention in
/// docs/PLAN-I18N.md §8. Keys are stable identifiers: renaming one is a change to every locale
/// file at once. A constant marked "plural prefix" names <c>&lt;key&gt;.one</c>/<c>.other</c>
/// together and must be resolved through <see cref="Localizer.Plural"/>, never <see cref="Localizer.T"/>.
/// </summary>
public static class StringKeys
{
    /// <summary>Vocabulary shared across surfaces — field names, dialog buttons, yes/no.</summary>
    public static class Common
    {
        public const string Account = "common.account";
        public const string Add = "common.add";
        public const string Apply = "common.apply";
        public const string Browse = "common.browse";
        public const string Bytes = "common.bytes";
        public const string Cancel = "common.cancel";
        public const string Close = "common.close";
        public const string Continue = "common.continue";
        public const string Copied = "common.copied";
        public const string Copy = "common.copy";
        public const string Create = "common.create";
        public const string File = "common.file";
        public const string Folder = "common.folder";
        public const string Loading = "common.loading";
        public const string Modified = "common.modified";
        /// <summary>Plural prefix.</summary>
        public const string More = "common.more";
        public const string Name = "common.name";
        public const string No = "common.no";
        public const string None = "common.none";
        public const string Ok = "common.ok";
        public const string Owner = "common.owner";
        public const string Path = "common.path";
        public const string Refresh = "common.refresh";
        public const string Save = "common.save";
        public const string Shared = "common.shared";
        public const string SignOut = "common.signout";
        public const string Size = "common.size";
        public const string Type = "common.type";
        public const string Unknown = "common.unknown";
        public const string Yes = "common.yes";
    }

    /// <summary>The window header: provider picker, session indicator, theme and panel toggles.</summary>
    public static class Header
    {
        public const string LocalPaneTooltip = "header.localpane.tooltip";
        public const string ProviderTooltip = "header.provider.tooltip";
        public const string SessionTooltip = "header.session.tooltip";
        public const string SettingsTooltip = "header.settings.tooltip";
        public const string ThemeDarkTooltip = "header.theme.dark.tooltip";
        public const string ThemeLightTooltip = "header.theme.light.tooltip";
        public const string ThemeSystemTooltip = "header.theme.system.tooltip";
    }

    /// <summary>The three view tabs plus Settings.</summary>
    public static class Nav
    {
        public const string Explorer = "nav.explorer";
        public const string Settings = "nav.settings";
        public const string Sync = "nav.sync";
        public const string SyncFailuresTooltip = "nav.sync.failures.tooltip";
        public const string SyncTooltip = "nav.sync.tooltip";
        public const string Viewer = "nav.viewer";
        public const string ViewerTooltip = "nav.viewer.tooltip";
    }

    /// <summary>Both explorer panes: toolbars, search, sorting, row affordances.</summary>
    public static class Explorer
    {
        public const string BackTooltip = "explorer.back.tooltip";
        public const string DownloadSelected = "explorer.download.selected";
        public const string EmptyClearFilters = "explorer.empty.clearfilters";
        public const string EmptyFilteredDetail = "explorer.empty.filtered.detail";
        public const string EmptyFilteredTitle = "explorer.empty.filtered.title";
        public const string EmptyFolderDetail = "explorer.empty.folder.detail";
        public const string EmptyFolderTitle = "explorer.empty.folder.title";
        public const string FilterSummary = "explorer.filter.summary";
        public const string HeaderSubtitle = "explorer.header.subtitle";
        public const string HeaderTitle = "explorer.header.title";
        public const string KindFilterTooltip = "explorer.kindfilter.tooltip";
        public const string LocalPath = "explorer.localpath";
        public const string NewFolderTooltip = "explorer.newfolder.tooltip";
        public const string RemotePath = "explorer.remotepath";
        public const string RowOpenTooltip = "explorer.row.open.tooltip";
        public const string RowRenameTooltip = "explorer.row.rename.tooltip";
        public const string RowSyncActiveTooltip = "explorer.row.syncactive.tooltip";
        public const string RowSyncPausedTooltip = "explorer.row.syncpaused.tooltip";
        public const string SearchClearTooltip = "explorer.search.clear.tooltip";
        public const string SearchPlaceholder = "explorer.search.placeholder";
        /// <summary>Plural prefix.</summary>
        public const string SearchResults = "explorer.search.results";
        /// <summary>Plural prefix.</summary>
        public const string SelectionCount = "explorer.selection.count";
        public const string SortLabel = "explorer.sort.label";
        public const string SplitterTooltip = "explorer.splitter.tooltip";
        public const string UploadTooltip = "explorer.upload.tooltip";
        public const string ViewGalleryTooltip = "explorer.view.gallery.tooltip";
        public const string ViewIconsTooltip = "explorer.view.icons.tooltip";
        public const string ViewListTooltip = "explorer.view.list.tooltip";
    }

    /// <summary>A listing row’s own affordances and the tooltips explaining a disabled one.</summary>
    public static class Node
    {
        public const string DownloadGoogleDoc = "node.download.googledoc";
        public const string DownloadTooltip = "node.download.tooltip";
        public const string ShareLinkTooltip = "node.sharelink.tooltip";
        public const string ShareLinkUnsupported = "node.sharelink.unsupported";
    }

    /// <summary>The item context menu. One vocabulary shared by all four menus in MainWindow.axaml.</summary>
    public static class Menu
    {
        public const string Copy = "menu.copy";
        public const string CopyPath = "menu.copypath";
        public const string CopyShareLink = "menu.copysharelink";
        public const string Delete = "menu.delete";
        public const string Download = "menu.download";
        public const string DownloadHere = "menu.downloadhere";
        public const string Open = "menu.open";
        public const string Preview = "menu.preview";
        public const string Properties = "menu.properties";
        public const string Rename = "menu.rename";
        public const string SyncNow = "menu.syncnow";
        public const string SyncPath = "menu.syncpath";
        public const string SyncPause = "menu.syncpause";
        public const string SyncResume = "menu.syncresume";
        public const string Trash = "menu.trash";
        public const string UploadHere = "menu.uploadhere";
    }

    /// <summary>The "you are not signed in" card in the listing area.</summary>
    public static class Auth
    {
        public const string RequiredTitle = "auth.required.title";
        public const string SignIn = "auth.signin";
    }

    /// <summary>What only the local pane has: home, hidden files, free space, local deletes.</summary>
    public static class Local
    {
        public const string ConfirmDeleteFolder = "local.confirm.deletefolder";
        /// <summary>Plural prefix.</summary>
        public const string ConfirmDeleteMany = "local.confirm.deletemany";
        public const string ConfirmDeleteOne = "local.confirm.deleteone";
        public const string DeleteSelected = "local.delete.selected";
        public const string EmptyFilteredDetail = "local.empty.filtered.detail";
        public const string EmptyFolderDetail = "local.empty.folder.detail";
        public const string FreeSpace = "local.freespace";
        public const string HiddenTooltip = "local.hidden.tooltip";
        public const string HomeTooltip = "local.home.tooltip";
        public const string StatusDeleteCancelled = "local.status.deletecancelled";
        public const string StatusDeleteCancelledOne = "local.status.deletecancelledone";
        public const string StatusDeleteFailed = "local.status.deletefailed";
        /// <summary>Plural prefix.</summary>
        public const string StatusDeletedMany = "local.status.deletedmany";
        public const string StatusDeletedOne = "local.status.deletedone";
        public const string StatusDeletedPartial = "local.status.deletedpartial";
        public const string StatusOpenFailed = "local.status.openfailed";
        public const string StatusRenameFailed = "local.status.renamefailed";
        public const string StatusSyncUnavailable = "local.status.syncunavailable";
    }

    /// <summary>Every StatusMessage sentence. Stored as a LocalizedText, so these follow a language change (docs/PLAN-I18N.md §6.3).</summary>
    public static class Status
    {
        public const string ActionReconnect = "status.action.reconnect";
        public const string ActionRetry = "status.action.retry";
        public const string ActionTooltip = "status.action.tooltip";
        public const string ActivitySaveFailed = "status.activity.savefailed";
        public const string ActivitySaved = "status.activity.saved";
        public const string ActivityUnavailable = "status.activity.unavailable";
        public const string BannerDismissTooltip = "status.banner.dismiss.tooltip";
        public const string AuthOpening = "status.auth.opening";
        public const string AuthSignedOut = "status.auth.signedout";
        public const string AuthSigningOut = "status.auth.signingout";
        /// <summary>Plural prefix.</summary>
        public const string BatchItems = "status.batch.items";
        public const string ClipboardPath = "status.clipboard.path";
        public const string CopyDone = "status.copy.done";
        public const string CopyProgress = "status.copy.progress";
        public const string CopyUnavailable = "status.copy.unavailable";
        /// <summary>Plural prefix.</summary>
        public const string DownloadBatchDone = "status.download.batchdone";
        public const string DownloadBatchPartial = "status.download.batchpartial";
        public const string DownloadCancelled = "status.download.cancelled";
        public const string DownloadCancelledTo = "status.download.cancelledto";
        /// <summary>Plural prefix.</summary>
        public const string DownloadDone = "status.download.done";
        public const string DownloadDoneTo = "status.download.doneto";
        public const string DownloadFailed = "status.download.failed";
        public const string DownloadItemError = "status.download.itemerror";
        public const string DownloadProgress = "status.download.progress";
        public const string DownloadSelectFiles = "status.download.selectfiles";
        public const string DownloadUnavailable = "status.download.unavailable";
        public const string EmptyDetails = "status.empty.details";
        public const string EmptyHint = "status.empty.hint";
        public const string LoadCacheFailed = "status.load.cachefailed";
        public const string LoadCached = "status.load.cached";
        /// <summary>Plural prefix.</summary>
        public const string LoadDone = "status.load.done";
        public const string LoadGone = "status.load.gone";
        public const string LoadProgress = "status.load.progress";
        public const string NewFolderDone = "status.newfolder.done";
        public const string NewFolderProgress = "status.newfolder.progress";
        public const string NewFolderUnavailable = "status.newfolder.unavailable";
        public const string PickCli = "status.pickcli";
        public const string PickCliInitial = "status.pickcli.initial";
        public const string ProviderNeedsAuth = "status.provider.needsauth";
        public const string ProviderNotConfigured = "status.provider.notconfigured";
        public const string ProviderSwitched = "status.provider.switched";
        public const string RenameDone = "status.rename.done";
        public const string RenameProgress = "status.rename.progress";
        public const string RenameUnavailable = "status.rename.unavailable";
        /// <summary>Plural prefix.</summary>
        public const string ScanCancelled = "status.scan.cancelled";
        /// <summary>Plural prefix.</summary>
        public const string ScanDone = "status.scan.done";
        public const string ScanSaveFailed = "status.scan.savefailed";
        public const string Selected = "status.selected";
        public const string SelectedTitle = "status.selected.title";
        public const string ShareCopied = "status.share.copied";
        public const string ShareLink = "status.share.link";
        public const string ShareProgress = "status.share.progress";
        public const string ShareUnsupported = "status.share.unsupported";
        public const string SignInToLoad = "status.signin.toload";
        public const string Title = "status.title";
        /// <summary>Plural prefix.</summary>
        public const string TrashCancelledMany = "status.trash.cancelledmany";
        public const string TrashCancelledOne = "status.trash.cancelledone";
        public const string TrashConfirmFolder = "status.trash.confirmfolder";
        /// <summary>Plural prefix.</summary>
        public const string TrashConfirmMany = "status.trash.confirmmany";
        /// <summary>Plural prefix.</summary>
        public const string TrashDoneMany = "status.trash.donemany";
        public const string TrashDoneOne = "status.trash.doneone";
        public const string TrashPartial = "status.trash.partial";
        public const string TrashProgress = "status.trash.progress";
        public const string UnexpectedError = "status.error.unexpected";
        public const string UploadCancelled = "status.upload.cancelled";
        public const string UploadCancelledItem = "status.upload.cancelleditem";
        /// <summary>Plural prefix.</summary>
        public const string UploadDone = "status.upload.done";
        public const string UploadDoneItem = "status.upload.doneitem";
        public const string UploadFailedItem = "status.upload.faileditem";
        /// <summary>Plural prefix.</summary>
        public const string UploadProgress = "status.upload.progress";
        public const string UploadUnavailable = "status.upload.unavailable";
        public const string ViewerError = "status.viewer.error";
        public const string ViewerImageUnavailable = "status.viewer.image.unavailable";
        public const string ViewerNotAText = "status.viewer.notatext";
        public const string ViewerOpenFailed = "status.viewer.openfailed";
        public const string ViewerOpening = "status.viewer.opening";
        public const string ViewerPdfUnavailable = "status.viewer.pdf.unavailable";
        public const string ViewerReadFailed = "status.viewer.readfailed";
        public const string ViewerSelectFile = "status.viewer.selectfile";
        public const string ViewerShowing = "status.viewer.showing";
        public const string ViewerTextUnavailable = "status.viewer.text.unavailable";
        public const string ViewerUnsupported = "status.viewer.unsupported";
    }

    /// <summary>The header connection badge: its state word and the sentence behind it (docs/PLAN-UX-ROUND-2.md §2).</summary>
    public static class Connection
    {
        public const string DescConnected = "connection.desc.connected";
        public const string DescDegraded = "connection.desc.degraded";
        public const string DescDisconnected = "connection.desc.disconnected";
        public const string DescLoading = "connection.desc.loading";
        public const string DescRateLimited = "connection.desc.ratelimited";
        public const string DescScanning = "connection.desc.scanning";
        public const string DescSyncing = "connection.desc.syncing";
        public const string Initial = "connection.initial";
        public const string StateDegraded = "connection.state.degraded";
        public const string StateDisconnected = "connection.state.disconnected";
        public const string StateOnline = "connection.state.online";
        public const string StateRateLimited = "connection.state.ratelimited";
        public const string StateSyncing = "connection.state.syncing";
    }

    /// <summary>The header quota gauge and its tooltip (docs/PLAN-UX-ROUND-2.md §3).</summary>
    public static class Quota
    {
        public const string AtLeast = "quota.atleast";
        public const string Caveat = "quota.caveat";
        public const string Exact = "quota.exact";
        public const string Summary = "quota.summary";
        public const string TooltipExact = "quota.tooltip.exact";
        public const string TooltipPartial = "quota.tooltip.partial";
        public const string TooltipUnknown = "quota.tooltip.unknown";
        public const string Unknown = "quota.unknown";
    }

    /// <summary>The folder-metrics panel in the status sidebar.</summary>
    public static class Metrics
    {
        /// <summary>Plural prefix.</summary>
        public const string AgeDays = "metrics.age.days";
        public const string AgeHours = "metrics.age.hours";
        public const string AgeJustNow = "metrics.age.justnow";
        public const string AgeMinutes = "metrics.age.minutes";
        public const string CaptionPartial = "metrics.caption.partial";
        public const string CaptionThisFolder = "metrics.caption.thisfolder";
        public const string CaptionTotal = "metrics.caption.total";
        public const string DepthNote = "metrics.depthnote";
        public const string EmptyFolder = "metrics.emptyfolder";
        /// <summary>Plural prefix.</summary>
        public const string Files = "metrics.files";
        /// <summary>Plural prefix.</summary>
        public const string Folders = "metrics.folders";
        public const string Headline = "metrics.headline";
        public const string Largest = "metrics.largest";
        public const string Newest = "metrics.newest";
        public const string NoData = "metrics.nodata";
        public const string Oldest = "metrics.oldest";
        public const string ProgressQueued = "metrics.progress.queued";
        /// <summary>Plural prefix.</summary>
        public const string ProgressScanned = "metrics.progress.scanned";
        public const string Scan = "metrics.scan";
        public const string ScanCancel = "metrics.scan.cancel";
        public const string ScanHint = "metrics.scan.hint";
        public const string ScopeCurrent = "metrics.scope.current";
        public const string ScopeDeclared = "metrics.scope.declared";
        public const string ScopeExcludes = "metrics.scope.excludes";
        /// <summary>Plural prefix.</summary>
        public const string ScopePartial = "metrics.scope.partial";
        /// <summary>Plural prefix.</summary>
        public const string ScopeRecursive = "metrics.scope.recursive";
        public const string ScopeRoot = "metrics.scope.root";
        public const string ScopeUnknownSize = "metrics.scope.unknownsize";
        /// <summary>Plural prefix.</summary>
        public const string Subfolders = "metrics.subfolders";
        public const string Title = "metrics.title";
    }

    /// <summary>The transfer queue.</summary>
    public static class Transfer
    {
        public const string Cancelled = "transfer.cancelled";
        public const string Done = "transfer.done";
        public const string Downloading = "transfer.downloading";
        public const string Failed = "transfer.failed";
        public const string None = "transfer.none";
        public const string Queued = "transfer.queued";
        public const string QueuedCount = "transfer.queuedcount";
        public const string Transferring = "transfer.transferring";
        public const string Uploading = "transfer.uploading";
    }

    /// <summary>The CLI activity console.</summary>
    public static class Console
    {
        /// <summary>Plural prefix.</summary>
        public const string ActiveOperations = "console.activeoperations";
        public const string ClearTooltip = "console.clear.tooltip";
        public const string DownloadTooltip = "console.download.tooltip";
        public const string Idle = "console.idle";
        public const string NoCommandRunning = "console.nocommand";
        public const string ResizeTooltip = "console.resize.tooltip";
        public const string SearchPlaceholder = "console.search.placeholder";
        public const string Title = "console.title";
        public const string ToggleHide = "console.toggle.hide";
        public const string ToggleShow = "console.toggle.show";
        public const string WarningsOnlyTooltip = "console.warningsonly.tooltip";
    }

    /// <summary>The in-app text/image/PDF viewer.</summary>
    public static class Viewer
    {
        public const string CloseTooltip = "viewer.close.tooltip";
        public const string Loading = "viewer.loading";
        public const string NotAText = "viewer.notatext";
        public const string NoteBytes = "viewer.note.bytes";
        public const string NoteDownloading = "viewer.note.downloading";
        public const string NoteMoreThan = "viewer.note.morethan";
        /// <summary>Plural prefix.</summary>
        public const string NotePageCount = "viewer.note.pagecount";
        public const string NotePages = "viewer.note.pages";
        public const string NoteText = "viewer.note.text";
        public const string NoteTruncated = "viewer.note.truncated";
        public const string Title = "viewer.title";
        public const string ZoomLabel = "viewer.zoom.label";
    }

    /// <summary>The sync view: pair rows, the panel around them, and their status lines.</summary>
    public static class Sync
    {
        public const string AccountPauseTooltip = "sync.account.pause.tooltip";
        public const string AccountResumeTooltip = "sync.account.resume.tooltip";
        public const string AddPair = "sync.addpair";
        public const string AddPairAdded = "sync.addpair.added";
        public const string AddPairCancelled = "sync.addpair.cancelled";
        public const string AddPairDuplicate = "sync.addpair.duplicate";
        public const string AddPairUnavailable = "sync.addpair.unavailable";
        public const string Analyzing = "sync.analyzing";
        public const string AutoSyncLabel = "sync.autosync.label";
        public const string AutoSyncOff = "sync.autosync.off";
        public const string AutoSyncOn = "sync.autosync.on";
        public const string AutoSyncPaused = "sync.autosync.paused";
        public const string AutoSyncResumed = "sync.autosync.resumed";
        public const string AutoSyncStateOff = "sync.autosync.state.off";
        public const string AutoSyncStateOn = "sync.autosync.state.on";
        public const string BusyFolderConfirm = "sync.busyfolder.confirm";
        /// <summary>Plural prefix.</summary>
        public const string ConflictsCount = "sync.conflicts.count";
        public const string ConflictsResolveFailed = "sync.conflicts.resolvefailed";
        /// <summary>Plural prefix.</summary>
        public const string ConflictsResolved = "sync.conflicts.resolved";
        public const string ConflictsResolvedPartial = "sync.conflicts.resolvedpartial";
        public const string ConflictsTooltip = "sync.conflicts.tooltip";
        public const string ConflictsUnavailable = "sync.conflicts.unavailable";
        public const string DirectionLocalToRemote = "sync.direction.localtoremote";
        public const string DirectionRemoteToLocal = "sync.direction.remotetolocal";
        public const string DirectionTwoWay = "sync.direction.twoway";
        public const string EditTooltip = "sync.edit.tooltip";
        public const string EditUnavailable = "sync.edit.unavailable";
        public const string EmptyState = "sync.emptystate";
        public const string ExecAborted = "sync.exec.aborted";
        /// <summary>Plural prefix.</summary>
        public const string ExecConflicts = "sync.exec.conflicts";
        /// <summary>Plural prefix.</summary>
        public const string ExecFailed = "sync.exec.failed";
        public const string ExecProgress = "sync.exec.progress";
        /// <summary>Plural prefix.</summary>
        public const string ExecScanning = "sync.exec.scanning";
        /// <summary>Plural prefix.</summary>
        public const string FailureAttempts = "sync.failure.attempts";
        public const string FailureNoReason = "sync.failure.noreason";
        public const string FailureSummary = "sync.failure.summary";
        /// <summary>Plural prefix.</summary>
        public const string FailuresDiscarded = "sync.failures.discarded";
        public const string FailuresNoChange = "sync.failures.nochange";
        /// <summary>Plural prefix.</summary>
        public const string FailuresRetried = "sync.failures.retried";
        /// <summary>Plural prefix.</summary>
        public const string FailuresSummary = "sync.failures.summary";
        public const string FailuresTooltip = "sync.failures.tooltip";
        public const string FailuresUnavailable = "sync.failures.unavailable";
        public const string FilterAccountTooltip = "sync.filter.account.tooltip";
        public const string FilterLabel = "sync.filter.label";
        public const string Intro = "sync.intro";
        public const string OpCreateLocalFolder = "sync.op.createlocalfolder";
        public const string OpCreateRemoteFolder = "sync.op.createremotefolder";
        public const string OpDeleteLocal = "sync.op.deletelocal";
        public const string OpDownload = "sync.op.download";
        public const string OpKeepBoth = "sync.op.keepboth";
        public const string OpRenameLocal = "sync.op.renamelocal";
        public const string OpRenameRemote = "sync.op.renameremote";
        public const string OpTrashRemote = "sync.op.trashremote";
        public const string OpUpdateBaseline = "sync.op.updatebaseline";
        public const string OpUpload = "sync.op.upload";
        public const string PausePauseTooltip = "sync.pause.pause.tooltip";
        public const string PauseResumeTooltip = "sync.pause.resume.tooltip";
        public const string PausedPrefix = "sync.paused.prefix";
        public const string PreviewTooltip = "sync.preview.tooltip";
        public const string PreviewUnavailable = "sync.preview.unavailable";
        public const string Progress = "sync.progress";
        /// <summary>Plural prefix.</summary>
        public const string RecoveryCleared = "sync.recovery.cleared";
        public const string RecoveryPrefix = "sync.recovery.prefix";
        public const string RemoveTooltip = "sync.remove.tooltip";
        public const string RetryFailed = "sync.retryfailed";
        public const string RetryFailedTooltip = "sync.retryfailed.tooltip";
        /// <summary>Plural prefix.</summary>
        public const string RetryRequeued = "sync.retry.requeued";
        public const string RetryReset = "sync.retry.reset";
        public const string SchedulerFailed = "sync.scheduler.failed";
        public const string ShowLabel = "sync.show.label";
        public const string SkipCaseCollision = "sync.skip.casecollision";
        public const string SkipDuplicateName = "sync.skip.duplicatename";
        public const string SkipGoogleNativeFile = "sync.skip.googlenativefile";
        public const string SkipUnmappableName = "sync.skip.unmappablename";
        public const string SkipUnspecified = "sync.skip.unspecified";
        public const string StatusError = "sync.status.error";
        public const string StatusNever = "sync.status.never";
        public const string StatusPartialFailure = "sync.status.partialfailure";
        public const string StatusUnknown = "sync.status.unknown";
        public const string StatusUpToDate = "sync.status.uptodate";
        public const string Syncing = "sync.syncing";
        public const string TimeNever = "sync.time.never";
        public const string Title = "sync.title";
        public const string WatcherDegraded = "sync.watcher.degraded";
        public const string WatcherDegradedLinux = "sync.watcher.degraded.linux";
    }

    /// <summary>The settings view.</summary>
    public static class Settings
    {
        public const string BandwidthLabel = "settings.bandwidth.label";
        public const string ConnectionTitle = "settings.connection.title";
        public const string GeneralTitle = "settings.general.title";
        public const string GoogleDriveClientIdLabel = "settings.googledrive.clientid.label";
        public const string GoogleDriveClientIdPlaceholder = "settings.googledrive.clientid.placeholder";
        public const string GoogleDriveClientSecretLabel = "settings.googledrive.clientsecret.label";
        public const string GoogleDriveClientSecretPlaceholder = "settings.googledrive.clientsecret.placeholder";
        public const string GoogleDriveHint = "settings.googledrive.hint";
        public const string GoogleDriveTitle = "settings.googledrive.title";
        public const string LanguageLabel = "settings.language.label";
        public const string LanguageTooltip = "settings.language.tooltip";
        public const string NextcloudConnect = "settings.nextcloud.connect";
        public const string NextcloudHint = "settings.nextcloud.hint";
        public const string NextcloudTitle = "settings.nextcloud.title";
        public const string OneDriveClientIdHint = "settings.onedrive.clientid.hint";
        public const string OneDriveClientIdLabel = "settings.onedrive.clientid.label";
        public const string OneDriveClientIdPlaceholder = "settings.onedrive.clientid.placeholder";
        public const string PanelShowStatus = "settings.panel.showstatus";
        public const string PanelTitle = "settings.panel.title";
        public const string ProtonCliPathBrowseTooltip = "settings.proton.clipath.browse.tooltip";
        public const string ProtonCliPathLabel = "settings.proton.clipath.label";
        public const string ProtonCliPathPlaceholder = "settings.proton.clipath.placeholder";
        public const string ProtonCliVersionLabel = "settings.proton.cliversion.label";
        public const string ProtonCliVersionRecheckTooltip = "settings.proton.cliversion.recheck.tooltip";
        public const string ProtonUpdateCheckTooltip = "settings.proton.update.check.tooltip";
        public const string ProtonUpdateInstall = "settings.proton.update.install";
        public const string ProtonUpdateInstallTooltip = "settings.proton.update.install.tooltip";
        public const string ProtonUpdateLabel = "settings.proton.update.label";
        public const string ProviderSignedInTooltip = "settings.provider.signedin.tooltip";
        public const string RefreshTooltip = "settings.refresh.tooltip";
        public const string S3Connect = "settings.s3.connect";
        public const string S3Hint = "settings.s3.hint";
        public const string S3Title = "settings.s3.title";
        public const string SignInTooltip = "settings.signin.tooltip";
        public const string SignOutTooltip = "settings.signout.tooltip";
        public const string SyncFolderBrowseTooltip = "settings.syncfolder.browse.tooltip";
        public const string SyncFolderLabel = "settings.syncfolder.label";
        public const string SyncFolderPlaceholder = "settings.syncfolder.placeholder";
        public const string ThemeDark = "settings.theme.dark";
        public const string ThemeLabel = "settings.theme.label";
        public const string ThemeLight = "settings.theme.light";
        public const string ThemeSystem = "settings.theme.system";
    }

    /// <summary>The drag-and-drop overlays (docs/INTERFACE_IMPROVEMENT_PLAN.md Task 5).</summary>
    public static class Drop
    {
        public const string CurrentFolder = "drop.currentfolder";
        public const string DownloadTo = "drop.downloadto";
        public const string Error = "drop.error";
        public const string UploadTo = "drop.uploadto";
    }

    /// <summary>Titles of the platform file/folder pickers.</summary>
    public static class Picker
    {
        public const string CliPathTitle = "picker.clipath.title";
        public const string DefaultSyncFolderTitle = "picker.defaultsyncfolder.title";
        public const string DownloadFolderTitle = "picker.downloadfolder.title";
        public const string LocalSyncFolderTitle = "picker.localsyncfolder.title";
        public const string SaveLogTitle = "picker.savelog.title";
        public const string UploadTitle = "picker.upload.title";
    }

    /// <summary>The dialogs built in MainWindow.axaml.cs rather than in markup.</summary>
    public static class Dialog
    {
        public const string AlertTitle = "dialog.alert.title";
        public const string ConfirmTitle = "dialog.confirm.title";
        public const string ConflictsChoiceKeepBoth = "dialog.conflicts.choice.keepboth";
        public const string ConflictsChoiceKeepLocal = "dialog.conflicts.choice.keeplocal";
        public const string ConflictsChoiceKeepRemote = "dialog.conflicts.choice.keepremote";
        public const string ConflictsChoiceLater = "dialog.conflicts.choice.later";
        public const string ConflictsIntro = "dialog.conflicts.intro";
        public const string ConflictsReasonBothAppeared = "dialog.conflicts.reason.bothappeared";
        public const string ConflictsReasonBothChanged = "dialog.conflicts.reason.bothchanged";
        public const string ConflictsReasonDefault = "dialog.conflicts.reason.default";
        public const string ConflictsReasonLocalDeleted = "dialog.conflicts.reason.localdeleted";
        public const string ConflictsReasonRemoteDeleted = "dialog.conflicts.reason.remotedeleted";
        /// <summary>Plural prefix.</summary>
        public const string ConflictsTitle = "dialog.conflicts.title";
        public const string CopyPlaceholder = "dialog.copy.placeholder";
        public const string CopyPrompt = "dialog.copy.prompt";
        public const string FailuresChoiceDiscard = "dialog.failures.choice.discard";
        public const string FailuresChoiceLeave = "dialog.failures.choice.leave";
        public const string FailuresChoiceRetry = "dialog.failures.choice.retry";
        public const string FailuresIntro = "dialog.failures.intro";
        public const string FailuresRetryAll = "dialog.failures.retryall";
        /// <summary>Plural prefix.</summary>
        public const string FailuresTitle = "dialog.failures.title";
        public const string NewFolderPlaceholder = "dialog.newfolder.placeholder";
        public const string NewFolderPrompt = "dialog.newfolder.prompt";
        public const string NewFolderTitle = "dialog.newfolder.title";
        public const string PairAddTitle = "dialog.pair.add.title";
        public const string PairDirectionDownload = "dialog.pair.direction.download";
        public const string PairDirectionLabel = "dialog.pair.direction.label";
        public const string PairDirectionTwoWay = "dialog.pair.direction.twoway";
        public const string PairDirectionUpload = "dialog.pair.direction.upload";
        public const string PairEditTitle = "dialog.pair.edit.title";
        public const string PairLocalFolderLabel = "dialog.pair.localfolder.label";
        public const string PairLocalFolderPlaceholder = "dialog.pair.localfolder.placeholder";
        public const string PairMirrorDeletes = "dialog.pair.mirrordeletes";
        public const string PairPolicyAsk = "dialog.pair.policy.ask";
        public const string PairPolicyKeepBoth = "dialog.pair.policy.keepboth";
        public const string PairPolicyLabel = "dialog.pair.policy.label";
        public const string PairPolicyPreferLocal = "dialog.pair.policy.preferlocal";
        public const string PairPolicyPreferRemote = "dialog.pair.policy.preferremote";
        public const string PairRemotePathLabel = "dialog.pair.remotepath.label";
        public const string PreviewAction = "dialog.preview.action";
        /// <summary>Plural prefix.</summary>
        public const string PreviewConflicts = "dialog.preview.conflicts";
        /// <summary>Plural prefix.</summary>
        public const string PreviewDownloadFiles = "dialog.preview.download.files";
        /// <summary>Plural prefix.</summary>
        public const string PreviewDownloadFolders = "dialog.preview.download.folders";
        /// <summary>Plural prefix.</summary>
        public const string PreviewMovedLocally = "dialog.preview.movedlocally";
        /// <summary>Plural prefix.</summary>
        public const string PreviewMovedRemotely = "dialog.preview.movedremotely";
        public const string PreviewNoActionsConflicts = "dialog.preview.noactions.conflicts";
        public const string PreviewNoActionsUpToDate = "dialog.preview.noactions.uptodate";
        public const string PreviewTitle = "dialog.preview.title";
        /// <summary>Plural prefix.</summary>
        public const string PreviewTrashLocal = "dialog.preview.trash.local";
        /// <summary>Plural prefix.</summary>
        public const string PreviewTrashRemote = "dialog.preview.trash.remote";
        /// <summary>Plural prefix.</summary>
        public const string PreviewUploadFiles = "dialog.preview.upload.files";
        /// <summary>Plural prefix.</summary>
        public const string PreviewUploadFolders = "dialog.preview.upload.folders";
        public const string PreviewWarning = "dialog.preview.warning";
        public const string PropertiesField = "dialog.properties.field";
        public const string PropertiesTitle = "dialog.properties.title";
        public const string RemoteBrowserBack = "dialog.remotebrowser.back";
        public const string RemoteBrowserEmpty = "dialog.remotebrowser.empty";
        public const string RemoteBrowserError = "dialog.remotebrowser.error";
        public const string RemoteBrowserFolder = "dialog.remotebrowser.folder";
        public const string RemoteBrowserSelect = "dialog.remotebrowser.select";
        public const string RemoteBrowserTitle = "dialog.remotebrowser.title";
        public const string RemoteBrowserUp = "dialog.remotebrowser.up";
        public const string RenamePrompt = "dialog.rename.prompt";
        public const string RenameTitle = "dialog.rename.title";
        public const string UploadConflictIntro = "dialog.uploadconflict.intro";
        public const string UploadConflictKeepBoth = "dialog.uploadconflict.keepboth";
        public const string UploadConflictQuestion = "dialog.uploadconflict.question";
        public const string UploadConflictReplace = "dialog.uploadconflict.replace";
        public const string UploadConflictSkip = "dialog.uploadconflict.skip";
        public const string UploadConflictTitle = "dialog.uploadconflict.title";
    }

    /// <summary>What `proton-drive --version` reported, or why it could not be read.</summary>
    public static class CliVersion
    {
        public const string NoVersionReported = "cliversion.none";
        public const string Unavailable = "cliversion.unavailable";
    }

    /// <summary>The CLI self-update check and install.</summary>
    public static class CliUpdate
    {
        public const string Available = "cliupdate.available";
        public const string Done = "cliupdate.done";
        public const string Downloading = "cliupdate.downloading";
        public const string DownloadingWithSize = "cliupdate.downloadingmb";
        public const string Failed = "cliupdate.failed";
        public const string InstalledVersionUnknown = "cliupdate.unknowninstalled";
        public const string ManifestUnreachable = "cliupdate.manifestfailed";
        public const string NoBuildForPlatform = "cliupdate.nobuild";
        public const string SyncInProgress = "cliupdate.syncinprogress";
        public const string Unavailable = "cliupdate.unavailable";
        public const string Unchecked = "cliupdate.unchecked";
        public const string UpToDate = "cliupdate.uptodate";
    }

    /// <summary>The framing sentences around a provider error. The provider's own message stays verbatim inside them (docs/PLAN-I18N.md §9).</summary>
    public static class Error
    {
        public const string AuthBadRedirect = "error.auth.badredirect";
        public const string AuthNoClientId = "error.auth.noclientid";
        public const string AuthNoRefreshToken = "error.auth.norefreshtoken";
        public const string AuthNoSession = "error.auth.nosession";
        public const string AuthRefreshFailed = "error.auth.refreshfailed";
        public const string AuthSignInCancelled = "error.auth.signincancelled";
        public const string AuthSignInFailed = "error.auth.signinfailed";
        public const string AuthSignInTimeout = "error.auth.signintimeout";
        public const string AuthTokenUnparsable = "error.auth.tokenunparsable";
        public const string CliCannotStart = "error.cli.cannotstart";
        public const string CliDownloadMissing = "error.cli.downloadmissing";
        public const string CliExpectedJson = "error.cli.expectedjson";
        public const string CliNoShareLinkCommand = "error.cli.nosharelinkcommand";
        public const string CliNotLocated = "error.cli.notlocated";
        public const string CliNothingDownloaded = "error.cli.nothingdownloaded";
        public const string CliUnrecognizedJson = "error.cli.unrecognizedjson";
        public const string CliUpdateChecksumMismatch = "error.cliupdate.checksummismatch";
        public const string HttpTimeout = "error.http.timeout";
        public const string KindAlreadyExists = "error.kind.alreadyexists";
        public const string KindBusy = "error.kind.busy";
        public const string KindConflict = "error.kind.conflict";
        public const string KindInvalidArgument = "error.kind.invalidargument";
        public const string KindNetwork = "error.kind.network";
        public const string KindNotAuthenticated = "error.kind.notauthenticated";
        public const string KindNotFound = "error.kind.notfound";
        public const string KindPermissionDenied = "error.kind.permissiondenied";
        public const string KindQuota = "error.kind.quota";
        public const string KindRateLimited = "error.kind.ratelimited";
        public const string KindTimeout = "error.kind.timeout";
        public const string KindUnknown = "error.kind.unknown";
        public const string LoadFailed = "error.loadfailed";
        public const string LogoutFailed = "error.logoutfailed";
        public const string NeedAuth = "error.needauth";
        public const string NeedAuthToLoad = "error.needauth.load";
        public const string OpCopyFailed = "error.op.copyfailed";
        public const string OpCopyTimeout = "error.op.copytimeout";
        public const string OpDeltaTooManyPages = "error.op.deltatoomanypages";
        public const string OpEmptyDeltaPage = "error.op.emptydeltapage";
        public const string OpEmptyListingPage = "error.op.emptylistingpage";
        public const string OpEmptyUpload = "error.op.emptyupload";
        public const string OpNoCopy = "error.op.nocopy";
        public const string OpNoCreatedFolder = "error.op.nocreatedfolder";
        public const string OpNoFreeName = "error.op.nofreename";
        public const string OpNoId = "error.op.noid";
        public const string OpNoParent = "error.op.noparent";
        public const string OpNoResumableSession = "error.op.noresumablesession";
        public const string OpNoShareLink = "error.op.nosharelink";
        public const string OpNoUploadSession = "error.op.nouploadsession";
        public const string OpNoUploadedFile = "error.op.nouploadedfile";
        public const string OpSegmentNotFound = "error.op.segmentnotfound";
        public const string OpUploadFailedAtByte = "error.op.uploadfailedatbyte";
        public const string PreviewFolderHasNoImage = "error.preview.folderhasnoimage";
        public const string PreviewFolderHasNoPdf = "error.preview.folderhasnopdf";
        public const string PreviewFolderHasNoText = "error.preview.folderhasnotext";
        public const string ProviderNotImplemented = "error.provider.notimplemented";
        public const string SyncVanishedBeforeMove = "error.sync.vanishedbeforemove";
        public const string SyncVanishedBeforeUpload = "error.sync.vanishedbeforeupload";
        public const string SyncWontOverwrite = "error.sync.wontoverwrite";
    }

    /// <summary>Why a sync pair was refused. The validator names the reason; these word it (docs/PLAN-I18N.md §9).</summary>
    public static class Issue
    {
        public const string DirectionUnsafeOverlap = "issue.direction.unsafeoverlap";
        public const string FreeSpace = "issue.freespace";
        public const string LocalAlreadySynced = "issue.local.alreadysynced";
        public const string LocalOverlaps = "issue.local.overlaps";
        public const string LocalPathHomeOrRoot = "issue.localpath.homeorroot";
        public const string LocalPathIsAFile = "issue.localpath.isafile";
        public const string LocalPathMissing = "issue.localpath.missing";
        public const string LocalPathNotWritable = "issue.localpath.notwritable";
        public const string RemoteAlreadySynced = "issue.remote.alreadysynced";
        public const string RemoteOverlaps = "issue.remote.overlaps";
        public const string RemotePathNotAbsolute = "issue.remotepath.notabsolute";
    }

    /// <summary>File-kind labels, for the metrics histogram and the type filter chips.</summary>
    public static class FileKind
    {
        public const string Archive = "filekind.archive";
        public const string Audio = "filekind.audio";
        public const string Code = "filekind.code";
        public const string Document = "filekind.document";
        public const string Folder = "filekind.folder";
        public const string Image = "filekind.image";
        public const string Other = "filekind.other";
        public const string Pdf = "filekind.pdf";
        public const string Presentation = "filekind.presentation";
        public const string Spreadsheet = "filekind.spreadsheet";
        public const string Text = "filekind.text";
        public const string Video = "filekind.video";
    }

    /// <summary>Provider session labels shown by the picker and the connection cards.</summary>
    public static class Provider
    {
        public const string NoSession = "provider.nosession";
        public const string SignedIn = "provider.signedin";
        public const string SignedOut = "provider.signedout";
    }
}
