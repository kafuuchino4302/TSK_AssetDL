using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace TSK_AssetDL
{
    public partial class MainWindow : Window
    {
        int pool = 50;
        const string RuntimePathToken = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}";

        // 共享一个 HttpClient，避免频繁 new WebClient 造成的连接开销/端口耗尽
        private static readonly HttpClient httpClient = new HttpClient();

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void btn_catalog_Click(object sender, RoutedEventArgs e)
        {
            btn_catalog.IsEnabled = false;
            App.glocount = 0;

            string catalogBundlePath = Path.Combine(App.Root, "catalog.bundle");
            await DownLoadFile(App.CatalogBundleUrl, catalogBundlePath, true);

            bool success = ExtractCatalogFromBundle(catalogBundlePath, Path.Combine(App.Root, "catalog.json"));

            if (success && App.glocount > 0)
                MessageBox.Show("Download catalog.bundle 并提取 catalog.json 完成", "Finish");
            else
                MessageBox.Show("Download catalog.bundle 失败或 catalog.json 提取失败", "Fail");

            App.glocount = 0;
            btn_catalog.IsEnabled = true;
        }

        private async void btn_download_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
            openFileDialog.InitialDirectory = App.Root;
            openFileDialog.Filter = "catalog.json|*.json";
            if (openFileDialog.ShowDialog() != true)
                return;

            string catalogPath = openFileDialog.FileName;

            List<string> internalIds;
            List<string> prefixes;
            try
            {
                (internalIds, prefixes) = ParseCatalogIds(catalogPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"解析 catalog.json 失败: {ex.Message}", "Error");
                return;
            }

            var assetSet = new HashSet<string>();
            foreach (string raw in internalIds)
            {
                string full = raw;
                int hashIdx = raw.IndexOf('#');
                if (hashIdx > 0
                    && int.TryParse(raw.Substring(0, hashIdx), out int prefixIndex)
                    && prefixIndex >= 0 && prefixIndex < prefixes.Count)
                {
                    full = prefixes[prefixIndex] + raw.Substring(hashIdx + 1);
                }

                if (!full.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                    continue;

                int tokenIdx = full.IndexOf(RuntimePathToken, StringComparison.Ordinal);
                string relative = tokenIdx >= 0
                    ? full.Substring(tokenIdx + RuntimePathToken.Length).TrimStart('/')
                    : full.TrimStart('/');

                assetSet.Add(relative);
            }

            var AssetList = assetSet.ToList();
            App.TotalCount = AssetList.Count;

            if (App.TotalCount == 0)
            {
                MessageBox.Show("未在 catalog 中找到任何 bundle", "Error");
                return;
            }

            App.Respath = Path.Combine(App.Root, "Asset");
            if (!Directory.Exists(App.Respath))
                Directory.CreateDirectory(App.Respath);

            int count = 0;
            List<Task> tasks = new List<Task>();

            foreach (string asset in AssetList)
            {
                string url = App.ServerURL + asset;                                 // 远程路径保留 WebGL/ 等前缀
                string path = Path.Combine(App.Respath, Path.GetFileName(asset));    // 本地只用文件名，平铺到 Asset 目录

                tasks.Add(DownLoadFile(url, path, cb_isCover.IsChecked == true));
                count++;

                if ((count % pool == 0) || App.TotalCount == count)
                {
                    await Task.WhenAll(tasks);
                    tasks.Clear();
                }

                lb_counter.Content = $"进度 : {count} / {App.TotalCount}";
                await Task.Delay(1);
            }

            if (cb_Debug.IsChecked == true && App.log.Count > 0)
            {
                File.WriteAllLines("404.log", App.log);
            }

            string failmsg = App.TotalCount - App.glocount > 0
                ? $"，{App.TotalCount - App.glocount}个檔案失敗"
                : "";

            MessageBox.Show($"下載完成，共{App.glocount}個檔案{failmsg}", "Finish");
            lb_counter.Content = string.Empty;
        }

        private (List<string> internalIds, List<string> prefixes) ParseCatalogIds(string path)
        {
            var internalIds = new List<string>();
            var prefixes = new List<string>();
            bool gotIds = false, gotPrefixes = false;

            using (var sr = new StreamReader(path, Encoding.UTF8))
            using (var reader = new JsonTextReader(sr))
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.PropertyName)
                    {
                        string? propName = (string?)reader.Value;
                        if (propName == "m_InternalIds" && !gotIds)
                        {
                            reader.Read();
                            ReadStringArray(reader, internalIds);
                            gotIds = true;
                        }
                        else if (propName == "m_InternalIdPrefixes" && !gotPrefixes)
                        {
                            reader.Read();
                            ReadStringArray(reader, prefixes);
                            gotPrefixes = true;
                        }

                        if (gotIds && gotPrefixes)
                            break;
                    }
                }
            }

            return (internalIds, prefixes);
        }

        private static void ReadStringArray(JsonTextReader reader, List<string> target)
        {
            if (reader.TokenType != JsonToken.StartArray) return;
            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
            {
                if (reader.TokenType == JsonToken.String && reader.Value != null)
                    target.Add((string)reader.Value);
            }
        }

        private bool ExtractCatalogFromBundle(string bundlePath, string outputJsonPath)
        {
            try
            {
                var am = new AssetsManager();
                BundleFileInstance bunInst = am.LoadBundleFile(bundlePath, true);
                AssetsFileInstance afileInst = am.LoadAssetsFileFromBundle(bunInst, 0, true);
                var afile = afileInst.file;

                // 如果类型信息没有内嵌，需要额外加载 classdata.tpk：
                // am.LoadClassPackage("classdata.tpk");
                // am.LoadClassDatabaseFromPackage(afile.Metadata.UnityVersion);

                foreach (var info in afile.GetAssetsOfType(AssetClassID.TextAsset))
                {
                    var baseField = am.GetBaseField(afileInst, info);
                    string? name = baseField["m_Name"].AsString;

                    if (name != null && name.IndexOf("catalog", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string? json = baseField["m_Script"].AsString;
                        if (json == null)
                            continue;

                        File.WriteAllText(outputJsonPath, json, new UTF8Encoding(false));
                        bunInst.file.Close();
                        return true;
                    }
                }

                bunInst.file.Close();
                return false;
            }
            catch (Exception ex)
            {
                App.log.Add("提取 catalog.json 失败: " + ex.Message);
                return false;
            }
        }

        public async Task DownLoadFile(string downPath, string savePath, bool overWrite)
        {
            string? dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(savePath) && !overWrite)
                return;

            App.glocount++;

            try
            {
                byte[] data = await httpClient.GetByteArrayAsync(downPath);
                await File.WriteAllBytesAsync(savePath, data);
            }
            catch (Exception ex)
            {
                App.glocount--;
                if (cb_Debug.IsChecked == true)
                    App.log.Add(downPath + Environment.NewLine + savePath + Environment.NewLine + ex.Message);
            }
        }
    }
}