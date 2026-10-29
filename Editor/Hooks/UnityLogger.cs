using FRGVersionControl.Core.Abstractions;
using UnityEngine;

namespace FRGVersionControl.Hooks
{
    public sealed class UnityLogger : IFrgLogger
    {
        private readonly string m_Tag;

        public UnityLogger(string tag = "FRGVersionControl") { m_Tag = tag; }

        public void Info(string message) => Debug.Log($"[{m_Tag}] {message}");
        public void Warn(string message) => Debug.LogWarning($"[{m_Tag}] {message}");
        public void Error(string message) => Debug.LogError($"[{m_Tag}] {message}");
        public void Error(string message, System.Exception exception) =>
            Debug.LogError($"[{m_Tag}] {message}\n{exception}");
    }
}
