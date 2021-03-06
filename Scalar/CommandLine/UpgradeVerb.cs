using CommandLine;
using Scalar.Common;
using Scalar.Common.FileSystem;
using Scalar.Common.Git;
using Scalar.Common.Tracing;
using Scalar.Upgrader;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Scalar.CommandLine
{
    [Verb(UpgradeVerbName, HelpText = "Checks for new Scalar release, downloads and installs it when available.")]
    public class UpgradeVerb : ScalarVerb.ForNoEnlistment
    {
        private const string UpgradeVerbName = "upgrade";
        private const string DryRunOption = "--dry-run";
        private const string NoVerifyOption = "--no-verify";
        private const string ConfirmOption = "--confirm";

        private ITracer tracer;
        private PhysicalFileSystem fileSystem;
        private ProductUpgrader upgrader;
        private InstallerPreRunChecker prerunChecker;
        private ProcessLauncher processLauncher;

        private ProductUpgraderPlatformStrategy productUpgraderPlatformStrategy;

        public UpgradeVerb(
            ProductUpgrader upgrader,
            ITracer tracer,
            PhysicalFileSystem fileSystem,
            InstallerPreRunChecker prerunChecker,
            ProcessLauncher processWrapper,
            TextWriter output)
        {
            this.upgrader = upgrader;
            this.tracer = tracer;
            this.fileSystem = fileSystem;
            this.prerunChecker = prerunChecker;
            this.processLauncher = processWrapper;
            this.Output = output;
            this.productUpgraderPlatformStrategy = ScalarPlatform.Instance.CreateProductUpgraderPlatformInteractions(fileSystem, tracer);
        }

        public UpgradeVerb()
        {
            this.fileSystem = new PhysicalFileSystem();
            this.processLauncher = new ProcessLauncher();
            this.Output = Console.Out;
        }

        [Option(
            "confirm",
            Default = false,
            Required = false,
            HelpText = "Pass in this flag to actually install the newest release")]
        public bool Confirmed { get; set; }

        [Option(
            "dry-run",
            Default = false,
            Required = false,
            HelpText = "Display progress and errors, but don't install Scalar")]
        public bool DryRun { get; set; }

        [Option(
            "no-verify",
            Default = false,
            Required = false,
            HelpText = "This parameter is reserved for internal use.")]
        public bool NoVerify { get; set; }

        protected override string VerbName
        {
            get { return UpgradeVerbName; }
        }

        public override void Execute()
        {
            string error;
            if (!this.TryInitializeUpgrader(out error) || !this.TryRunProductUpgrade())
            {
                this.ReportErrorAndExit(this.tracer, ReturnCode.GenericError, error);
            }
        }

        private bool TryInitializeUpgrader(out string error)
        {
            if (this.DryRun && this.Confirmed)
            {
                error = $"{DryRunOption} and {ConfirmOption} arguments are not compatible.";
                return false;
            }

            JsonTracer jsonTracer = new JsonTracer(ScalarConstants.ScalarEtwProviderName, "UpgradeVerb");
            string logFilePath = ScalarEnlistment.GetNewScalarLogFileName(
                ProductUpgraderInfo.GetLogDirectoryPath(),
                ScalarConstants.LogFileTypes.UpgradeVerb);
            jsonTracer.AddLogFileEventListener(logFilePath, EventLevel.Informational, Keywords.Any);

            this.tracer = jsonTracer;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                error = null;
                return true;
            }

            if (ScalarPlatform.Instance.UnderConstruction.UsesCustomUpgrader)
            {
                error = null;
                if (this.upgrader == null)
                {
                    this.productUpgraderPlatformStrategy = ScalarPlatform.Instance.CreateProductUpgraderPlatformInteractions(this.fileSystem, tracer: null);
                    if (!this.productUpgraderPlatformStrategy.TryPrepareLogDirectory(out error))
                    {
                        return false;
                    }

                    this.prerunChecker = new InstallerPreRunChecker(this.tracer, this.Confirmed ? ScalarConstants.UpgradeVerbMessages.ScalarUpgradeConfirm : ScalarConstants.UpgradeVerbMessages.ScalarUpgrade);

                    string gitBinPath = ScalarPlatform.Instance.GitInstallation.GetInstalledGitBinPath();
                    if (string.IsNullOrEmpty(gitBinPath))
                    {
                        error = $"nameof(this.TryInitializeUpgrader): Unable to locate git installation. Ensure git is installed and try again.";
                        return false;
                    }

                    ICredentialStore credentialStore = new GitProcess(gitBinPath, workingDirectoryRoot: null);

                    ProductUpgrader upgrader;
                    if (ProductUpgrader.TryCreateUpgrader(this.tracer, this.fileSystem, new LocalScalarConfig(), credentialStore, this.DryRun, this.NoVerify, out upgrader, out error))
                    {
                        this.upgrader = upgrader;
                    }
                    else
                    {
                        error = $"ERROR: {error}";
                    }
                }

                return this.upgrader != null;
            }
            else
            {
                error = $"ERROR: {ScalarConstants.UpgradeVerbMessages.ScalarUpgrade} is not supported on this operating system.";
                return false;
            }
        }

        private bool TryGetBrewOutput(string args, out string output, out string error)
        {
            this.Output.WriteLine($"Running 'brew {args}'");
            var launcher = new ProcessLauncher();
            bool result = launcher.TryStart("brew", args, useShellExecute: false, out Exception ex);

            if (!result)
            {
                this.tracer.RelatedEvent(EventLevel.Warning, $"Failure during 'brew {args}'", this.CreateEventMetadata(ex));
                output = null;
                error = "Failed to start 'brew' process";
                return false;
            }

            output = launcher.Process.StandardOutput.ReadToEnd().Trim();
            error = launcher.Process.StandardError.ReadToEnd().Trim();
            launcher.WaitForExit();
            return true;
        }

        private bool TryUpgradeWithBrew(out string error)
        {
            string output;
            string stderr;
            if (!this.TryGetBrewOutput("list --cask", out output, out stderr))
            {
                error = $"Failed to check 'brew' casks: '{stderr}' Is brew installed?";
                return false;
            }

            string packageName = string.Empty;

            if (output.IndexOf(ScalarConstants.HomebrewCasks.Scalar + "\n") >= 0)
            {
                packageName = ScalarConstants.HomebrewCasks.Scalar;
            }
            else if (output.IndexOf(ScalarConstants.HomebrewCasks.ScalarWithGVFS + "\n") >= 0)
            {
                packageName = ScalarConstants.HomebrewCasks.ScalarWithGVFS;
            }
            else
            {
                error = $"Scalar does not appear to be installed with 'brew': {stderr}";
                return false;
            }

            this.Output.WriteLine($"Found brew package '{packageName}'");

            if (!this.TryGetBrewOutput("update", out output, out stderr))
            {
                error = "An error occurred while updating 'brew' packages";
                return false;
            }

            if (!this.TryGetBrewOutput($"upgrade --cask {packageName}", out output, out stderr))
            {
                error = $"An error occurred while updating the {packageName} package: {stderr}";
                return false;
            }

            error = null;
            return true;
        }

        private bool TryRunProductUpgrade()
        {
            string errorOutputFormat = Environment.NewLine + "ERROR: {0}";
            string message = null;
            string cannotInstallReason = null;
            Version newestVersion = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (!this.TryUpgradeWithBrew(out string error)) {
                    this.tracer.RelatedError(error);
                    return false;
                }
                return true;
            }

            bool isInstallable = this.TryCheckUpgradeInstallable(out cannotInstallReason);
            if (this.ShouldRunUpgraderTool() && !isInstallable)
            {
                this.ReportInfoToConsole($"Cannot upgrade Scalar on this machine.");
                this.Output.WriteLine(errorOutputFormat, cannotInstallReason);
                return false;
            }

            if (!this.upgrader.UpgradeAllowed(out message))
            {
                ProductUpgraderInfo productUpgraderInfo = new ProductUpgraderInfo(
                    this.tracer,
                    this.fileSystem);
                productUpgraderInfo.DeleteAllInstallerDownloads();
                productUpgraderInfo.RecordHighestAvailableVersion(highestAvailableVersion: null);
                this.ReportInfoToConsole(message);
                return true;
            }

            if (!this.TryRunUpgradeChecks(out newestVersion, out message))
            {
                this.Output.WriteLine(errorOutputFormat, message);
                this.tracer.RelatedError($"{nameof(this.TryRunProductUpgrade)}: Upgrade checks failed. {message}");
                return false;
            }

            if (newestVersion == null)
            {
                // Make sure there a no asset installers remaining in the Downloads directory. This can happen if user
                // upgraded by manually downloading and running asset installers.
                ProductUpgraderInfo productUpgraderInfo = new ProductUpgraderInfo(
                    this.tracer,
                    this.fileSystem);
                productUpgraderInfo.DeleteAllInstallerDownloads();
                this.ReportInfoToConsole(message);
                return true;
            }

            if (this.ShouldRunUpgraderTool())
            {
                this.ReportInfoToConsole(message);

                if (!isInstallable)
                {
                    this.tracer.RelatedError($"{nameof(this.TryRunProductUpgrade)}: {message}");
                    this.Output.WriteLine(errorOutputFormat, message);
                    return false;
                }

                if (!this.TryRunInstaller(out message))
                {
                    this.tracer.RelatedError($"{nameof(this.TryRunProductUpgrade)}: Could not launch upgrade tool. {message}");
                    this.Output.WriteLine(errorOutputFormat, "Could not launch upgrade tool. " + message);
                    return false;
                }
            }
            else
            {
                string upgradeMessage = string.Format("{1}{0}{0}{2}{0}",
                    Environment.NewLine, message, ScalarConstants.UpgradeVerbMessages.UpgradeInstallAdvice);
                this.ReportInfoToConsole(upgradeMessage);
            }

            return true;
        }

        private bool TryRunUpgradeChecks(
            out Version latestVersion,
            out string error)
        {
            bool upgradeCheckSuccess = false;
            string errorMessage = null;
            Version version = null;

            this.ShowStatusWhileRunning(
                () =>
                {
                    upgradeCheckSuccess = this.TryCheckUpgradeAvailable(out version, out errorMessage);
                    return upgradeCheckSuccess;
                },
                 "Checking for Scalar upgrades");

            latestVersion = version;
            error = errorMessage;

            return upgradeCheckSuccess;
        }

        private bool TryRunInstaller(out string consoleError)
        {
            string upgraderPath = null;
            string errorMessage = null;
            bool supportsInlineUpgrade = ScalarPlatform.Instance.Constants.SupportsUpgradeWhileRunning;

            this.ReportInfoToConsole("Launching upgrade tool...");

            if (!this.TryCopyUpgradeTool(out upgraderPath, out consoleError))
            {
                return false;
            }

            if (!this.TryLaunchUpgradeTool(
                    upgraderPath,
                    runUpgradeInline: supportsInlineUpgrade,
                    consoleError: out errorMessage))
            {
                return false;
            }

            if (supportsInlineUpgrade)
            {
                this.processLauncher.WaitForExit();
                this.ReportInfoToConsole($"{Environment.NewLine}Upgrade completed.");
            }
            else
            {
                this.ReportInfoToConsole($"{Environment.NewLine}Installer launched in a new window. Do not run any git or scalar commands until the installer has completed.");
            }

            consoleError = null;
            return true;
        }

        private bool TryCopyUpgradeTool(out string upgraderExePath, out string consoleError)
        {
            upgraderExePath = null;

            using (ITracer activity = this.tracer.StartActivity(nameof(this.TryCopyUpgradeTool), EventLevel.Informational))
            {
                if (!this.upgrader.TrySetupUpgradeApplicationDirectory(out upgraderExePath, out consoleError))
                {
                    return false;
                }

                activity.RelatedInfo($"Successfully Copied upgrade tool to {upgraderExePath}");
            }

            return true;
        }

        private bool TryLaunchUpgradeTool(string path, bool runUpgradeInline, out string consoleError)
        {
            using (ITracer activity = this.tracer.StartActivity(nameof(this.TryLaunchUpgradeTool), EventLevel.Informational))
            {
                Exception exception;
                string args = string.Empty + (this.DryRun ? $" {DryRunOption}" : string.Empty) + (this.NoVerify ? $" {NoVerifyOption}" : string.Empty);

                // If the upgrade application is being run "inline" with the current process, then do not run the installer via the
                // shell - we want the upgrade process to inherit the current terminal's stdin / stdout / sterr
                if (!this.processLauncher.TryStart(path, args, !runUpgradeInline, out exception))
                {
                    if (exception != null)
                    {
                        consoleError = exception.Message;
                        this.tracer.RelatedError($"Error launching upgrade tool. {exception.ToString()}");
                    }
                    else
                    {
                        consoleError = "Error launching upgrade tool";
                    }

                    return false;
                }

                activity.RelatedInfo("Successfully launched upgrade tool.");
            }

            consoleError = null;
            return true;
        }

        private bool TryCheckUpgradeAvailable(
            out Version latestVersion,
            out string error)
        {
            latestVersion = null;
            error = null;

            using (ITracer activity = this.tracer.StartActivity(nameof(this.TryCheckUpgradeAvailable), EventLevel.Informational))
            {
                bool checkSucceeded = false;
                Version version = null;

                checkSucceeded = this.upgrader.TryQueryNewestVersion(out version, out error);
                if (!checkSucceeded)
                {
                    return false;
                }

                string currentVersion = ProcessHelper.GetCurrentProcessVersion();
                latestVersion = version;

                string message = latestVersion == null ?
                    $"Successfully checked for Scalar upgrades. Local version ({currentVersion}) is up-to-date." :
                    $"Successfully checked for Scalar upgrades. A new version is available: {latestVersion}, local version is: {currentVersion}.";

                activity.RelatedInfo(message);
            }

            return true;
        }

        private bool TryCheckUpgradeInstallable(out string consoleError)
        {
            consoleError = null;

            using (ITracer activity = this.tracer.StartActivity(nameof(this.TryCheckUpgradeInstallable), EventLevel.Informational))
            {
                if (!this.prerunChecker.TryRunPreUpgradeChecks(
                    out consoleError))
                {
                    return false;
                }

                activity.RelatedInfo("Upgrade is installable.");
            }

            return true;
        }

        private bool ShouldRunUpgraderTool()
        {
            return this.Confirmed || this.DryRun;
        }

        private void ReportInfoToConsole(string message, params object[] args)
        {
            this.Output.WriteLine(message, args);
        }

        public class ProcessLauncher
        {
            public ProcessLauncher()
            {
                this.Process = new Process();
            }

            public Process Process { get; private set; }

            public virtual bool HasExited
            {
                get { return this.Process.HasExited; }
            }

            public virtual int ExitCode
            {
                get { return this.Process.ExitCode; }
            }

            public virtual void WaitForExit()
            {
                this.Process.WaitForExit();
            }

            public virtual bool TryStart(string path, string args, bool useShellExecute, out Exception exception)
            {
                this.Process.StartInfo = new ProcessStartInfo(path)
                {
                    UseShellExecute = useShellExecute,
                    WorkingDirectory = Environment.SystemDirectory,
                    WindowStyle = ProcessWindowStyle.Normal,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Arguments = args
                };

                exception = null;

                try
                {
                    return this.Process.Start();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                return false;
            }
        }
    }
}
