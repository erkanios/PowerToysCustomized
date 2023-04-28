// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Shapes;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Wox.Infrastructure;
using Wox.Plugin;
using Wox.Plugin.Logger;
using BrowserInfo = Wox.Plugin.Common.DefaultBrowserInfo;

namespace Community.PowerToys.Run.Plugin.VSSolutionSearch
{
    /// <summary>
    /// TODOS: JUMPLIST item support??  C:\Users\erkan.ceyhan\AppData\Roaming\Microsoft\Windows\Recent
    /// Indexed solution files only!!
    /// </summary>
    public class Main : IPlugin, IPluginI18n, IContextMenu, ISettingProvider, IReloadable, IDisposable
    {
        // Should only be set in Init()
        private Action onPluginError;
        private static readonly IFileSystem _fileSystem = new FileSystem();
        private static ConcurrentBag<string> _allFiles = new ConcurrentBag<string>();
        private static string[] _ignoreFolders = new string[] { "node_modules", ".cache", ".git", ".svn", ".vscode", ".vs", ".nuget", "assets", "styles", "$recycle.bin", "bin", "obj", "dist", "wwwroot", "tmp", "history", "windows", "program files", "program files (x86)", "programdata", "recent" };
        private static bool _indexed = false;

        private const string NotGlobalIfUri = nameof(NotGlobalIfUri);

        /// <summary>If true, dont show global result on queries that are URIs</summary>
        private bool _notGlobalIfUri;

        private PluginInitContext _context;

        private string _iconPath;

        private bool _disposed;

        public string Name => Properties.Resources.plugin_name;

        public string Description => Properties.Resources.plugin_description;



        public IEnumerable<PluginAdditionalOption> AdditionalOptions => new List<PluginAdditionalOption>()
        {
            new PluginAdditionalOption()
            {
                Key = NotGlobalIfUri,
                DisplayLabel = Properties.Resources.plugin_global_if_uri,
                Value = false,
            },
        };

        public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
        {
            return new List<ContextMenuResult>(0);
        }

        public List<Result> Query(Query query)
        {
            if (query.Search.Contains(":reset")) 
            {
                _indexed = false;

                _context.API.ShowMsg(
                        $"Plugin: {Properties.Resources.plugin_name}",
                        $"Re-Indexing all solution files accross indexed disks...");
            }

            if (!_indexed)
            {
                _indexed = true;
                //Start building index
                try
                {
                    var locations = new List<string>();
                    var drives = System.IO.DriveInfo.GetDrives();
                    foreach (var drive in drives.Where(c => c.IsReady && c.DriveType == DriveType.Fixed))
                        locations.Add(drive.Name);

                    var identifiedFiles = new List<string>();

                    foreach (var directory in locations)
                    {
                        var files = Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly).ToList();
                        if (files.Any())
                        {
                            foreach (var file in files)
                                _allFiles.Add(file);
                        }

                        var childDirectories = Directory.GetDirectories(directory, string.Empty, SearchOption.TopDirectoryOnly);
                        Parallel.ForEach(childDirectories, dir => FindSolutionFilesRecursively(dir, "*.sln"));
                    }
                }
                catch (Exception ex)
                {
                    //_context.API.ShowMsg(
                    //        $"Plugin: {Properties.Resources.plugin_name}",
                    //        $"While indexing files... {ex.Message}");
                }

            }

            if (query is null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            var results = new List<Result>();

            // empty query
            if (string.IsNullOrEmpty(query.Search))
            {
                string arguments = "? ";
                results.Add(new Result
                {
                    Title = Properties.Resources.plugin_description,
                    SubTitle = string.Format(CultureInfo.CurrentCulture, Properties.Resources.plugin_in_browser_name, "all connected disks"),
                    QueryTextDisplay = string.Empty,
                    IcoPath = "Images/WebSearch.dark.png",
                    ProgramArguments = arguments,
                    Action = action =>
                    {
                        if (!Helper.OpenCommandInShell(BrowserInfo.Path, BrowserInfo.ArgumentsPattern, arguments))
                        {
                            onPluginError();
                            return false;
                        }

                        return true;
                    },
                });
                return results;
            }
            else
            {
                string searchTerm = query.Search;
                searchTerm = searchTerm.Replace("*", "");
                searchTerm = searchTerm.Replace(".sln", "");
                searchTerm = searchTerm.Replace("sln", "");
                var parts = searchTerm.Split(" ");

                var files = _allFiles.Where(s => parts.All(c => s.Contains(c, StringComparison.OrdinalIgnoreCase))).ToList();

                foreach (var file in files)
                {

                    var path = System.IO.Path.GetFullPath(file);
                    var fileName = System.IO.Path.GetFileName(file);

                    var toolTipTitle = string.Format(CultureInfo.CurrentCulture, "{0} : {1}", Properties.Resources.Microsoft_plugin_indexer_name, fileName);
                    var toolTipText = string.Format(CultureInfo.CurrentCulture, "{0} : {1}", Properties.Resources.Microsoft_plugin_indexer_path, path);


                    Result r = new Result();
                    r.Title = fileName;
                    r.SubTitle = Properties.Resources.Microsoft_plugin_indexer_subtitle_header + ": " + path;
                    r.IcoPath = "Images/WebSearch.dark.png";
                    r.ToolTipData = new ToolTipData(toolTipTitle, toolTipText);
                    r.Action = c =>
                    {
                        bool hide = true;
                        if (!Helper.OpenInShell(file, null, path))
                        {
                            hide = false;
                            var name = $"Plugin: {_context.CurrentPluginMetadata.Name}";
                            var msg = Properties.Resources.Microsoft_plugin_indexer_file_open_failed;
                            _context.API.ShowMsg(name, msg, string.Empty);
                        }

                        return hide;
                    };
                    // r.ContextData = searchResult;

                    // If the result is a directory, then it's display should show a directory.
                    if (_fileSystem.Directory.Exists(path))
                    {
                        r.QueryTextDisplay = path;
                    }

                    results.Add(r);
                }

            }

            return results;

        }

