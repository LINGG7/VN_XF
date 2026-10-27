using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEditor.UnityLinker;

/// <summary>
/// ResourceManager creates generic operations through Activator.CreateInstance.
/// Preserve their constructors in WebGL players, including TapTap minigames.
/// </summary>
public sealed class AddressablesLinkerPreservation : IUnityLinkerProcessor
{
    public int callbackOrder => -1000;

    public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
    {
        if (report.summary.platform != BuildTarget.WebGL)
            return null;

        PackageInfo package = PackageInfo.FindForAssembly(typeof(AddressablesLinkerPreservation).Assembly);
        if (package == null)
            throw new BuildFailedException("Cannot locate VNovelizer's Addressables preservation rules.");

        string path = Path.Combine(package.resolvedPath, "Editor", "AddressablesPreserve.xml");
        if (!File.Exists(path))
            throw new BuildFailedException("Addressables preservation rules are missing: " + path);

        // Supply the package file directly. Never modify Addressables' generated Assets link.xml.
        UnityEngine.Debug.Log("[AddressablesLinkerPreservation] Preserving Unity.ResourceManager reflection constructors for WebGL: " + path);
        return path;
    }
}
