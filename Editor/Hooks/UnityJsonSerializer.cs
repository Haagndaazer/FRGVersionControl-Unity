using FRGVersionControl.Core.Abstractions;
using UnityEngine;

namespace FRGVersionControl.Hooks
{
    /// <summary>
    /// Foundation impl backed by Unity's JsonUtility. Adequate for flat field-based shapes
    /// (matches existing on-disk JSON formats). The full migration to System.Text.Json with
    /// fixture round-trips lands in a subsequent version once the call-site rewire is staged.
    /// </summary>
    public sealed class UnityJsonSerializer : IJsonSerializer
    {
        public string Serialize<T>(T value) => JsonUtility.ToJson(value);
        public T Deserialize<T>(string json) => JsonUtility.FromJson<T>(json);
    }
}
