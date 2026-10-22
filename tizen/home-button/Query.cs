// Read-only query of the Samsung Home assignment, run on the TV as the SDK user (uid 901).
//
// Adapted from NelsonEU/arn0-tv native/Query.cs (Git blob 07e5137b9e63ca50875dea9a524fdc69b109a1d4),
// Copyright (c) 2026 Arnaud Etienne, MIT License: see LICENSE-arn0 next to this file.
// Changes: the target app and the expected firmware come from the command line instead of constants.
//
//   dotnet --globalizationinvariant HomeQuery.dll FIRMWARE APP_ID
//
// No preference-writing function is imported. aul_svc_set_operation only fills the temporary bundle used for
// the query; it does not assign Home.
using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class Query
{
    private static readonly string[] Keys = {
        "db/appfw/home_appid",
        "db/appfw/startup_appid",
        "db/appfw/booting_appid_backup",
        "db/first_screen/type"
    };
    private const string AutoKey = "db/menu/smart_hub/ontvsetting/auto_start";
    // Letters, digits, dot, underscore and dash only: what Tizen app ids and firmware names use.
    private static bool IsSafe(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (char c in value)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'))
                return false;
        return true;
    }

    [DllImport("libbundle.so.0")] private static extern IntPtr bundle_create();
    [DllImport("libbundle.so.0")] private static extern void bundle_free(IntPtr bundle);
    [DllImport("libaul.so.0")] private static extern int aul_svc_set_operation(IntPtr bundle, string operation);
    [DllImport("libaul.so.0")] private static extern int aul_get_default_app(IntPtr bundle, out IntPtr app);
    [DllImport("libapp-context-manager-common.so")] private static extern byte util_is_appbooting_supported_webapp_byappid(string appId);
    [DllImport("libvconf.so.0")] private static extern IntPtr vconf_get_str(string key);
    [DllImport("libvconf.so.0")] private static extern int vconf_get_int(string key, out int value);
    [DllImport("libc")] private static extern uint getuid();
    [DllImport("libc")] private static extern void free(IntPtr ptr);

    private static string ReadVconf(string key)
    {
        IntPtr value = vconf_get_str(key);
        if (value == IntPtr.Zero) throw new Exception("Unable to read string key: " + key);
        try { return Marshal.PtrToStringAnsi(value); }
        finally { free(value); }
    }

    private static string ReadHomeOperation()
    {
        IntPtr bundle = bundle_create();
        if (bundle == IntPtr.Zero) throw new Exception("bundle_create failed");
        try
        {
            const string operation = "http://tizen.org/appcontrol/operation/main";
            if (aul_svc_set_operation(bundle, operation) != 0) throw new Exception("Unable to query main operation");
            IntPtr app;
            if (aul_get_default_app(bundle, out app) != 0 || app == IntPtr.Zero) throw new Exception("Unable to read default main app");
            try { return Marshal.PtrToStringAnsi(app); }
            finally { free(app); }
        }
        finally { bundle_free(bundle); }
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || !IsSafe(args[0]) || !IsSafe(args[1]))
                throw new Exception("Use: HomeQuery.dll FIRMWARE APP_ID");
            string firmware = args[0];
            string targetApp = args[1];
            if (IntPtr.Size != 4) throw new Exception("Expected 32-bit process; stopped");
            uint uid = getuid();
            if (uid != 901) throw new Exception("Expected SDK UID 901, got " + uid + "; stopped");
            string prd = File.ReadAllText("/etc/prd_info.ini");
            if (prd.IndexOf(firmware, StringComparison.Ordinal) < 0)
                throw new Exception("Firmware guard did not match the expected " + firmware + "; stopped");
            Console.WriteLine("QUERY_BEGIN");
            Console.WriteLine("uid=" + uid);
            Console.WriteLine("architecture_bits=" + (IntPtr.Size * 8));
            Console.WriteLine("firmware_guard=matched");
            Console.WriteLine("home_operation=" + ReadHomeOperation());
            foreach (string key in Keys) Console.WriteLine(key + "=" + ReadVconf(key));
            int auto;
            if (vconf_get_int(AutoKey, out auto) != 0) throw new Exception("Unable to read integer key: " + AutoKey);
            Console.WriteLine(AutoKey + "=" + auto);
            Console.WriteLine("launcher_boot_supported=" + util_is_appbooting_supported_webapp_byappid(targetApp));
            Console.WriteLine("QUERY_END");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("QUERY_ABORTED=" + ex);
            return 1;
        }
    }
}
