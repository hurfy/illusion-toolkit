namespace Illusion.Domain.Materials;

/// <summary>One known shader parameter code: the id ("D013"), its friendly name, and the canonical float
/// count observed in the loaded libraries (null when no loaded material carries the code — then the
/// payload length is unverifiable and a creation accepts what the user enters).</summary>
public sealed record ParamDescriptor(string Id, string Display, int? Length);
