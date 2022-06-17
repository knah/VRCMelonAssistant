using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using Microsoft.Build.Utilities;

namespace VRCMelonAssistant
{
    public partial class ModInfoWindow : Window
    {
        public ModInfoWindow()
        {
            InitializeComponent();
        }

        public void SetMod(Mod mod)
        {
            Title = string.Format((string) FindResource("ModInfoWindow:Title"), mod.versions[0].name);

            ModDescription.Text = mod.versions[0].description ?? (string) FindResource("ModInfoWindow:NoDescription");
            ModName.Text = mod.versions[0].name;
            ModAuthor.Text = string.Format((string) FindResource("ModInfoWindow:Author"), mod.versions[0].author ?? FindResource("ModInfoWindow:NoAuthor"));
            ModVersion.Text = mod.versions[0].modVersion;

            var modReq = "";
            foreach (var requirement in mod.versions[0].requirements)
            {
                modReq += $"{requirement}\n";
            }
            ModRequirements.Text = modReq;

            var dlLink = mod.versions[0].downloadLink;
            DownloadLink.Text = (string) FindResource("ModInfoWindow:DownloadLink");
            DownloadLink.Inlines.Add(new Run(" "));
            if (dlLink?.StartsWith("http") == true)
                DownloadLink.Inlines.Add(CreateHyperlink(dlLink));
            else
                DownloadLink.Inlines.Add(new Run(dlLink));

            var srcLink = mod.versions[0].sourceLink;
            SourceCodeLink.Text = (string) FindResource("ModInfoWindow:SourceCodeLink");
            SourceCodeLink.Inlines.Add(new Run(" "));
            if (srcLink?.StartsWith("http") == true)
                SourceCodeLink.Inlines.Add(CreateHyperlink(srcLink));
            else
                SourceCodeLink.Inlines.Add(new Run(srcLink));

            InternalIds.Text = string.Format((string) FindResource("ModInfoWindow:InternalIds"), mod._id, mod.versions[0]._version);
        }

        private static Hyperlink CreateHyperlink(string uri)
        {
            var link = new Hyperlink(new Run(uri)) {NavigateUri = new Uri(uri)};
            link.RequestNavigate += HyperlinkExtensions.Hyperlink_RequestNavigate;
            return link;
        }
    }
}

