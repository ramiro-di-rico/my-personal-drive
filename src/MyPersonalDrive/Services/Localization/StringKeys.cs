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
