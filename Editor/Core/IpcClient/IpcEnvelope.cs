using System;

namespace FRGVersionControl.Core.IpcClient
{
    /// <summary>
    /// JSON envelope shapes for one-shot svn-core.exe IPC. Designed for Unity's JsonUtility,
    /// which serializes public fields only and does not support dictionaries — so per-op
    /// `args` and `result` shapes are op-specific subclasses, not generic containers.
    /// </summary>
    [Serializable]
    public class IpcRequestBase
    {
        public int v = 1;
        public string id;
        public string op;
        public IpcRequestContext ctx;
    }

    [Serializable]
    public sealed class IpcRequestContext
    {
        public string wc;
        public string configDir;
        public string username;
        public string[] certTrust;
        public string svnBinaryHint;
    }

    /// <summary>Request payload for ops that take no args (meta.version, override.list, etc.).</summary>
    [Serializable]
    public sealed class IpcRequestNoArgs : IpcRequestBase
    {
        public IpcEmptyArgs args = new IpcEmptyArgs();
    }

    [Serializable]
    public sealed class IpcEmptyArgs { }

    [Serializable]
    public sealed class IpcResponseEnvelope
    {
        public int v;
        public string id;
        // ok serialized as "true"/"false"; absent on progress frames. We capture into a
        // string-ish nullable via a flag pattern since JsonUtility can't model nullable bool.
        public bool ok;
        public IpcResponseError error;
        public IpcResponseProgress progress;
        // `result` is op-specific; deserialize a second time with op-specific class against
        // the raw line if needed.
    }

    [Serializable]
    public sealed class IpcResponseError
    {
        public string svnCode;
        public string kind;
        public string message;
        public string defaultAction;
        public bool retriable;
    }

    [Serializable]
    public sealed class IpcResponseProgress
    {
        public string phase;
        public long current;
        public long total;
        public string msg;
    }

    /// <summary>Result shape for meta.version.</summary>
    [Serializable]
    public sealed class MetaVersionResult
    {
        public string coreVersion;
        public string svnVersion;
        public int wireVersion;
    }

    /// <summary>Wrapper used to round-trip a typed result through JsonUtility.</summary>
    [Serializable]
    public sealed class IpcResponseWithMetaVersion
    {
        public int v;
        public string id;
        public bool ok;
        public MetaVersionResult result;
        public IpcResponseError error;
    }
}
