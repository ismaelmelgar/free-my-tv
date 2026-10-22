// Verifies, applies or restores the Samsung Home assignment, run on the TV as the SDK user (uid 901).
//
// Adapted from NelsonEU/arn0-tv native/Control.cs, Copyright (c) 2026 Arnaud Etienne, MIT License:
// see LICENSE-arn0 next to this file.
// Changes: target app, package and expected firmware come from the command line; the saved original values
// (the per-device baseline, captured by `home_button.py query --save-baseline`) are passed as arguments instead
// of being read from JSON, so no JSON runtime is needed on the TV.
//
//   dotnet --globalizationinvariant HomeControl.dll query   FIRMWARE APP_ID PACKAGE
//   dotnet --globalizationinvariant HomeControl.dll verify  FIRMWARE APP_ID PACKAGE HOME HOME_APPID STARTUP_APPID BOOTING_BACKUP FIRST_SCREEN AUTOSTART
//   (same arguments for apply and restore)
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

internal static class Control
{
    private const string MainOperation = "http://tizen.org/appcontrol/operation/main";
    private const string AutoKey = "db/menu/smart_hub/ontvsetting/auto_start";
    private const string HomeKey = "db/appfw/home_appid";
    private static readonly string[] Keys = {
        HomeKey,
        "db/appfw/startup_appid",
        "db/appfw/booting_appid_backup",
        "db/first_screen/type"
    };
    // Letters, digits, dot, underscore and dash only: what Tizen app ids and firmware names use.
    private static bool IsSafe(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (char c in value)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'))
                return false;
        return true;
    }

    private static string Target;
    private static string TargetPackage;

    [DllImport("libbundle.so.0")] private static extern IntPtr bundle_create();
    [DllImport("libbundle.so.0")] private static extern void bundle_free(IntPtr bundle);
    [DllImport("libaul.so.0")] private static extern int aul_svc_set_operation(IntPtr bundle, string operation);
    [DllImport("libaul.so.0")] private static extern int aul_get_default_app(IntPtr bundle, out IntPtr app);
    [DllImport("libapp-context-manager-common.so")] private static extern byte util_is_appbooting_supported_webapp_byappid(string appId);
    [DllImport("libcapi-appfw-app-operation-tv.so.0")] private static extern int app_operation_set_current_app(string operation, string appId);
    [DllImport("libvconf.so.0")] private static extern IntPtr vconf_get_str(string key);
    [DllImport("libvconf.so.0")] private static extern int vconf_set_str(string key, string value);
    [DllImport("libvconf.so.0")] private static extern int vconf_get_int(string key, out int value);
    [DllImport("libvconf.so.0")] private static extern int vconf_set_int(string key, int value);
    [DllImport("libc")] private static extern uint getuid();
    [DllImport("libc")] private static extern void free(IntPtr ptr);

    private sealed class Snapshot
    {
        public string Home { get; set; }
        public Dictionary<string, string> Strings { get; set; }
        public int Autostart { get; set; }
    }

    private static string Safe(string value, string what)
    {
        if (!IsSafe(value)) throw new Exception("Invalid " + what + "; stopped");
        return value;
    }

    private static void Guard(string firmware)
    {
        if (IntPtr.Size != 4) throw new Exception("Expected 32-bit process; stopped");
        if (getuid() != 901) throw new Exception("Expected SDK UID 901; stopped");
        string prd = File.ReadAllText("/etc/prd_info.ini");
        if (prd.IndexOf(firmware, StringComparison.Ordinal) < 0)
            throw new Exception("Firmware guard mismatch (expected " + firmware + "); stopped");
    }

    private static string ReadString(string key)
    {
        IntPtr ptr = vconf_get_str(key);
        if (ptr == IntPtr.Zero) throw new Exception("Cannot read " + key);
        try { return Marshal.PtrToStringAnsi(ptr); }
        finally { free(ptr); }
    }

    private static string ReadHome()
    {
        IntPtr bundle = bundle_create();
        if (bundle == IntPtr.Zero) throw new Exception("bundle_create failed");
        try
        {
            if (aul_svc_set_operation(bundle, MainOperation) != 0)
                throw new Exception("Cannot set Home query operation");
            IntPtr app;
            if (aul_get_default_app(bundle, out app) != 0 || app == IntPtr.Zero)
                throw new Exception("Cannot read Home operation");
            try { return Marshal.PtrToStringAnsi(app); }
            finally { free(app); }
        }
        finally { bundle_free(bundle); }
    }

    private static Snapshot Query()
    {
        var s = new Snapshot { Home = ReadHome(), Strings = new Dictionary<string, string>() };
        foreach (string key in Keys) s.Strings[key] = ReadString(key);
        int autostart;
        if (vconf_get_int(AutoKey, out autostart) != 0) throw new Exception("Cannot read " + AutoKey);
        s.Autostart = autostart;
        return s;
    }

