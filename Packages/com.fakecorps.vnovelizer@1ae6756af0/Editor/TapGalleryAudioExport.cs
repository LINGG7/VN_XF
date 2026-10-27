using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

[InitializeOnLoad]
public static class TapGalleryAudioExport
{
    private const string ModuleName = "vnovelizer-gallery-audio.js";
    private const string BootRequire = "require(\"./vnovelizer-gallery-audio\");";
    private static Type converter;
    private static Delegate registeredHandler;
    private static DateTime nextRegistrationAttempt;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    static TapGalleryAudioExport()
    {
        EditorApplication.delayCall += Register;
        EditorApplication.update += RegisterWhenAvailable;
        AssemblyReloadEvents.beforeAssemblyReload += Unregister;
    }

    private static void RegisterWhenAvailable()
    {
        if (DateTime.UtcNow < nextRegistrationAttempt) return;
        nextRegistrationAttempt = DateTime.UtcNow.AddSeconds(1);
        Register();
    }

    private static void Register()
    {
        if (registeredHandler != null) return;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            converter = assembly.GetType("minihost.editor.TJConvertCore", false);
            if (converter != null) break;
        }
        if (converter == null)
        {
            try { converter = Assembly.Load("minihost-plugin.editor").GetType("minihost.editor.TJConvertCore", false); }
            catch (FileNotFoundException) { return; }
            catch (FileLoadException) { return; }
        }
        if (converter == null) return;
        MethodInfo method = converter.GetMethod("RegisterPostProcessHandler", BindingFlags.Static | BindingFlags.Public);
        if (method == null) return;
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != 1 || !typeof(Delegate).IsAssignableFrom(parameters[0].ParameterType))
            throw new InvalidOperationException("Unsupported TapTap conversion callback signature.");
        Type callbackType = parameters[0].ParameterType;
        MethodInfo invoke = callbackType.GetMethod("Invoke");
        if (invoke == null || invoke.ReturnType != typeof(void) || invoke.GetParameters().Length != 0)
            throw new InvalidOperationException("Unsupported TapTap conversion callback arguments.");
        // The SDK uses its own PostProcessHandler delegate, rather than System.Action.
        Delegate handler = Delegate.CreateDelegate(callbackType,
            typeof(TapGalleryAudioExport).GetMethod(nameof(OnConverted), BindingFlags.Static | BindingFlags.NonPublic));
        method.Invoke(null, new object[] { handler });
        registeredHandler = handler;
        EditorApplication.update -= RegisterWhenAvailable;
        Debug.Log("[TapGalleryAudio] Registered conversion hook (before TapTap ZIP packaging).");
    }

    private static void Unregister()
    {
        EditorApplication.update -= RegisterWhenAvailable;
        if (registeredHandler == null) return;
        converter?.GetMethod("UnregisterPostProcessHandler", BindingFlags.Static | BindingFlags.Public,
            null, new[] { registeredHandler.GetType() }, null)?.Invoke(null, new object[] { registeredHandler });
        registeredHandler = null;
    }
    private static object ReadMember(object value, string name)
    {
        if (value == null) return null;
        Type type = value.GetType();
        return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value);
    }

    private static void OnConverted()
    {
        object config = converter.GetField("config", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        string output = ReadMember(ReadMember(config, "ProjectConf"), "DST") as string;
        string minigame = converter.GetField("miniGameDir", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as string;
        string root = Directory.GetParent(Application.dataPath).FullName;
        PackageInfo package = PackageInfo.FindForAssembly(typeof(TapGalleryAudioExport).Assembly);
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(minigame) || package == null)
            throw new InvalidOperationException("TapTap output configuration or VNovelizer package is unavailable.");
        PatchOutput(root, Path.Combine(output, minigame),
            Path.Combine(package.resolvedPath, "Editor", "TapGalleryAudioRuntime.js.txt"));
        Debug.Log("[TapGalleryAudio] v4 boot module exported; include it in the minigame package.");
    }

    // Only named files in the configured generated output are written. No project cleanup.
    public static void PatchOutput(string projectRoot, string minigamePath, string runtimeTemplate)
    {
        string root = Path.GetFullPath(projectRoot);
        string output = Path.GetFullPath(minigamePath);
        EnsureSafeOutput(root, output);
        string gamePath = Path.Combine(output, "game.js");
        string modulePath = Path.Combine(output, ModuleName);
        string frameworkPath = Path.Combine(output, "webgl.wasm.framework.unityweb.js");
        EnsureSafeOutput(root, gamePath);
        EnsureSafeOutput(root, modulePath);
        EnsureSafeOutput(root, frameworkPath);
        if (!File.Exists(Path.Combine(output, "unity-sdk", "audio", "unity-audio.js")))
            throw new InvalidOperationException("Converted TapTap audio SDK not found.");
        string game = File.ReadAllText(gamePath);
        if (!game.Contains(BootRequire))
        {
            const string start = "gameManager.startGame()";
            int at = game.IndexOf(start, StringComparison.Ordinal);
            if (at < 0 || game.IndexOf(start, at + start.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("Expected exactly one TapTap gameManager.startGame() entry.");
            game = game.Insert(at, "/* VNovelizer gallery audio boot */" + BootRequire);
        }
        string runtime = File.ReadAllText(runtimeTemplate);
        if (!runtime.StartsWith("// VNovelizer managed TapTap gallery audio bridge", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected gallery runtime template.");
        if (File.Exists(modulePath) && !File.ReadAllText(modulePath).StartsWith("// VNovelizer managed TapTap gallery audio bridge", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to replace an unrelated generated module.");
        string framework = File.Exists(frameworkPath) ? File.ReadAllText(frameworkPath) : null;
        string patched = framework == null ? null : PatchLegacyFramework(framework);
        File.WriteAllText(modulePath, runtime, Utf8);
        File.WriteAllText(gamePath, game, Utf8);
        if (framework != patched) File.WriteAllText(frameworkPath, patched, Utf8);
    }

    // Existing builds keep the same native ABI; replace only the old helper's object.
    public static string PatchLegacyFramework(string source)
    {
        const string declaration = "var VNovelizer_TapGalleryAudio=";
        int at = source.IndexOf(declaration, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("Gallery bridge is missing from this build.");
        int open = at + declaration.Length;
        if (source[open] != '{') throw new InvalidOperationException("Unexpected gallery bridge declaration.");
        int level = 0;
        char quote = '\0';
        bool escaped = false;
        int end = -1;
        for (int i = open; i < source.Length; i++)
        {
            char ch = source[i];
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (ch == '\\') { escaped = true; continue; }
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch == '"' || ch == '\'') { quote = ch; continue; }
            if (ch == '{') level++;
            else if (ch == '}' && --level == 0) { end = i + 1; break; }
        }
        if (end < 0) throw new InvalidOperationException("Unclosed gallery bridge object.");
        string current = source.Substring(open, end - open);
        if (!current.Contains("prototype.setup") && !current.Contains("require(\"./unity-sdk/audio/unity-audio\")")) return source;
        const string replacement = "{runtime:function(){return typeof GameGlobal!=='undefined'?GameGlobal.VNovelizerTapGalleryAudio:null},begin:function(t){var r=this.runtime();return r?r.begin(t):0},end:function(){var r=this.runtime();if(r)r.end()},position:function(){var r=this.runtime();return r?r.position():-1},reset:function(){var r=this.runtime();if(r)r.reset()}}";
        return source.Substring(0, open) + replacement + source.Substring(end);
    }

    private static void EnsureSafeOutput(string root, string path)
    {
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Generated TapTap output must remain inside this project.");
        string relative = path.Substring(root.Length + 1);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (segment.Equals("Assets", StringComparison.OrdinalIgnoreCase) || segment.Equals("Temp", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Library", StringComparison.OrdinalIgnoreCase) || segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Protected project path cannot be used for generated output.");
        string current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse points cannot be used for generated output.");
            current = Path.GetDirectoryName(current);
        }
    }
}