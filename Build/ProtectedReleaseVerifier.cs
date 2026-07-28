using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Xml;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ProtectedReleaseVerifier
{
    private static readonly string[] ForbiddenText =
    {
        "NetworkAuthEnabled",
        "Network auth disabled",
        "Auth bypassed"
    };

    private static readonly HashSet<string> UnityMessageNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake", "Start", "Update", "LateUpdate", "FixedUpdate",
            "OnGUI", "OnEnable", "OnDisable", "OnDestroy",
            "OnApplicationQuit", "OnApplicationFocus", "OnApplicationPause",
            "OnLevelWasLoaded", "OnDrawGizmos", "OnDrawGizmosSelected",
            "OnValidate", "Reset", "OnAnimatorIK", "OnAnimatorMove",
            "OnAudioFilterRead", "OnBecameInvisible", "OnBecameVisible",
            "OnCollisionEnter", "OnCollisionEnter2D", "OnCollisionExit",
            "OnCollisionExit2D", "OnCollisionStay", "OnCollisionStay2D",
            "OnControllerColliderHit", "OnJointBreak", "OnJointBreak2D",
            "OnMouseDown", "OnMouseDrag", "OnMouseEnter", "OnMouseExit",
            "OnMouseOver", "OnMouseUp", "OnMouseUpAsButton",
            "OnParticleCollision", "OnPostRender", "OnPreCull", "OnPreRender",
            "OnRenderImage", "OnRenderObject", "OnServerInitialized",
            "OnTransformChildrenChanged", "OnTransformParentChanged",
            "OnTriggerEnter", "OnTriggerEnter2D", "OnTriggerExit",
            "OnTriggerExit2D", "OnTriggerStay", "OnTriggerStay2D",
            "OnWillRenderObject"
        };

    private static readonly string[] HarmonyConventionNames =
    {
        "TargetMethod", "Prefix", "Postfix", "Transpiler"
    };

    private static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine(
                "Usage: ProtectedReleaseVerifier <raw> <protected> <mapping> <version>");
            return 2;
        }

        try
        {
            AssemblyDefinition raw = AssemblyDefinition.ReadAssembly(args[0]);
            AssemblyDefinition protectedAssembly = AssemblyDefinition.ReadAssembly(args[1]);

            VerifyAssembly(raw, "raw", args[3]);
            VerifyAssembly(protectedAssembly, "protected", args[3]);
            VerifyAuthorizationGate(raw, "raw");
            VerifyAuthorizationGate(protectedAssembly, "protected");
            VerifyEntryPoint(protectedAssembly);
            VerifyConventionMethods(raw, protectedAssembly);
            VerifyObfuscation(args[0], args[1], args[2]);

            Console.WriteLine("Protected release IL verification passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Protected release verification failed: " + exception.Message);
            return 1;
        }
    }

    private static void VerifyAssembly(
        AssemblyDefinition assembly,
        string label,
        string expectedVersion)
    {
        Require(
            string.Equals(
                assembly.Name.Version.ToString(),
                expectedVersion,
                StringComparison.Ordinal),
            label + " assembly version is " + assembly.Name.Version +
            ", expected " + expectedVersion + ".");
        Require(
            assembly.MainModule.Runtime == TargetRuntime.Net_2_0 &&
            assembly.MainModule.RuntimeVersion.StartsWith(
                "v2.0",
                StringComparison.OrdinalIgnoreCase),
            label + " assembly no longer targets CLR 2.0: " +
            assembly.MainModule.Runtime + " / " + assembly.MainModule.RuntimeVersion + ".");
        Require(
            !HasUnsafeDebugging(assembly.CustomAttributes) &&
            !HasUnsafeDebugging(assembly.MainModule.CustomAttributes),
            label + " assembly enables JIT tracking, edit-and-continue, or " +
            "disabled optimizations through DebuggableAttribute.");

        foreach (TypeDefinition type in AllTypes(assembly.MainModule))
        {
            VerifyText(type.FullName, label + " type");
            foreach (FieldDefinition field in type.Fields)
            {
                VerifyText(field.Name, label + " field");
            }
            foreach (MethodDefinition method in type.Methods)
            {
                VerifyText(method.Name, label + " method");
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (Instruction instruction in method.Body.Instructions)
                {
                    string literal = instruction.Operand as string;
                    if (literal != null)
                    {
                        VerifyText(literal, label + " string literal");
                    }
                }
            }
        }
    }

    private static void VerifyAuthorizationGate(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition consoleManager = FindType(assembly.MainModule, "ConsoleManager");
        Require(consoleManager != null, label + " ConsoleManager type is missing.");

        foreach (FieldDefinition field in consoleManager.Fields)
        {
            Require(
                !string.Equals(
                    field.Name,
                    "NetworkAuthEnabled",
                    StringComparison.Ordinal),
                label + " ConsoleManager still contains NetworkAuthEnabled.");
        }

        MethodDefinition start = FindMethod(consoleManager, "Start");
        Require(start != null && start.HasBody, label + " ConsoleManager.Start is missing.");
        foreach (Instruction instruction in start.Body.Instructions)
        {
            MethodReference called = instruction.Operand as MethodReference;
            if (called == null)
            {
                continue;
            }

            Require(
                !string.Equals(called.Name, "BootCheatMain", StringComparison.Ordinal) &&
                !string.Equals(called.Name, "InstallAuthorized", StringComparison.Ordinal),
                label + " ConsoleManager.Start directly calls " + called.FullName +
                "; authorization must complete through the login callback.");
        }
    }

    private static void VerifyEntryPoint(AssemblyDefinition assembly)
    {
        TypeDefinition entrypoint = FindType(assembly.MainModule, "Doorstop.Entrypoint");
        Require(entrypoint != null, "Protected Doorstop.Entrypoint is missing.");
        MethodDefinition startInjected = FindMethod(entrypoint, "StartInjected");
        Require(
            startInjected != null && startInjected.IsStatic && startInjected.IsPublic,
            "Protected Doorstop.Entrypoint.StartInjected must remain public and static.");
    }

    private static void VerifyConventionMethods(
        AssemblyDefinition raw,
        AssemblyDefinition protectedAssembly)
    {
        Dictionary<string, int> rawUnity = CountUnityMessages(raw.MainModule);
        Dictionary<string, int> protectedUnity = CountUnityMessages(protectedAssembly.MainModule);
        foreach (KeyValuePair<string, int> pair in rawUnity)
        {
            int protectedCount;
            protectedUnity.TryGetValue(pair.Key, out protectedCount);
            Require(
                pair.Value == protectedCount,
                "Unity message " + pair.Key + " count changed from " + pair.Value +
                " to " + protectedCount + ".");
        }

        foreach (string name in HarmonyConventionNames)
        {
            int rawCount = CountMethods(raw.MainModule, name);
            int protectedCount = CountMethods(protectedAssembly.MainModule, name);
            Require(rawCount > 0, "Raw Harmony convention method is missing: " + name + ".");
            Require(
                rawCount == protectedCount,
                "Harmony convention method " + name + " count changed from " +
                rawCount + " to " + protectedCount + ".");
        }
    }

    private static void VerifyObfuscation(
        string rawPath,
        string protectedPath,
        string mappingPath)
    {
        Require(
            !HashesEqual(rawPath, protectedPath),
            "Protected assembly is byte-identical to the raw assembly.");

        XmlDocument mapping = new XmlDocument();
        mapping.Load(mappingPath);
        int renamed = 0;
        XmlNodeList elements = mapping.SelectNodes("//*");
        foreach (XmlNode element in elements)
        {
            if (element.Attributes == null)
            {
                continue;
            }

            string status = GetAttribute(element, "status");
            string oldName = GetAttribute(element, "oldName");
            string newName = GetAttribute(element, "newName");
            if ((!string.IsNullOrEmpty(status) &&
                 string.Equals(status, "renamed", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(oldName) && !string.IsNullOrEmpty(newName) &&
                 !string.Equals(oldName, newName, StringComparison.Ordinal)))
            {
                renamed++;
            }
        }

        Require(
            renamed >= 10,
            "Obfuscation mapping contains only " + renamed +
            " renamed items; expected at least 10.");
    }

    private static Dictionary<string, int> CountUnityMessages(ModuleDefinition module)
    {
        Dictionary<string, TypeDefinition> byName = new Dictionary<string, TypeDefinition>(
            StringComparer.Ordinal);
        foreach (TypeDefinition type in AllTypes(module))
        {
            byName[type.FullName] = type;
        }

        Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (TypeDefinition type in AllTypes(module))
        {
            if (!DerivesFromMonoBehaviour(type, byName))
            {
                continue;
            }

            foreach (MethodDefinition method in type.Methods)
            {
                if (!UnityMessageNames.Contains(method.Name))
                {
                    continue;
                }

                int count;
                result.TryGetValue(method.Name, out count);
                result[method.Name] = count + 1;
            }
        }
        return result;
    }

    private static bool DerivesFromMonoBehaviour(
        TypeDefinition type,
        IDictionary<string, TypeDefinition> byName)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        TypeReference current = type.BaseType;
        while (current != null && seen.Add(current.FullName))
        {
            if (string.Equals(
                current.FullName,
                "UnityEngine.MonoBehaviour",
                StringComparison.Ordinal))
            {
                return true;
            }

            TypeDefinition local;
            if (!byName.TryGetValue(current.FullName, out local))
            {
                break;
            }
            current = local.BaseType;
        }
        return false;
    }

    private static int CountMethods(ModuleDefinition module, string name)
    {
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module))
        {
            foreach (MethodDefinition method in type.Methods)
            {
                if (string.Equals(method.Name, name, StringComparison.Ordinal))
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static bool HasUnsafeDebugging(IEnumerable<CustomAttribute> attributes)
    {
        foreach (CustomAttribute attribute in attributes)
        {
            if (!string.Equals(
                attribute.AttributeType.FullName,
                "System.Diagnostics.DebuggableAttribute",
                StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Count == 1)
            {
                int modes = Convert.ToInt32(attribute.ConstructorArguments[0].Value);
                const int Default = 1;
                const int EnableEditAndContinue = 4;
                const int DisableOptimizations = 256;
                return (modes & (Default | EnableEditAndContinue | DisableOptimizations)) != 0;
            }
            if (attribute.ConstructorArguments.Count == 2)
            {
                bool jitTracking = Convert.ToBoolean(
                    attribute.ConstructorArguments[0].Value);
                bool disableOptimizations = Convert.ToBoolean(
                    attribute.ConstructorArguments[1].Value);
                return jitTracking || disableOptimizations;
            }

            return true;
        }
        return false;
    }

    private static string GetAttribute(XmlNode node, string name)
    {
        if (node.Attributes == null)
        {
            return null;
        }
        foreach (XmlAttribute attribute in node.Attributes)
        {
            if (string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Value;
            }
        }
        return null;
    }

    private static void VerifyText(string value, string source)
    {
        if (value == null)
        {
            return;
        }
        foreach (string forbidden in ForbiddenText)
        {
            Require(
                value.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0,
                source + " contains forbidden authorization bypass text: " + forbidden + ".");
        }
    }

    private static TypeDefinition FindType(ModuleDefinition module, string fullName)
    {
        foreach (TypeDefinition type in AllTypes(module))
        {
            if (string.Equals(type.FullName, fullName, StringComparison.Ordinal))
            {
                return type;
            }
        }
        return null;
    }

    private static MethodDefinition FindMethod(TypeDefinition type, string name)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (string.Equals(method.Name, name, StringComparison.Ordinal))
            {
                return method;
            }
        }
        return null;
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (TypeDefinition type in module.Types)
        {
            foreach (TypeDefinition nested in AllTypes(type))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type)
    {
        yield return type;
        foreach (TypeDefinition nested in type.NestedTypes)
        {
            foreach (TypeDefinition descendant in AllTypes(nested))
            {
                yield return descendant;
            }
        }
    }

    private static bool HashesEqual(string leftPath, string rightPath)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] left;
            byte[] right;
            using (FileStream stream = File.OpenRead(leftPath))
            {
                left = sha256.ComputeHash(stream);
            }
            using (FileStream stream = File.OpenRead(rightPath))
            {
                right = sha256.ComputeHash(stream);
            }

            if (left.Length != right.Length)
            {
                return false;
            }
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
