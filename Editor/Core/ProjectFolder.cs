using System;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace FRGVersionControl.Core
{
    /// <summary>
    /// The Unity project folder: the parent directory of <c>Application.dataPath</c>, which Unity
    /// documents as <c>&lt;project folder&gt;/Assets</c> in the editor. Taken structurally (no string
    /// matching on "Assets") and cached on the main thread at editor load, because dataPath is a
    /// main-thread Unity API while the IPC client builds request contexts on background threads.
    /// Forward slashes.
    /// </summary>
    public static class ProjectFolder
    {
        private static volatile string s_Path;

        [InitializeOnLoadMethod]
        private static void Prime() => s_Path = FromDataPath(Application.dataPath);

        public static string Path
        {
            get
            {
                var p = s_Path;
                if (p != null) return p;
                if (!InternalEditorUtility.CurrentThreadIsMainThread())
                {
                    throw new InvalidOperationException(
                        "The Unity project folder is not known yet: it was requested off the main thread before editor load finished.");
                }
                return s_Path = FromDataPath(Application.dataPath);
            }
        }

        /// <summary>Parent folder of an Assets path, forward slashes.</summary>
        public static string FromDataPath(string dataPath)
        {
            if (string.IsNullOrEmpty(dataPath)) return dataPath;
            var trimmed = dataPath.TrimEnd('/', '\\');
            var parent = System.IO.Path.GetDirectoryName(trimmed);
            return (parent ?? trimmed).Replace('\\', '/');
        }
    }

    /// <summary>One spelling and one comparison for project-relative paths used as keys (sets,
    /// dictionaries, lookups): forward slashes, no "./" prefix or trailing slash, compared
    /// case-insensitively like the Windows file system.</summary>
    public static class PathKey
    {
        public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? "";
            var p = path.Trim().Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            if (p.Length > 1) p = p.TrimEnd('/');
            return p;
        }

        public static bool Same(string a, string b) => Comparer.Equals(Normalize(a), Normalize(b));
    }

    /// <summary>EditorPrefs scoped to this project (EditorPrefs are per machine user, shared by every
    /// project). A value saved before per-project keys existed seeds the new key once.</summary>
    public static class ProjectPrefs
    {
        public static string Key(string name) => name + "." + ProjectHash();

        private static readonly System.Collections.Generic.Dictionary<string, bool> s_Bools = new System.Collections.Generic.Dictionary<string, bool>();

        public static bool GetBool(string name, bool defaultValue)
        {
            if (s_Bools.TryGetValue(name, out var cached)) return cached;
            var key = Key(name);
            if (!EditorPrefs.HasKey(key) && EditorPrefs.HasKey(name)) EditorPrefs.SetBool(key, EditorPrefs.GetBool(name, defaultValue));
            var value = EditorPrefs.GetBool(key, defaultValue);
            s_Bools[name] = value;
            return value;
        }

        public static void SetBool(string name, bool value)
        {
            EditorPrefs.SetBool(Key(name), value);
            s_Bools[name] = value;
        }

        public static int GetInt(string name, int defaultValue)
        {
            var key = Key(name);
            if (!EditorPrefs.HasKey(key) && EditorPrefs.HasKey(name)) EditorPrefs.SetInt(key, EditorPrefs.GetInt(name, defaultValue));
            return EditorPrefs.GetInt(key, defaultValue);
        }

        public static void SetInt(string name, int value) => EditorPrefs.SetInt(Key(name), value);

        public static string GetString(string name, string defaultValue)
        {
            var key = Key(name);
            if (!EditorPrefs.HasKey(key) && EditorPrefs.HasKey(name)) EditorPrefs.SetString(key, EditorPrefs.GetString(name, defaultValue));
            return EditorPrefs.GetString(key, defaultValue);
        }

        public static void SetString(string name, string value) => EditorPrefs.SetString(Key(name), value);

        private static string s_Hash;

        internal static string ProjectHash()
        {
            if (s_Hash != null) return s_Hash;
            return s_Hash = HashOf(ProjectFolder.Path);
        }

        internal static string HashOf(string folder)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes((folder ?? "").Replace('\\', '/').TrimEnd('/').ToLowerInvariant()));
                return BitConverter.ToString(bytes, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
