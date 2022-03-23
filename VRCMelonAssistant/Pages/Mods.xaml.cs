using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using Mono.Cecil;
using VRCMelonAssistant.Libs;
using static VRCMelonAssistant.Http;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace VRCMelonAssistant.Pages
{
    /// <summary>
    /// Interaction logic for Mods.xaml
    /// </summary>
    public sealed partial class Mods : Page
    {
        public static Mods Instance = new Mods();

        private static readonly ModListItem.CategoryInfo BrokenCategory = new("Broken", "These mods were broken by a game update. They will be temporarily removed and restored once they are updated for the current game version");
        private static readonly ModListItem.CategoryInfo UncategorizedCategory = new("Uncategorized", "Mods without a category assigned");
        private static readonly ModListItem.CategoryInfo UnknownCategory = new("Unknown/Unverified", "Mods not coming from VRCMG. Potentially dangerous.");

        public List<string> DefaultMods = new List<string>() { "UI Expansion Kit", "Finitizer", "VRCModUpdater.Loader", "VRChatUtilityKit", "Final IK Sanity", "ActionMenuApi" };
        public Mod[] AllModsList;
        public List<Mod> UnknownMods = new List<Mod>();
        public CollectionView view;
        public bool PendingChanges;
        public bool HaveInstalledMods;
        public bool FilteredToInstalled = false;
        public bool FilteredBySearch = false;

        private readonly SemaphoreSlim _modsLoadSem = new SemaphoreSlim(1, 1);

        public List<ModListItem> ModList { get; set; }

        public Mods()
        {
            InitializeComponent();
        }

        private void RefreshModsList()
        {
            view?.Refresh();
        }

        public void RefreshColumns()
        {
            if (MainWindow.Instance.Main.Content != Instance) return;
            double viewWidth = ModsListView.ActualWidth;
            double totalSize = 0;
            GridViewColumn description = null;

            if (ModsListView.View is GridView grid)
            {
                foreach (var column in grid.Columns)
                {
                    if (column.Header?.ToString() == FindResource("Mods:Header:Description").ToString())
                    {
                        description = column;
                    }
                    else
                    {
                        totalSize += column.ActualWidth;
                    }
                    if (double.IsNaN(column.Width))
                    {
                        column.Width = column.ActualWidth;
                        column.Width = double.NaN;
                    }
                }
                double descriptionNewWidth = viewWidth - totalSize - 35;
                description.Width = descriptionNewWidth > 200 ? descriptionNewWidth : 200;
            }
        }

        public async Task LoadMods()
        {
            await _modsLoadSem.WaitAsync();

            try
            {
                MainWindow.Instance.InstallButton.IsEnabled = false;
                MainWindow.Instance.InfoButton.IsEnabled = false;

                AllModsList = null;

                ModList = new List<ModListItem>();
                UnknownMods.Clear();
                HaveInstalledMods = false;

                ModsListView.Visibility = Visibility.Hidden;

                MainWindow.Instance.MainText = $"{FindResource("Mods:CheckingInstalledMods")}...";
                await CheckInstalledMods();
                InstalledColumn.Width = double.NaN;
                UninstallColumn.Width = 70;
                DescriptionColumn.Width = 750;

                MainWindow.Instance.MainText = $"{FindResource("Mods:LoadingMods")}...";
                await PopulateModsList();

                ModsListView.ItemsSource = ModList;

                view = (CollectionView)CollectionViewSource.GetDefaultView(ModsListView.ItemsSource);
                PropertyGroupDescription groupDescription = new PropertyGroupDescription("Category");
                view.GroupDescriptions.Add(groupDescription);

                this.DataContext = this;

                RefreshModsList();
                ModsListView.Visibility = ModList.Count == 0 ? Visibility.Hidden : Visibility.Visible;
                NoModsGrid.Visibility = ModList.Count == 0 ? Visibility.Visible : Visibility.Hidden;

                MainWindow.Instance.MainText = $"{FindResource("Mods:FinishedLoadingMods")}.";
                MainWindow.Instance.InstallButton.IsEnabled = ModList.Count != 0;
            }
            finally
            {
                _modsLoadSem.Release();
            }
        }

        public async Task CheckInstalledMods()
        {
            await GetAllMods();

            await Task.Run(() =>
            {
                CheckInstallDir("Plugins", false);
                CheckInstallDir("Mods", false);
                CheckInstallDir("Plugins/Broken", true);
                CheckInstallDir("Mods/Broken", true);
            });
        }

        public async Task GetAllMods()
        {
            try
            {
                var resp = await HttpClient.GetAsync(Utils.Constants.VRCMGModsJson);
                var body = await resp.Content.ReadAsStringAsync();
                AllModsList = JsonSerializer.Deserialize<Mod[]>(body);
                foreach (var mod in AllModsList)
                    mod.category ??= HardcodedCategories.GetCategoryFor(mod) ?? "Uncategorized";

                Array.Sort(AllModsList, (a, b) =>
                {
                    var categoryCompare = String.Compare(a.category, b.category, StringComparison.Ordinal);
                    if (categoryCompare != 0) return categoryCompare;
                    return String.Compare(a.versions[0].name, b.versions[0].name, StringComparison.Ordinal);
                });
            }
            catch (Exception e)
            {
                System.Windows.MessageBox.Show($"{FindResource("Mods:LoadFailed")}.\n\n" + e);
            }
        }

        private void CheckInstallDir(string directory, bool isBrokenDir)
        {
            if (!Directory.Exists(Path.Combine(App.VRChatInstallDirectory, directory)))
            {
                return;
            }

            foreach (string file in Directory.GetFileSystemEntries(Path.Combine(App.VRChatInstallDirectory, directory), "*.dll", SearchOption.TopDirectoryOnly))
            {
                if (!File.Exists(file) || Path.GetExtension(file) != ".dll") continue;

                var modInfo = ExtractModVersions(file);
                if (modInfo.Item1 != null && modInfo.Item2 != null)
                {
                    var haveFoundMod = false;

                    foreach (var mod in AllModsList)
                    {
                        if (!mod.aliases.Contains(modInfo.ModName) && mod.versions.All(it => it.name != modInfo.ModName)) continue;

                        HaveInstalledMods = true;
                        haveFoundMod = true;
                        mod.installedFilePath = file;
                        mod.installedVersion = modInfo.ModVersion;
                        mod.installedInBrokenDir = isBrokenDir;
                        break;
                    }

                    if (!haveFoundMod)
                    {
                        var mod = new Mod()
                        {
                            installedFilePath = file,
                            installedVersion = modInfo.ModVersion,
                            installedInBrokenDir = isBrokenDir,
                            versions = new []
                            {
                                new Mod.ModVersion()
                                {
                                    name = modInfo.ModName,
                                    modversion = modInfo.ModVersion,
                                    author = modInfo.ModAuthor,
                                    description = ""
                                }
                            }
                        };
                        UnknownMods.Add(mod);
                    }
                }
            }
        }

        private (string ModName, string ModVersion, string ModAuthor) ExtractModVersions(string dllPath)
        {
            try
            {
                using var asmdef = AssemblyDefinition.ReadAssembly(dllPath);
                foreach (var attr in asmdef.CustomAttributes)
                    if (attr.AttributeType.Name == "MelonInfoAttribute" ||
                        attr.AttributeType.Name == "MelonModInfoAttribute")
                        return ((string) attr.ConstructorArguments[1].Value,
                            (string) attr.ConstructorArguments[2].Value, (string) attr.ConstructorArguments[3].Value);
            }
            catch (Exception ex)
            {
                var result = MessageBox.Show(
                    $"A mod in {Path.GetFileName(dllPath)} is invalid. Would you like to delete it to avoid this error in the future?",
                    "Invalid mod", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        File.Delete(dllPath);
                    }
                    catch (Exception ex2)
                    {
                        Utils.ShowErrorMessageBox($"Unable to delete file {dllPath}", ex2);
                    }
                }
            }

            return (null, null, null);
        }

        public async Task PopulateModsList()
        {
            foreach (Mod mod in AllModsList)
                AddModToList(mod);

            foreach (var mod in UnknownMods)
                AddModToList(mod, UnknownCategory);
        }

        private void AddModToList(Mod mod, ModListItem.CategoryInfo categoryOverride = null)
        {
            bool preSelected = false;

            var latestVersion = mod.versions[0];

            if (DefaultMods.Contains(latestVersion.name) && !HaveInstalledMods || mod.installedFilePath != null)
            {
                preSelected = true;
            }

            ModListItem.CategoryInfo GetCategory(Mod mod)
            {
                if (mod.category == null) return UncategorizedCategory;
                return new ModListItem.CategoryInfo(mod.category,
                    HardcodedCategories.GetCategoryDescription(mod.category));
            }

            ModListItem ListItem = new ModListItem()
            {
                IsSelected = preSelected,
                IsEnabled = true,
                ModName = latestVersion.name,
                ModVersion = latestVersion.modversion,
                ModAuthor = HardcodedCategories.FixupAuthor(latestVersion.author),
                ModDescription = latestVersion.description.Replace("\r\n", " ").Replace("\n", " "),
                ModInfo = mod,
                IsInstalled = mod.installedFilePath != null,
                InstalledVersion = mod.installedVersion,
                InstalledModInfo = mod,
                Category = categoryOverride ?? (latestVersion.IsBroken ? BrokenCategory : GetCategory(mod))
            };

            foreach (Promotion promo in Promotions.ActivePromotions)
            {
                if (latestVersion.name == promo.ModName)
                {
                    ListItem.PromotionText = promo.Text;
                    ListItem.PromotionLink = promo.Link;
                }
            }

            mod.ListItem = ListItem;

            ModList.Add(ListItem);
        }

        public async void InstallMods()
        {
            MainWindow.Instance.InstallButton.IsEnabled = false;

            if (!InstallHandlers.IsMelonLoaderInstalled())
                await InstallHandlers.InstallMelonLoader();

            foreach (Mod mod in AllModsList)
            {
                // Ignore mods that are newer than installed version or up-to-date
                if (mod.ListItem.GetVersionComparison >= 0 && mod.installedInBrokenDir == mod.versions[0].IsBroken) continue;

                if (mod.ListItem.IsSelected)
                {
                    MainWindow.Instance.MainText = $"{string.Format((string)FindResource("Mods:InstallingMod"), mod.versions[0].name)}...";
                    await InstallHandlers.InstallMod(mod);
                    MainWindow.Instance.MainText = $"{string.Format((string)FindResource("Mods:InstalledMod"), mod.versions[0].name)}.";
                }
            }

            MainWindow.Instance.MainText = $"{FindResource("Mods:FinishedInstallingMods")}.";
            MainWindow.Instance.InstallButton.IsEnabled = true;
            RefreshModsList();
        }

        private void ModCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            Mod mod = ((sender as System.Windows.Controls.CheckBox).Tag as Mod);
            mod.ListItem.IsSelected = true;
        }

        private void ModCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            Mod mod = ((sender as System.Windows.Controls.CheckBox).Tag as Mod);
            mod.ListItem.IsSelected = false;
        }

        public class Category
        {
            public string CategoryName { get; set; }
            public List<ModListItem> Mods = new List<ModListItem>();
        }

        public class ModListItem
        {
            public string ModName { get; set; }
            public string ModVersion { get; set; }
            public string ModAuthor { get; set; }
            public string ModDescription { get; set; }
            public bool PreviousState { get; set; }

            public bool IsEnabled { get; set; }
            public bool IsSelected { get; set; }
            public Mod ModInfo { get; set; }
            public CategoryInfo Category { get; set; }

            public Mod InstalledModInfo { get; set; }
            public bool IsInstalled { get; set; }
            private SemVersion _installedVersion { get; set; }
            public string InstalledVersion
            {
                get
                {
                    if (!IsInstalled || _installedVersion == null) return "-";
                    return _installedVersion.ToString();
                }
                set
                {
                    if (SemVersion.TryParse(value, out SemVersion tempInstalledVersion))
                    {
                        _installedVersion = tempInstalledVersion;
                    }
                    else
                    {
                        _installedVersion = null;
                    }
                }
            }

            public string GetVersionColor
            {
                get
                {
                    if (!IsInstalled || _installedVersion == null) return "Black";
                    return _installedVersion >= ModVersion ? "Green" : "Red";
                }
            }

            public string GetVersionDecoration
            {
                get
                {
                    if (!IsInstalled || _installedVersion == null) return "None";
                    return _installedVersion >= ModVersion ? "None" : "Strikethrough";
                }
            }

            public int GetVersionComparison
            {
                get
                {
                    if (!IsInstalled || _installedVersion == null || _installedVersion < ModVersion) return -1;
                    if (_installedVersion > ModVersion) return 1;
                    return 0;
                }
            }

            public bool CanDelete => IsInstalled;

            public string CanSeeDelete => IsInstalled ? "Visible" : "Hidden";

            public string PromotionText { get; set; }
            public string PromotionLink { get; set; }
            public string PromotionMargin
            {
                get
                {
                    if (string.IsNullOrEmpty(PromotionText)) return "0";
                    return "0,0,5,0";
                }
            }

            public Visibility PromotionVisibility => string.IsNullOrEmpty(PromotionText) ? Visibility.Collapsed : Visibility.Visible;

            public record CategoryInfo(string Name, string Description)
            {
                public string Name { get; } = Name;
                public string Description { get; } = Description;
            }
        }

        private void ModsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((Mods.ModListItem)Instance.ModsListView.SelectedItem == null)
            {
                MainWindow.Instance.InfoButton.IsEnabled = false;
            }
            else
            {
                MainWindow.Instance.InfoButton.IsEnabled = true;
            }
        }


        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            Mod mod = ((sender as System.Windows.Controls.Button).Tag as Mod);

            string title = string.Format((string)FindResource("Mods:UninstallBox:Title"), mod.versions[0].name);
            string body1 = string.Format((string)FindResource("Mods:UninstallBox:Body1"), mod.versions[0].name);
            string body2 = string.Format((string)FindResource("Mods:UninstallBox:Body2"), mod.versions[0].name);
            var result = System.Windows.Forms.MessageBox.Show($"{body1}\n{body2}", title, MessageBoxButtons.YesNo);

            if (result == DialogResult.Yes)
            {
                UninstallModFromList(mod);
            }
        }

        private void UninstallModFromList(Mod mod)
        {
            UninstallMod(mod.ListItem.InstalledModInfo);
            mod.ListItem.IsInstalled = false;
            mod.ListItem.InstalledVersion = null;
            mod.ListItem.IsSelected = false;
            RefreshModsList();
            view.Refresh();
        }

        public void UninstallMod(Mod mod)
        {
            try
            {
                File.Delete(mod.installedFilePath);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"{FindResource("Mods:UninstallSingleFailed")}.\n\n" + ex);
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
            e.Handled = true;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshColumns();
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (SearchBar.Height == 0)
            {
                SearchBar.Focus();
                Animate(SearchBar, 0, 16, new TimeSpan(0, 0, 0, 0, 300));
                Animate(SearchText, 0, 16, new TimeSpan(0, 0, 0, 0, 300));
                FilteredBySearch = true;
                ModsListView.Items.Filter = new Predicate<object>(SearchFilter);
            }
            else
            {
                Animate(SearchBar, 16, 0, new TimeSpan(0, 0, 0, 0, 300));
                Animate(SearchText, 16, 0, new TimeSpan(0, 0, 0, 0, 300));
                FilteredBySearch = false;
                if (FilteredToInstalled)
                {
                    ModsListView.Items.Filter = ModsListView.Items.Filter;
                }
                // Only null this if both filters (search and installed) are off
                else
                {
                    ModsListView.Items.Filter = null;
                }
            }
        }

        private void SearchBar_TextChanged(object sender, TextChangedEventArgs e)
        {
            ModsListView.Items.Filter = ModsListView.Items.Filter;
            if (SearchBar.Text.Length > 0)
            {
                SearchText.Text = null;
            }
            else
            {
                SearchText.Text = (string)FindResource("Mods:SearchLabel");
            }
        }

        private bool SearchFilter(object mod)
        {
            ModListItem item = mod as ModListItem;
            // If the installed filter is on, don't search the text of mods that aren't installed
            if (FilteredToInstalled && !item.IsInstalled) return false;
            // SearchBar.Height does not adjust fast enough to be used here, FilteredBySearch is changed at the same time
            if (FilteredBySearch)
            {
                if (item.ModName.ToLower().Contains(SearchBar.Text.ToLower())) return true;
                if (item.ModDescription.ToLower().Contains(SearchBar.Text.ToLower())) return true;
                if (item.ModName.ToLower().Replace(" ", string.Empty).Contains(SearchBar.Text.ToLower().Replace(" ", string.Empty))) return true;
                if (item.ModDescription.ToLower().Replace(" ", string.Empty).Contains(SearchBar.Text.ToLower().Replace(" ", string.Empty))) return true;
                return false;
            }
            return true;
        }

        private void InstalledButton_Checked(object sender, RoutedEventArgs e)
        {
            FilteredToInstalled = true;
            ModsListView.Items.Filter = new Predicate<object>(SearchFilter);
        }

        private void InstalledButton_Unchecked(object sender, RoutedEventArgs e)
        {
            FilteredToInstalled = false;
            ModsListView.Items.Filter = new Predicate<object>(SearchFilter);
        }

        private void Animate(TextBlock target, double oldHeight, double newHeight, TimeSpan duration)
        {
            target.Height = oldHeight;
            DoubleAnimation animation = new DoubleAnimation(newHeight, duration);
            target.BeginAnimation(HeightProperty, animation);
        }

        private void Animate(TextBox target, double oldHeight, double newHeight, TimeSpan duration)
        {
            target.Height = oldHeight;
            DoubleAnimation animation = new DoubleAnimation(newHeight, duration);
            target.BeginAnimation(HeightProperty, animation);
        }

        private void ModsListView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            var selectedMod = ModsListView.SelectedItem as ModListItem;
            if (selectedMod == null) return;
            MainWindow.ShowModInfoWindow(selectedMod.ModInfo);
        }
    }
}