        public void Init(PluginInitContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _context.API.ThemeChanged += OnThemeChanged;
            UpdateIconPath(_context.API.GetCurrentTheme());
            BrowserInfo.UpdateIfTimePassed();

            onPluginError = () =>
            {
                string errorMsgString = string.Format(CultureInfo.CurrentCulture, Properties.Resources.plugin_search_failed, BrowserInfo.Name ?? BrowserInfo.MSEdgeName);

                Log.Error(errorMsgString, this.GetType());
                _context.API.ShowMsg(
                    $"Plugin: {Properties.Resources.plugin_name}",
                    errorMsgString);
            };


        }

        public void FindSolutionFilesRecursively(string directory, string searchPattern)
        {
            try
            {
                var folders = directory.ToLower(CultureInfo.InvariantCulture).Split("\\");
                if (_ignoreFolders.Contains(folders[folders.Length - 1].ToLower(CultureInfo.InvariantCulture)) ||
                    folders[folders.Length - 1].StartsWith(".", StringComparison.OrdinalIgnoreCase) ||
                    directory.Contains("AppData\\Roaming") ||
                    directory.Contains("AppData\\Local") ||
                    directory.Contains("AppData\\LocalLow") ||
                    directory.Contains("inetpub") ||
                    directory.Contains("Users\\Admin") ||
                    directory.Contains("Users\\Default") ||
                    directory.Contains("Users\\Public") ||
                    directory.Contains("Users\\All Users"))
                {
                    return; // No need to continue!
                }

                var files = Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    _allFiles.Add(file);
                }


                var childDirectories = Directory.GetDirectories(directory);
                if (childDirectories.Any())
                {
                    Parallel.ForEach(childDirectories, dir => FindSolutionFilesRecursively(dir, searchPattern));
                }
            }
            catch (Exception ex)
            {
                //Log.Error(ex.Message, typeof(Main));
                //_context.API.ShowMsg(
                //    $"Plugin: {Properties.Resources.plugin_name}",
                //    ex.Message);
            }
        }

        public string GetTranslatedPluginTitle()
        {
            return Properties.Resources.plugin_name;
        }

        public string GetTranslatedPluginDescription()
        {
            return Properties.Resources.plugin_description;
        }

        private void OnThemeChanged(Theme oldtheme, Theme newTheme)
        {
            UpdateIconPath(newTheme);
        }

        private void UpdateIconPath(Theme theme)
        {
            if (theme == Theme.Light || theme == Theme.HighContrastWhite)
            {
                _iconPath = "Images/setting_icon.png";
            }
            else
            {
                _iconPath = "Images/WebSearch.dark.png";
            }
        }

        public Control CreateSettingPanel()
        {
            throw new NotImplementedException();
        }

        public void UpdateSettings(PowerLauncherPluginSettings settings)
        {
            _notGlobalIfUri = settings?.AdditionalOptions?.FirstOrDefault(x => x.Key == NotGlobalIfUri)?.Value ?? false;
        }

        public void ReloadData()
        {
            if (_context is null)
            {
                return;
            }

            UpdateIconPath(_context.API.GetCurrentTheme());
            BrowserInfo.UpdateIfTimePassed();

        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                if (_context != null && _context.API != null)
                {
                    _context.API.ThemeChanged -= OnThemeChanged;
                }

                _disposed = true;
            }
        }
    }
}
