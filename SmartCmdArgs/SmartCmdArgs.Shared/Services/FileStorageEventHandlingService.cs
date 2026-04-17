using SmartCmdArgs.Helper;
using SmartCmdArgs.Wrapper;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartCmdArgs.Services
{
    internal interface IFileStorageEventHandlingService : IDisposable
    {
        void AttachToEvents();
        void DetachFromEvents();
    }

    internal class FileStorageEventHandlingService : IFileStorageEventHandlingService
    {
        // Coalesces filewatcher bursts (e.g. UBT regenerating many .args.json files at once)
        // into a single update pass. 250 ms is imperceptible to users saving edits but
        // collapses UBT bursts that span seconds.
        private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);

        private readonly IFileStorageService fileStorage;
        private readonly IOptionsSettingsService optionsSettings;
        private readonly ISettingsService settingsService;
        private readonly ILifeCycleService lifeCycleService;
        private readonly IVisualStudioHelperService vsHelper;
        private readonly IViewModelUpdateService viewModelUpdateService;
        private readonly IToolWindowHistory toolWindowHistory;

        private readonly Debouncer _debouncer;

        private readonly object _pendingLock = new object();
        private readonly HashSet<IVsHierarchyWrapper> _pendingProjects = new HashSet<IVsHierarchyWrapper>();
        private bool _pendingSolutionWide;
        private bool _pendingSettings;

        public FileStorageEventHandlingService(
            IFileStorageService fileStorage,
            IOptionsSettingsService optionsSettings,
            ISettingsService settingsService,
            ILifeCycleService lifeCycleService,
            IVisualStudioHelperService vsHelper,
            IViewModelUpdateService viewModelUpdateService,
            IToolWindowHistory toolWindowHistory)
        {
            this.fileStorage = fileStorage;
            this.optionsSettings = optionsSettings;
            this.settingsService = settingsService;
            this.lifeCycleService = lifeCycleService;
            this.vsHelper = vsHelper;
            this.viewModelUpdateService = viewModelUpdateService;
            this.toolWindowHistory = toolWindowHistory;

            _debouncer = new Debouncer(DebounceWindow, ProcessPendingChanges);
        }

        public void Dispose()
        {
            DetachFromEvents();
            _debouncer.Dispose();
        }

        public void AttachToEvents()
        {
            fileStorage.FileStorageChanged += FileStorage_FileStorageChanged;
        }

        public void DetachFromEvents()
        {
            fileStorage.FileStorageChanged -= FileStorage_FileStorageChanged;
        }

        private void FileStorage_FileStorageChanged(object sender, FileStorageChangedEventArgs e)
        {
            // This event is triggered on non-main thread!

            lock (_pendingLock)
            {
                if (e.Type == FileStorageChanedType.Settings)
                {
                    _pendingSettings = true;
                }
                else if (e.IsSolutionWide)
                {
                    _pendingSolutionWide = true;
                }
                else if (e.Project != null)
                {
                    _pendingProjects.Add(e.Project);
                }
            }

            _debouncer.CallActionDebounced();
        }

        private void ProcessPendingChanges()
        {
            // Runs on UI thread (Debouncer onUiThread default = true).

            bool handleSettings;
            bool handleSolutionWide;
            List<IVsHierarchyWrapper> projectsToUpdate;

            lock (_pendingLock)
            {
                handleSettings = _pendingSettings;
                handleSolutionWide = _pendingSolutionWide;
                projectsToUpdate = _pendingProjects.Count > 0
                    ? _pendingProjects.ToList()
                    : null;

                _pendingSettings = false;
                _pendingSolutionWide = false;
                _pendingProjects.Clear();
            }

            if (handleSettings)
            {
                if (optionsSettings.SaveSettingsToJson)
                    settingsService.Load();
            }

            if (!lifeCycleService.IsEnabled)
                return;

            if (!optionsSettings.VcsSupportEnabled)
                return;

            // git branch and merge might lead to a race condition here.
            // If a branch is checkout where the json file differs, the
            // filewatcher will trigger an event which is dispatched here.
            // However, while the function call is queued VS may reopen the
            // solution due to changes. This will ultimately result in a
            // null ref exception because the project object is unloaded.
            // UpdateCommandsForProject() will skip such projects because
            // their guid is empty.

            IEnumerable<IVsHierarchyWrapper> projects = null;
            if (optionsSettings.UseSolutionDir)
            {
                if (handleSolutionWide)
                    projects = vsHelper.GetSupportedProjects();
            }
            else if (projectsToUpdate != null)
            {
                projects = projectsToUpdate;
            }

            if (projects == null)
                return;

            toolWindowHistory.SaveState();

            viewModelUpdateService.UpdateCommandsForProjects(projects);

            viewModelUpdateService.UpdateIsActiveForParamsDebounced();
        }
    }
}
