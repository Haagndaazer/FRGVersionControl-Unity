namespace FRGVersionControl.Core.Abstractions
{
    /// <summary>
    /// Logger seam for engine-agnostic core code. Named IFrgLogger (rather than ILogger)
    /// to avoid a name collision with UnityEngine.ILogger that fires in every Unity-side
    /// file that has `using UnityEngine;`.
    /// </summary>
    public interface IFrgLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Error(string message, System.Exception exception);
    }
}
