namespace FRGVersionControl.Core.Abstractions
{
    public interface IProjectPaths
    {
        string WorkingCopyRoot { get; }
        string AssetsRoot { get; }
        string LibraryRoot { get; }
        string ToolingConfigDir { get; }
    }
}
