using System;
using System.Collections.Generic;
using System.Windows;

namespace TSK_AssetDL
{
    public partial class App : Application
    {
        public static string Root = Environment.CurrentDirectory;
        public static string Respath = String.Empty;
        public static int TotalCount = 0;
        public static int glocount = 0;
        
        // ==================== 更新 ====================
        public static string ServerURL = "https://d3mya90gbacu0m.cloudfront.net/prod/StreamingAssets/aa/";
        public static string CatalogBundleUrl = ServerURL + "catalog.bundle";
        // =============================================

        public static List<string> log = new List<string>();
    }
}