    // The baseline is what this TV reported before any change; it is never synthesized from defaults.
    // A baseline that already names the target app was captured after a change and is refused.
    private static Snapshot LoadSnapshot(string[] args)
    {
        var s = new Snapshot {
            Home = Safe(args[4], "baseline home operation"),
            Strings = new Dictionary<string, string>()
        };
        for (int i = 0; i < Keys.Length; i++) s.Strings[Keys[i]] = Safe(args[5 + i], "baseline " + Keys[i]);
        int autostart;
        if (!int.TryParse(args[9], out autostart)) throw new Exception("Invalid baseline autostart; stopped");
        s.Autostart = autostart;
        if (s.Home == Target) throw new Exception("Baseline already names the target app as Home; not an original");
        foreach (string key in Keys)
            if (s.Strings[key] == Target) throw new Exception("Baseline already names the target app: " + key);
        return s;
    }

    private static string Format(Snapshot s)
    {
        string result = "home_operation=" + s.Home;
        foreach (string key in Keys) result += "\n" + key + "=" + s.Strings[key];
        return result + "\n" + AutoKey + "=" + s.Autostart;
    }

    private static void SetString(string key, string value)
    {
        if (vconf_set_str(key, value) != 0 || ReadString(key) != value)
            throw new Exception("Write/readback failed: " + key);
    }

    private static void SetInt(string key, int value)
    {
        int actual;
        if (vconf_set_int(key, value) != 0 || vconf_get_int(key, out actual) != 0 || actual != value)
            throw new Exception("Write/readback failed: " + key);
    }

    private static void SetHome(string appId)
    {
        if (app_operation_set_current_app(MainOperation, appId) != 0 || ReadHome() != appId)
            throw new Exception("Home operation write/readback failed");
    }

    private static void Restore(Snapshot s)
    {
        SetHome(s.Home);
        foreach (string key in Keys) SetString(key, s.Strings[key]);
        SetInt(AutoKey, s.Autostart);
    }

    private static void RequireOriginal(Snapshot before, Snapshot original)
    {
        if (before.Home != original.Home) throw new Exception("Home operation differs from saved original");
        foreach (string key in Keys)
            if (before.Strings[key] != original.Strings[key])
                throw new Exception("Setting differs from saved original: " + key);
        if (before.Autostart != original.Autostart)
            throw new Exception("Autostart differs from saved original");
    }

    private static void RequireRestorable(Snapshot before, Snapshot original)
    {
        if (before.Home != original.Home && before.Home != Target)
            throw new Exception("Home operation changed to an unexpected app; refusing restore");
        foreach (string key in Keys)
            if (before.Strings[key] != original.Strings[key] && before.Strings[key] != Target)
                throw new Exception("Setting changed unexpectedly; refusing restore: " + key);
        if (before.Autostart != original.Autostart)
            throw new Exception("Autostart changed unexpectedly; refusing restore");
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 4) throw new Exception("Use query FIRMWARE APP PACKAGE, or verify|apply|restore FIRMWARE APP PACKAGE <baseline values>");
            string firmware = Safe(args[1], "firmware");
            Target = Safe(args[2], "app id");
            TargetPackage = Safe(args[3], "package");
            Guard(firmware);
            if (args.Length == 4 && args[0] == "query")
            {
                Console.WriteLine("HOME_QUERY=" + Format(Query()));
                return 0;
            }
            if (args.Length != 10 || (args[0] != "apply" && args[0] != "restore" && args[0] != "verify"))
                throw new Exception("Use query FIRMWARE APP PACKAGE, or verify|apply|restore FIRMWARE APP PACKAGE <baseline values>");
            Snapshot original = LoadSnapshot(args);
            Snapshot before = Query();
            if (args[0] == "verify")
            {
                RequireOriginal(before, original);
                Console.WriteLine("HOME_BASELINE_MATCH=" + Format(before));
                return 0;
            }
            if (args[0] == "restore")
            {
                RequireRestorable(before, original);
                Restore(original);
                Console.WriteLine("HOME_RESTORED=" + Format(Query()));
                return 0;
            }

            RequireOriginal(before, original);
            string appDir = "/opt/usr/home/owner/apps_rw/" + TargetPackage;
            if (!Directory.Exists(appDir)) throw new Exception("Expected launcher package directory missing");
            if (util_is_appbooting_supported_webapp_byappid(Target) == 0)
                throw new Exception("TV says launcher is not eligible for boot; stopped");

            try
            {
                SetHome(Target);
                SetString(HomeKey, Target);
                SetInt(AutoKey, original.Autostart);
                Snapshot after = Query();
                if (after.Home != Target || after.Strings[HomeKey] != Target ||
                    after.Autostart != original.Autostart)
                    throw new Exception("Applied values failed verification");
                Console.WriteLine("HOME_APPLIED=" + Format(after));
                return 0;
            }
            catch
            {
                try
                {
                    Restore(original);
                    Console.Error.WriteLine("Original values restored and verified after apply failure");
                }
                catch (Exception restoreError)
                {
                    Console.Error.WriteLine("URGENT: automatic restore failed: " + restoreError);
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("HOME_ABORTED=" + ex);
            return 1;
        }
    }
}
